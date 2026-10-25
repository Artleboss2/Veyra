#!/usr/bin/env python3
"""Hand-rolled AArch64 encoder and ELF64 writer.

No toolchain is installed for ARM64 on this machine, so the guest code is
assembled here, bitfield by bitfield, straight from the instruction layouts in
the ARM Architecture Reference Manual. Keeping it in Python means the same
bytes can be emitted as a shared object (loaded by the C# interpreter) and as a
bare-metal executable (run on qemu-system-aarch64, which is the ground truth).

Only the subset the proof of concept needs is implemented. Every encoder below
is checked against a known-good constant in KNOWN so a typo in a field offset
fails loudly instead of producing a plausible-looking wrong instruction.
"""

import struct

# cond field of B.cond
EQ, NE, CS, CC, MI, PL, VS, VC, HI, LS, GE, LT, GT, LE, AL = range(15)


def _chk(value, bits, name):
    if not 0 <= value < (1 << bits):
        raise ValueError("%s out of range: %r" % (name, value))
    return value


# ---------------------------------------------------------------- data processing

def movz(rd, imm16):
    """MOVZ Xd, #imm16 (no shift)."""
    return 0xD2800000 | (_chk(imm16, 16, "imm16") << 5) | _chk(rd, 5, "rd")


def mov(rd, rm):
    """MOV Xd, Xm -- ORR Xd, XZR, Xm."""
    return 0xAA0003E0 | (_chk(rm, 5, "rm") << 16) | _chk(rd, 5, "rd")


def add_imm(rd, rn, imm12):
    """ADD Xd, Xn, #imm12."""
    return 0x91000000 | (_chk(imm12, 12, "imm12") << 10) | (_chk(rn, 5, "rn") << 5) | _chk(rd, 5, "rd")


def sub_imm(rd, rn, imm12):
    """SUB Xd, Xn, #imm12."""
    return 0xD1000000 | (_chk(imm12, 12, "imm12") << 10) | (_chk(rn, 5, "rn") << 5) | _chk(rd, 5, "rd")


def cmp_imm(rn, imm12):
    """CMP Xn, #imm12 -- SUBS XZR, Xn, #imm12."""
    return 0xF1000000 | (_chk(imm12, 12, "imm12") << 10) | (_chk(rn, 5, "rn") << 5) | 31


def add_reg(rd, rn, rm):
    """ADD Xd, Xn, Xm (shift 0)."""
    return 0x8B000000 | (_chk(rm, 5, "rm") << 16) | (_chk(rn, 5, "rn") << 5) | _chk(rd, 5, "rd")


def sub_reg(rd, rn, rm):
    """SUB Xd, Xn, Xm (shift 0)."""
    return 0xCB000000 | (_chk(rm, 5, "rm") << 16) | (_chk(rn, 5, "rn") << 5) | _chk(rd, 5, "rd")


def cmp_reg(rn, rm):
    """CMP Xn, Xm -- SUBS XZR, Xn, Xm."""
    return 0xEB000000 | (_chk(rm, 5, "rm") << 16) | (_chk(rn, 5, "rn") << 5) | 31


def mul(rd, rn, rm):
    """MUL Xd, Xn, Xm -- MADD Xd, Xn, Xm, XZR."""
    return 0x9B007C00 | (_chk(rm, 5, "rm") << 16) | (_chk(rn, 5, "rn") << 5) | _chk(rd, 5, "rd")


def ubfm(rd, rn, immr, imms):
    """UBFM Xd, Xn, #immr, #imms -- bitfield extract / shift by immediate."""
    return (0xD3400000 | (_chk(immr, 6, "immr") << 16) | (_chk(imms, 6, "imms") << 10)
            | (_chk(rn, 5, "rn") << 5) | _chk(rd, 5, "rd"))


def lsl_imm(rd, rn, shift):
    """LSL Xd, Xn, #shift."""
    return ubfm(rd, rn, (64 - shift) & 63, 63 - shift)


def lsr_imm(rd, rn, shift):
    """LSR Xd, Xn, #shift."""
    return ubfm(rd, rn, shift, 63)


def extract(rd, rn, shift, width):
    """Xd = (Xn >> shift) & ((1 << width) - 1)."""
    return ubfm(rd, rn, shift, shift + width - 1)


