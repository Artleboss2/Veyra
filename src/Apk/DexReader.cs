using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Veyra
{
    internal class DexInfo
    {
        public string Name = "classes.dex";
        public string Version = "";
        public long Size;
        public int StringCount;
        public int TypeCount;
        public int ProtoCount;
        public int FieldCount;
        public int MethodCount;
        public int ClassCount;
        public List<string> ClassNames = new List<string>();
    }

    /// <summary>Reads DEX headers, identifiers and class definitions straight out of the archive.</summary>
    internal static class DexReader
    {
        public static DexInfo Read(Stream stream, string name)
        {
            DexInfo info = new DexInfo();
            info.Name = name;

            MemoryStream ms = new MemoryStream();
            try
            {
                byte[] buf = new byte[64 * 1024];
                int n;
                while ((n = stream.Read(buf, 0, buf.Length)) > 0)
                {
                    ms.Write(buf, 0, n);
                    if (ms.Length > 48L * 1024 * 1024) break; // hard safety cap
                }
                byte[] d = ms.ToArray();
                info.Size = d.Length;
                if (d.Length < 0x70) throw new InvalidDataException("truncated");

                if (!(d[0] == (byte)'d' && d[1] == (byte)'e' && d[2] == (byte)'x')) 
                    throw new InvalidDataException("not a dex file");

                info.Version = Encoding.ASCII.GetString(d, 4, 3);

                info.StringCount = ReadI32(d, 56);
                int stringOff = ReadI32(d, 60);
                info.TypeCount = ReadI32(d, 64);
                int typeOff = ReadI32(d, 68);
                info.ProtoCount = ReadI32(d, 72);
                info.FieldCount = ReadI32(d, 80);
                info.MethodCount = ReadI32(d, 88);
                info.ClassCount = ReadI32(d, 96);
                int classOff = ReadI32(d, 100);

                int max = Math.Min(info.ClassCount, 4000);
                for (int i = 0; i < max; i++)
                {
                    int cd = classOff + i * 32;
                    if (cd < 0 || cd + 32 > d.Length) break;
                    int classIdx = ReadI32(d, cd);
                    int tid = typeOff + classIdx * 4;
                    if (tid < 0 || tid + 4 > d.Length) break;
                    int descriptorIdx = ReadI32(d, tid);
                    int sid = stringOff + descriptorIdx * 4;
                    if (sid < 0 || sid + 4 > d.Length) break;
                    int strOff = ReadI32(d, sid);
                    string descriptor = ReadMutf8(d, strOff);
                    string pretty = Pretty(descriptor);
                    if (pretty.Length > 0) info.ClassNames.Add(pretty);
                }
            }
            finally { ms.Dispose(); }
            return info;
        }

        private static int ReadI32(byte[] d, int o)
        {
            if (o < 0 || o + 4 > d.Length) return 0;
            return d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24);
        }

        private static string ReadMutf8(byte[] d, int off)
        {
            if (off <= 0 || off >= d.Length) return "";
            int p = off;
            // uleb128 utf16 length
            int len = 0, shift = 0;
            while (p < d.Length && p < off + 5)
            {
                byte b = d[p++];
                len |= (b & 0x7F) << shift;
                shift += 7;
                if ((b & 0x80) == 0) break;
            }
            int start = p;
            while (p < d.Length && d[p] != 0) p++;
            if (p <= start) return "";
            return Encoding.UTF8.GetString(d, start, p - start);
        }

        private static string Pretty(string descriptor)
        {
            if (string.IsNullOrEmpty(descriptor)) return "";
            string s = descriptor;
            if (s.StartsWith("L") && s.EndsWith(";")) s = s.Substring(1, s.Length - 2);
            s = s.Replace('/', '.');
            s = s.Replace('$', '.');
            return s;
        }
    }
}
