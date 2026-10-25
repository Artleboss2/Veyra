using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace Arm64Lab
{
    /// <summary>
    /// A deliberately small AArch64 interpreter: enough opcodes to run the
    /// hand-assembled guest.so in this folder, nothing more. It exists to put a
    /// number on the first step of a native bridge, not to run Android apps.
    /// </summary>
    internal static class Program
    {
        private const ulong GuestBase = 0x10000000;
        private const ulong TextAddr = GuestBase + 0x1000;
        private const ulong Scratch = GuestBase + 0x2000;

        private static int Main(string[] args)
        {
            string dir = "build/arm64-lab";
            if (Value(args, "--dir") != null) dir = Value(args, "--dir");

            string benchArg = Value(args, "--bench");
            try
            {
                Elf elf = Elf.Load(Path.Combine(dir, "guest.so"), GuestBase);
                Cpu cpu = new Cpu(elf);

                if (benchArg != null)
                {
                    ulong n = ulong.Parse(benchArg, CultureInfo.InvariantCulture);
                    return Bench(cpu, elf, n, dir);
                }
                return Selftest(cpu, elf, dir);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("failed: " + ex.Message);
                return 2;
            }
        }

        private static int Selftest(Cpu cpu, Elf elf, string dir)
        {
            // One data buffer the guest can walk: the 64-bit words 1..8.
            for (int i = 0; i < 8; i++)
                cpu.Memory.WriteU64(Scratch + (ulong)(i * 8), (ulong)(i + 1));

            Dictionary<string, ulong> got = new Dictionary<string, ulong>();
            got["sum_words"] = cpu.Call(elf.Symbol("sum_words"), Scratch, 8);
            got["fib"] = cpu.Call(elf.Symbol("fib"), 30);
            got["mix"] = cpu.Call(elf.Symbol("mix"), 7, 5);
            got["host_sum"] = cpu.Call(elf.Symbol("host_sum"), 19, 23);

            Dictionary<string, ulong> want = new Dictionary<string, ulong>();
            want["sum_words"] = 36;
            want["fib"] = 832040;
            want["mix"] = 30;
            want["host_sum"] = 42;

            bool ok = true;
            StringBuilder rows = new StringBuilder();
            foreach (string key in new string[] { "sum_words", "fib", "mix", "host_sum" })
            {
                bool pass = got[key] == want[key];
                ok &= pass;
                rows.AppendFormat(CultureInfo.InvariantCulture,
                    "    {{\"name\": \"{0}\", \"expected\": {1}, \"actual\": {2}, \"pass\": {3}}}{4}\n",
                    key, want[key], got[key], pass ? "true" : "false",
                    key == "host_sum" ? "" : ",");
            }

            string json = "{\n" +
                "  \"ok\": " + (ok ? "true" : "false") + ",\n" +
                "  \"instructionsExecuted\": " + cpu.Executed + ",\n" +
                "  \"elfBytes\": " + elf.FileBytes + ",\n" +
                "  \"textBytes\": " + elf.TextBytes + ",\n" +
                "  \"symbols\": " + elf.Symbols.Count + ",\n" +
                "  \"results\": [\n" + rows + "  ]\n" +
                "}\n";
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "interp.json"), json, Encoding.UTF8);
            Console.Write(json);
            return ok ? 0 : 1;
        }

        private static int Bench(Cpu cpu, Elf elf, ulong n, string dir)
        {
            // Sustained interpretation throttles this 15 W laptop, so each side
            // runs three times and the fastest run is reported: the number is
            // throughput, not thermal state.
            const int Trials = 3;
            ulong entry = elf.Symbol("fib");
            ulong value = 0;
            long instructions = 0;
            double bestInterp = double.MaxValue;

            for (int t = 0; t < Trials; t++)
            {
                cpu.ResetCounters();
                Stopwatch sw = Stopwatch.StartNew();
                value = cpu.Call(entry, n);
                sw.Stop();
                bestInterp = Math.Min(bestInterp, sw.Elapsed.TotalMilliseconds);
                instructions = cpu.Executed;
            }

            NativeFib(n > 1000000 ? n / 100 : n);   // let the JIT settle first
            ulong expected = 0;
            double bestNative = double.MaxValue;
            for (int t = 0; t < Trials; t++)
            {
                Stopwatch sw = Stopwatch.StartNew();
                expected = NativeFib(n);
                sw.Stop();
                bestNative = Math.Min(bestNative, sw.Elapsed.TotalMilliseconds);
            }

            if (value != expected) throw new InvalidOperationException("interpreter disagrees with native fib");

            double seconds = Math.Max(0.0001, bestInterp / 1000.0);
            double mips = instructions / seconds / 1e6;
            double ratio = bestInterp / Math.Max(0.0001, bestNative);

            string json = "{\n" +
                "  \"n\": " + n + ",\n" +
                "  \"trials\": " + Trials + ",\n" +
                "  \"value\": \"" + value.ToString("x", CultureInfo.InvariantCulture) + "\",\n" +
                "  \"instructions\": " + instructions + ",\n" +
                "  \"interpreterMs\": " + bestInterp.ToString("0.00", CultureInfo.InvariantCulture) + ",\n" +
                "  \"nativeMs\": " + bestNative.ToString("0.0000", CultureInfo.InvariantCulture) + ",\n" +
                "  \"slowdown\": " + ratio.ToString("0", CultureInfo.InvariantCulture) + ",\n" +
                "  \"mips\": " + mips.ToString("0.0", CultureInfo.InvariantCulture) + "\n" +
                "}\n";
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "bench.json"), json, Encoding.UTF8);
            Console.Write(json);
            return 0;
        }

        private static ulong NativeFib(ulong n)
        {
            ulong a = 0, b = 1;
            for (ulong i = 0; i < n; i++)
            {
                ulong t = a + b;
                a = b;
                b = t;
            }
            return a;
        }

        private static string Value(string[] args, string flag)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == flag) return args[i + 1];
            return null;
        }
    }

    internal sealed class Elf
    {
        public readonly byte[] Image;
        public readonly ulong Base;
        public readonly Dictionary<string, ulong> Symbols = new Dictionary<string, ulong>(StringComparer.Ordinal);
        public readonly long FileBytes;
        public readonly int TextBytes;

        private Elf(byte[] image, ulong loadBase, long fileBytes, int textBytes)
        {
            Image = image;
            Base = loadBase;
            FileBytes = fileBytes;
            TextBytes = textBytes;
        }

        public ulong Symbol(string name)
        {
            ulong value;
            if (!Symbols.TryGetValue(name, out value))
                throw new InvalidOperationException("symbol not found: " + name);
            return value;
        }

        public static Elf Load(string path, ulong loadBase)
        {
            byte[] file = File.ReadAllBytes(path);
            if (file.Length < 64 || file[0] != 0x7F || file[1] != 'E' || file[2] != 'L' || file[3] != 'F')
                throw new InvalidOperationException("not an ELF file");
            if (file[4] != 2) throw new InvalidOperationException("not ELF64");
            ushort machine = BitConverter.ToUInt16(file, 18);
            if (machine != 183) throw new InvalidOperationException("not AArch64 (e_machine=" + machine + ")");

            ulong shoff = BitConverter.ToUInt64(file, 0x28);
            int shentsize = BitConverter.ToUInt16(file, 0x3A);
            int shnum = BitConverter.ToUInt16(file, 0x3C);
            int shstrndx = BitConverter.ToUInt16(file, 0x3E);

            Section[] sections = new Section[shnum];
            for (int i = 0; i < shnum; i++)
                sections[i] = Section.Read(file, (int)shoff + i * shentsize);

            string[] names = SectionNames(file, sections[shstrndx], sections);
            Section text = Find(sections, names, ".text");
            Section symtab = Find(sections, names, ".symtab");
            Section strtab = Find(sections, names, ".strtab");
            if (text.Size == 0 || symtab.Size == 0) throw new InvalidOperationException("guest.so has no .text/.symtab");

            byte[] image = new byte[64 * 1024 * 1024];
            Array.Copy(file, (int)text.Offset, image, (int)text.Addr, (int)text.Size);

            Elf elf = new Elf(image, loadBase, file.Length, (int)text.Size);

            int count = (int)(symtab.Size / 24);
            for (int i = 0; i < count; i++)
            {
                int at = (int)symtab.Offset + i * 24;
                uint nameOff = BitConverter.ToUInt32(file, at);
                ushort shndx = BitConverter.ToUInt16(file, at + 6);
                ulong value = BitConverter.ToUInt64(file, at + 8);
                if (shndx == 0 || nameOff == 0) continue;
                string name = CString(file, (int)strtab.Offset + (int)nameOff);
                elf.Symbols[name] = loadBase + value;
            }
            return elf;
        }

        private static Section Find(Section[] sections, string[] names, string name)
        {
            for (int i = 0; i < sections.Length; i++)
                if (names[i] == name) return sections[i];
            throw new InvalidOperationException("missing section " + name);
        }

        private static string[] SectionNames(byte[] file, Section shstr, Section[] sections)
        {
            string[] names = new string[sections.Length];
            for (int i = 0; i < sections.Length; i++)
                names[i] = sections[i].Name == 0 ? "" : CString(file, (int)shstr.Offset + (int)sections[i].Name);
            return names;
        }

        private static string CString(byte[] file, int offset)
        {
            int end = offset;
            while (end < file.Length && file[end] != 0) end++;
            return Encoding.ASCII.GetString(file, offset, end - offset);
        }

        internal struct Section
        {
            public uint Name;
            public uint Type;
            public ulong Addr;
            public ulong Offset;
            public ulong Size;

            public static Section Read(byte[] file, int at)
            {
                Section s = new Section();
                s.Name = BitConverter.ToUInt32(file, at);
                s.Type = BitConverter.ToUInt32(file, at + 4);
                s.Addr = BitConverter.ToUInt64(file, at + 16);
                s.Offset = BitConverter.ToUInt64(file, at + 24);
                s.Size = BitConverter.ToUInt64(file, at + 32);
                return s;
            }
        }
    }

    internal sealed class Memory
    {
        private readonly byte[] bytes;
        private readonly ulong baseAddr;

        public Memory(byte[] image, ulong baseAddr)
        {
            bytes = image;
            this.baseAddr = baseAddr;
        }

        private int At(ulong addr, int len)
        {
            ulong off = addr - baseAddr;
            if (off + (ulong)len > (ulong)bytes.Length)
                throw new InvalidOperationException("guest memory fault at 0x" + addr.ToString("x"));
            return (int)off;
        }

        public uint ReadU32(ulong addr) { return BitConverter.ToUInt32(bytes, At(addr, 4)); }
        public ulong ReadU64(ulong addr) { return BitConverter.ToUInt64(bytes, At(addr, 8)); }
        public byte ReadU8(ulong addr) { return bytes[At(addr, 1)]; }
        public void WriteU64(ulong addr, ulong value) { BitConverter.GetBytes(value).CopyTo(bytes, At(addr, 8)); }
        public void WriteU8(ulong addr, byte value) { bytes[At(addr, 1)] = value; }
    }

    internal sealed class Cpu
    {
        private const ulong ReturnSentinel = 0xFFFFFFFFFFFFFFFFUL;

        public readonly ulong[] X = new ulong[32];
        public readonly Memory Memory;
        public ulong Pc;
        public long Executed;

        private bool n, z, c, v;
        private readonly Dictionary<string, long> histogram = new Dictionary<string, long>(StringComparer.Ordinal);

        public Cpu(Elf elf)
        {
            Memory = new Memory(elf.Image, elf.Base);
        }

        public void ResetCounters()
        {
            Executed = 0;
            histogram.Clear();
        }

        public Dictionary<string, long> Histogram { get { return histogram; } }

        /// <summary>Calls a guest function with System V / AAPCS64 integer arguments.</summary>
        public ulong Call(ulong entry, params ulong[] args)
        {
            Array.Clear(X, 0, X.Length);
            for (int i = 0; i < args.Length && i < 8; i++) X[i] = args[i];
            X[30] = ReturnSentinel;
            Pc = entry;

            while (true)
            {
                Step();
                if (Pc == ReturnSentinel) return X[0];
            }
        }

        private void Step()
        {
            if (Pc == ReturnSentinel) return;
            uint insn = Memory.ReadU32(Pc);
            ulong pc = Pc;
            Pc += 4;
            Executed++;
            Execute(insn, pc);
        }

        private void Execute(uint insn, ulong pc)
        {
            int rd = (int)(insn & 31);
            int rn = (int)((insn >> 5) & 31);
            int rm = (int)((insn >> 16) & 31);
            string op = null;

            // MOVZ (64-bit; MOVN and MOVK are not implemented)
            if ((insn & 0xFF800000) == 0xD2800000)
            {
                int hw = (int)((insn >> 21) & 3);
                Set(rd, (ulong)((insn >> 5) & 0xFFFF) << (hw * 16));
                op = "movz";
            }
            // ORR <Xd>, XZR, <Xm> -- the MOV register alias
            else if ((insn & 0xFFE0FFE0) == 0xAA0003E0)
            {
                Set(rd, Get(rm));
                op = "mov";
            }
            // ADD / SUB / ADDS / SUBS immediate (SUBS with Rd=31 is CMP)
            else if ((insn & 0x9F000000) == 0x91000000)
            {
                bool sub = (insn & 0x40000000) != 0;
                bool setFlags = (insn & 0x20000000) != 0;
                int shift = (int)((insn >> 22) & 1) * 12;
                ulong imm = (ulong)((insn >> 10) & 0xFFF) << shift;
                ulong a = Get(rn);
                ulong result = sub ? a - imm : a + imm;
                if (setFlags) SetFlags(result, a, imm, sub);
                if (!setFlags || rd != 31) Set(rd, result);
                op = sub ? (setFlags ? "subs-imm" : "sub-imm") : (setFlags ? "adds-imm" : "add-imm");
            }
            // ADD / SUB shifted register, LSL #0 only
            else if ((insn & 0x9F200000) == 0x8B000000)
            {
                bool sub = (insn & 0x40000000) != 0;
                bool setFlags = (insn & 0x20000000) != 0;
                if ((insn & 0x80000000) == 0) throw new NotSupportedException("32-bit ADD/SUB register");
                int shift = (int)((insn >> 22) & 3);
                if (shift != 0) throw new NotSupportedException("shifted register ADD/SUB with shift " + shift);
                ulong a = Get(rn), b = Get(rm);
                ulong result = sub ? a - b : a + b;
                if (setFlags) SetFlags(result, a, b, sub);
                if (!setFlags || rd != 31) Set(rd, result);
                op = sub ? (setFlags ? "subs-reg" : "sub-reg") : (setFlags ? "adds-reg" : "add-reg");
            }
            // MADD / MUL (Ra = 31)
            else if ((insn & 0x7FE00000) == 0x1B000000)
            {
                ulong a = Get(rn), b = Get(rm), acc = Get((int)((insn >> 10) & 31));
                Set(rd, acc + a * b);
                op = "madd";
            }
            // LDR / STR 64-bit unsigned offset
            else if ((insn & 0xFFC00000) == 0xF9400000 || (insn & 0xFFC00000) == 0xF9000000)
            {
                bool load = (insn & 0x00400000) != 0;
                ulong addr = Get(rn) + (ulong)((insn >> 10) & 0xFFF) * 8;
                if (load) Set(rd, Memory.ReadU64(addr));
                else Memory.WriteU64(addr, Get(rd));
                op = load ? "ldr" : "str";
            }
            // LDRB / STRB
            else if ((insn & 0xFFC00000) == 0x39400000 || (insn & 0xFFC00000) == 0x39000000)
            {
                bool load = (insn & 0x00400000) != 0;
                ulong addr = Get(rn) + (ulong)((insn >> 10) & 0xFFF);
                if (load) Set(rd, Memory.ReadU8(addr));
                else Memory.WriteU8(addr, (byte)Get(rd));
                op = load ? "ldrb" : "strb";
            }
            // UBFM -- extracts, LSR and LSL immediates all land here
            else if ((insn & 0xFF800000) == 0xD3400000)
            {
                int immr = (int)((insn >> 16) & 63);
                int imms = (int)((insn >> 10) & 63);
                ulong src = Get(rn);
                ulong result;
                if (imms >= immr)
                {
                    int width = imms - immr + 1;
                    ulong mask = width == 64 ? ulong.MaxValue : ((1UL << width) - 1);
                    result = (src >> immr) & mask;
                }
                else
                {
                    int width = imms + 1;
                    ulong mask = (1UL << width) - 1;
                    result = (src & mask) << (64 - immr);
                }
                Set(rd, result);
                op = "ubfm";
            }
            // ADR
            else if ((insn & 0x9F000000) == 0x10000000)
            {
                long immlo = (long)((insn >> 29) & 3);
                long immhi = (long)((insn >> 5) & 0x7FFFF);
                long imm = (immhi << 2) | immlo;
                if ((imm & (1 << 20)) != 0) imm |= ~0x1FFFFFL;   // 21-bit signed
                Set(rd, (ulong)((long)pc + imm));
                op = "adr";
            }
            // B / BL
            else if ((insn & 0x7C000000) == 0x14000000)
            {
                long imm = (long)(insn & 0x3FFFFFF);
                if ((imm & (1 << 25)) != 0) imm |= ~0x3FFFFFFL;
                if ((insn & 0x80000000) != 0) X[30] = Pc;
                Pc = (ulong)((long)pc + (imm << 2));
                op = (insn & 0x80000000) != 0 ? "bl" : "b";
            }
            // B.cond
            else if ((insn & 0xFF000010) == 0x54000000)
            {
                long imm = (long)((insn >> 5) & 0x7FFFF);
                if ((imm & (1 << 18)) != 0) imm |= ~0x7FFFFL;
                int cond = (int)(insn & 15);
                if (Condition(cond)) Pc = (ulong)((long)pc + (imm << 2));
                op = "b.cond";
            }
            // CBZ / CBNZ (64-bit)
            else if ((insn & 0xBF000000) == 0xB4000000)
            {
                bool nonzero = (insn & 0x01000000) != 0;
                long imm = (long)((insn >> 5) & 0x7FFFF);
                if ((imm & (1 << 18)) != 0) imm |= ~0x7FFFFL;
                bool take = nonzero ? Get(rd) != 0 : Get(rd) == 0;
                if (take) Pc = (ulong)((long)pc + (imm << 2));
                op = nonzero ? "cbnz" : "cbz";
            }
            // RET
            else if (insn == 0xD65F03C0)
            {
                Pc = Get(30);
                op = "ret";
            }
            // SVC -- the host call bridge
            else if ((insn & 0xFFE0001F) == 0xD4000001)
            {
                ulong selector = Get(16);
                switch (selector)
                {
                    case 1:
                        Set(0, Get(0) + Get(1));      // host implementation of the callee
                        break;
                    default:
                        throw new NotSupportedException("host call " + selector + " is not wired up");
                }
                op = "svc";
            }
            else
            {
                throw new NotSupportedException("unimplemented instruction 0x" + insn.ToString("x8") +
                    " at 0x" + pc.ToString("x"));
            }

            long hits;
            histogram.TryGetValue(op, out hits);
            histogram[op] = hits + 1;
        }

        private ulong Get(int reg)
        {
            return reg == 31 ? 0UL : X[reg];
        }

        private void Set(int reg, ulong value)
        {
            if (reg != 31) X[reg] = value;
        }

        private void SetFlags(ulong result, ulong a, ulong b, bool subtract)
        {
            n = (result >> 63) != 0;
            z = result == 0;
            if (subtract)
            {
                c = a >= b;
                v = (((a ^ b) & (a ^ result)) >> 63) != 0;
            }
            else
            {
                c = result < a;
                v = (((a ^ result) & (b ^ result)) >> 63) != 0;
            }
        }

        private bool Condition(int cond)
        {
            switch (cond)
            {
                case 0: return z;
                case 1: return !z;
                case 2: return c;
                case 3: return !c;
                case 4: return n;
                case 5: return !n;
                case 6: return v;
                case 7: return !v;
                case 8: return c && !z;
                case 9: return !c || z;
                case 10: return n == v;
                case 11: return n != v;
                case 12: return !z && n == v;
                case 13: return z || n != v;
                case 14: return true;
                default: return false;
            }
        }
    }
}