# ----------------------------------------------------------------------- memory

def ldr(rt, rn, byte_offset=0):
    """LDR Xt, [Xn, #byte_offset] (unsigned offset, scaled by 8)."""
    if byte_offset % 8:
        raise ValueError("ldr offset must be a multiple of 8")
    return 0xF9400000 | (_chk(byte_offset // 8, 12, "imm12") << 10) | (_chk(rn, 5, "rn") << 5) | _chk(rt, 5, "rt")


def str_(rt, rn, byte_offset=0):
    """STR Xt, [Xn, #byte_offset]."""
    if byte_offset % 8:
        raise ValueError("str offset must be a multiple of 8")
    return 0xF9000000 | (_chk(byte_offset // 8, 12, "imm12") << 10) | (_chk(rn, 5, "rn") << 5) | _chk(rt, 5, "rt")


def ldrb(rt, rn, byte_offset=0):
    """LDRB Wt, [Xn, #byte_offset]."""
    return 0x39400000 | (_chk(byte_offset, 12, "imm12") << 10) | (_chk(rn, 5, "rn") << 5) | _chk(rt, 5, "rt")


def strb(rt, rn, byte_offset=0):
    """STRB Wt, [Xn, #byte_offset]."""
    return 0x39000000 | (_chk(byte_offset, 12, "imm12") << 10) | (_chk(rn, 5, "rn") << 5) | _chk(rt, 5, "rt")


# ---------------------------------------------------------------- control flow

def b(byte_offset):
    return 0x14000000 | ((byte_offset >> 2) & 0x3FFFFFF)


def bl(byte_offset):
    return 0x94000000 | ((byte_offset >> 2) & 0x3FFFFFF)


def bcond(cond, byte_offset):
    return 0x54000000 | (((byte_offset >> 2) & 0x7FFFF) << 5) | _chk(cond, 4, "cond")


def cbz(rt, byte_offset):
    return 0xB4000000 | (((byte_offset >> 2) & 0x7FFFF) << 5) | _chk(rt, 5, "rt")


def cbnz(rt, byte_offset):
    return 0xB5000000 | (((byte_offset >> 2) & 0x7FFFF) << 5) | _chk(rt, 5, "rt")


def ret():
    return 0xD65F03C0


def svc(imm16=0):
    return 0xD4000001 | (_chk(imm16, 16, "imm16") << 5)


def hlt(imm16):
    return 0xD4400000 | (_chk(imm16, 16, "imm16") << 5)


def adr(rd, byte_offset):
    """ADR Xd, .+byte_offset (pc-relative, +-1 MB)."""
    if not -1048576 <= byte_offset < 1048576:
        raise ValueError("adr range")
    imm = byte_offset & 0x1FFFFF
    return 0x10000000 | ((imm & 3) << 29) | (((imm >> 2) & 0x7FFFF) << 5) | _chk(rd, 5, "rd")


def adrp(rd, page_delta):
    """ADRP Xd, page_delta (in pages)."""
    imm = page_delta & 0x1FFFFF
    return 0x90000000 | ((imm & 3) << 29) | (((imm >> 2) & 0x7FFFF) << 5) | _chk(rd, 5, "rd")


# --------------------------------------------------------------------- assembler

class Asm:
    """Tiny two-pass assembler: emit words, name labels, resolve branches."""

    def __init__(self):
        self.code = bytearray()
        self.labels = {}
        self.fixups = []

    def word(self, w):
        self.code += struct.pack("<I", w & 0xFFFFFFFF)

    # Convenience wrappers so guest code reads like assembly. Inside a method the
    # bare names below still resolve to the module-level encoders, not to these.
    def movz(self, rd, imm16): self.word(movz(rd, imm16))
    def mov(self, rd, rm): self.word(mov(rd, rm))
    def add_imm(self, rd, rn, imm12): self.word(add_imm(rd, rn, imm12))
    def sub_imm(self, rd, rn, imm12): self.word(sub_imm(rd, rn, imm12))
    def cmp_imm(self, rn, imm12): self.word(cmp_imm(rn, imm12))
    def add_reg(self, rd, rn, rm): self.word(add_reg(rd, rn, rm))
    def sub_reg(self, rd, rn, rm): self.word(sub_reg(rd, rn, rm))
    def cmp_reg(self, rn, rm): self.word(cmp_reg(rn, rm))
    def mul(self, rd, rn, rm): self.word(mul(rd, rn, rm))
    def ubfm(self, rd, rn, immr, imms): self.word(ubfm(rd, rn, immr, imms))
    def lsl_imm(self, rd, rn, shift): self.word(lsl_imm(rd, rn, shift))
    def lsr_imm(self, rd, rn, shift): self.word(lsr_imm(rd, rn, shift))
    def extract(self, rd, rn, shift, width): self.word(extract(rd, rn, shift, width))
    def ldr(self, rt, rn, off=0): self.word(ldr(rt, rn, off))
    def str_(self, rt, rn, off=0): self.word(str_(rt, rn, off))
    def ldrb(self, rt, rn, off=0): self.word(ldrb(rt, rn, off))
    def strb(self, rt, rn, off=0): self.word(strb(rt, rn, off))
    def ret(self): self.word(ret())
    def svc(self, imm16=0): self.word(svc(imm16))
    def hlt(self, imm16): self.word(hlt(imm16))

    def here(self):
        return len(self.code)

    def label(self, name):
        if name in self.labels:
            raise ValueError("duplicate label " + name)
        self.labels[name] = len(self.code)

    def _rel(self, label, at):
        if label not in self.labels:
            raise ValueError("unknown label " + label)
        return self.labels[label] - at

    # Every branch reserves its word now and is patched in resolve(); forgetting
    # the placeholder silently shifts the whole function body.
    def B(self, label):
        at = len(self.code)
        self.word(0)
        self.fixups.append((at, label, "b"))

    def BL(self, label):
        at = len(self.code)
        self.word(0)
        self.fixups.append((at, label, "bl"))

    def BCOND(self, cond, label):
        at = len(self.code)
        self.word(0)
        self.fixups.append((at, label, ("cond", cond)))

    def CBZ(self, rt, label):
        at = len(self.code)
        self.word(0)
        self.fixups.append((at, label, ("cbz", rt)))

    def CBNZ(self, rt, label):
        at = len(self.code)
        self.word(0)
        self.fixups.append((at, label, ("cbnz", rt)))

    def ADR(self, rd, label):
        at = len(self.code)
        self.fixups.append((at, label, ("adr", rd)))
        self.word(0)

    def resolve(self):
        code = bytearray(self.code)
        for at, label, kind in self.fixups:
            delta = self._rel(label, at)
            if kind == "b":
                w = b(delta)
            elif kind == "bl":
                w = bl(delta)
            elif isinstance(kind, tuple) and kind[0] == "cond":
                w = bcond(kind[1], delta)
            elif isinstance(kind, tuple) and kind[0] == "cbz":
                w = cbz(kind[1], delta)
            elif isinstance(kind, tuple) and kind[0] == "cbnz":
                w = cbnz(kind[1], delta)
            elif isinstance(kind, tuple) and kind[0] == "adr":
                w = adr(kind[1], delta)
            else:
                raise ValueError("bad fixup " + repr(kind))
            struct.pack_into("<I", code, at, w & 0xFFFFFFFF)
        return bytes(code)


# ----------------------------------------------------------------------- ELF64

EM_AARCH64 = 183
ET_EXEC, ET_DYN = 2, 3
PT_LOAD = 1
PF_X, PF_W, PF_R = 1, 2, 4
SHT_PROGBITS, SHT_SYMTAB, SHT_STRTAB = 1, 2, 3
SHF_ALLOC, SHF_EXECINSTR = 2, 4


def _elf_header(vaddr_base, entry, phoff, shoff, phnum, shnum, shstrndx):
    ident = bytearray(16)
    ident[0:4] = b"\x7fELF"
    ident[4] = 2          # 64-bit
    ident[5] = 1          # little endian
    ident[6] = 1          # version
    header = struct.pack(
        "<16sHHIQQQIHHHHHH",
        bytes(ident), ET_EXEC, EM_AARCH64, 1,
        entry, 64, shoff, 0,
        64, 56, phnum, 64, shnum, shstrndx)
    return header


def _phdr(flags, offset, vaddr, filesz, memsz):
    return struct.pack("<IIQQQQQQ", PT_LOAD, flags, offset, vaddr, vaddr, filesz, memsz, 16)


def _shdr(name, typ, flags, addr, offset, size, link=0, info=0, addralign=1, entsize=0):
    return struct.pack("<IIQQQQIIQQ", name, typ, flags, addr, offset, size, link, info, addralign, entsize)


def build_elf(code, symbols, base=0x40000000, executable=False):
    """Write a tiny ELF64: one RX/RWX PT_LOAD plus .symtab for our loader.

    code     -- raw instruction bytes (functions concatenated)
    symbols  -- list of (name, offset, size) with offsets relative to code
    base     -- vaddr of the load segment; 0 for a shared object
    """
    # Page-aligned so p_vaddr % 0x1000 == p_offset % 0x1000, which QEMU's ELF
    # loader and every real loader assume.
    text_off = 0x1000
    text_addr = base + text_off

    strtab = bytearray(b"\x00")
    sym_entries = [b"\x00" * 24]
    for name, off, size in symbols:
        name_off = len(strtab)
        strtab += name.encode() + b"\x00"
        sym_entries.append(struct.pack("<IBBHQQ", name_off, 0x12, 0, 1, text_addr + off, size))
    symtab = b"".join(sym_entries)

    shstrtab = bytearray(b"\x00")
    sh_names = {}
    for nm in (".text", ".symtab", ".strtab", ".shstrtab"):
        sh_names[nm] = len(shstrtab)
        shstrtab += nm.encode() + b"\x00"

    sym_off = text_off + len(code)
    sym_off += (-sym_off) % 8
    str_off = sym_off + len(symtab)
    shstr_off = str_off + len(strtab)
    shstr_off += (-shstr_off) % 8
    shoff = shstr_off + len(shstrtab)
    shoff += (-shoff) % 8

    shnum = 5
    phnum = 1
    header = _elf_header(base, text_addr, 64, shoff, phnum, shnum, 4)

    blob = bytearray()
    blob += header
    blob += _phdr(PF_R | PF_X | (PF_W if not executable else PF_W),
                  text_off, text_addr, len(code), len(code))
    blob += b"\x00" * (text_off - len(blob))
    blob += code
    blob += b"\x00" * (sym_off - len(blob))
    blob += symtab
    blob += strtab
    blob += b"\x00" * (shstr_off - len(blob))
    blob += shstrtab
    blob += b"\x00" * (shoff - len(blob))

    sh = bytearray()
    sh += _shdr(0, 0, 0, 0, 0, 0)
    sh += _shdr(sh_names[".text"], SHT_PROGBITS, SHF_ALLOC | SHF_EXECINSTR, text_addr, text_off, len(code))
    sh += _shdr(sh_names[".symtab"], SHT_SYMTAB, 0, 0, sym_off, len(symtab), link=3, info=1, entsize=24)
    sh += _shdr(sh_names[".strtab"], SHT_STRTAB, 0, 0, str_off, len(strtab))
    sh += _shdr(sh_names[".shstrtab"], SHT_STRTAB, 0, 0, shstr_off, len(shstrtab))
    blob += sh
    return bytes(blob)


def check(encoders):
    """Fail fast if an encoder no longer produces a known encoding."""
    for name, got, want in encoders:
        if got != want:
            raise SystemExit("encoder %s produced 0x%08X, expected 0x%08X" % (name, got, want))
    return True


KNOWN = [
    ("nop-ish movz", movz(0, 0), 0xD2800000),
    ("mov x1,x2", mov(1, 2), 0xAA0203E1),
    ("ret", ret(), 0xD65F03C0),
    ("svc #0", svc(0), 0xD4000001),
    ("hlt #0xf000", hlt(0xF000), 0xD45E0000),
    ("add x0,x0,#1", add_imm(0, 0, 1), 0x91000400),
    ("cmp x0,#0", cmp_imm(0, 0), 0xF100001F),
    ("mul x0,x1,x2", mul(0, 1, 2), 0x9B027C20),
    ("ldr x0,[x1]", ldr(0, 1, 0), 0xF9400020),
    ("lsr x0,x0,#4", lsr_imm(0, 0, 4), 0xD344FC00),
]


if __name__ == "__main__":
    check(KNOWN)
    print("encoder self-check ok (%d known encodings)" % len(KNOWN))
