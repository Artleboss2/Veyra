#!/usr/bin/env python3
"""Emit the two artefacts the proof of concept needs.

build/guest.so    a real AArch64 shared object with three exported functions,
                  assembled by hand and loaded by the C# interpreter
build/harness.elf a bare-metal AArch64 executable that calls the same function
                  bodies, prints the results through semihosting and exits --
                  run on qemu-system-aarch64 it is the ground truth for both
                  the encodings and the arithmetic

Usage: python research/arm64-bridge/gen_guest.py [--out build/arm64-lab]
"""

import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import arm64enc as A


def sum_words(a):
    """x0 = pointer, x1 = count -> x0 = sum of the 64-bit words."""
    a.mov(9, 31)                     # x9 = 0 via mov x9, xzr
    a.label("sum_loop")
    a.word(A.cmp_imm(1, 0))
    a.BCOND(A.EQ, "sum_done")
    a.word(A.ldr(10, 0))
    a.word(A.add_reg(9, 9, 10))
    a.word(A.add_imm(0, 0, 8))
    a.word(A.sub_imm(1, 1, 1))
    a.B("sum_loop")
    a.label("sum_done")
    a.word(A.mov(0, 9))
    a.word(A.ret())
    return "sum_words", 0, a.here()


def fib(a):
    """x0 = n -> x0 = fib(n), iterative."""
    a.word(A.movz(9, 0))             # a = 0
    a.word(A.movz(10, 1))            # b = 1
    a.label("fib_loop")
    a.word(A.cmp_imm(0, 0))
    a.BCOND(A.EQ, "fib_done")
    a.word(A.add_reg(11, 9, 10))     # t = a + b
    a.word(A.mov(9, 10))             # a = b
    a.word(A.mov(10, 11))            # b = t
    a.word(A.sub_imm(0, 0, 1))
    a.B("fib_loop")
    a.label("fib_done")
    a.word(A.mov(0, 9))
    a.word(A.ret())
    return "fib", 0, a.here()


def mix(a):
    """x0 = a, x1 = b -> a*b - b when a > b, else a + b."""
    a.word(A.cmp_reg(0, 1))
    a.BCOND(A.LE, "mix_low")
    a.word(A.mul(0, 0, 1))
    a.word(A.sub_reg(0, 0, 1))
    a.word(A.ret())
    a.label("mix_low")
    a.word(A.add_reg(0, 0, 1))
    a.word(A.ret())
    return "mix", 0, a.here()


def host_sum(a):
    """x0 = a, x1 = b -> x0 = a + b, answered by the host through SVC #0.

    This is the "library wrapping" idea in miniature: the guest does not carry
    an implementation of the callee, it traps into the host, which is exactly how
    a native bridge forwards libc-style calls instead of translating them.
    """
    a.word(A.movz(16, 1))            # function selector for the host
    a.word(A.svc(0))
    a.word(A.ret())
    return "host_sum", 0, a.here()


def build_guest(path):
    a = A.Asm()
    symbols = []
    for fn in (sum_words, fib, mix, host_sum):
        start = a.here()
        name, _, _ = fn(a)
        symbols.append((name, start, a.here() - start))
    code = a.resolve()
    blob = A.build_elf(code, symbols, base=0, executable=False)
    with open(path, "wb") as f:
        f.write(blob)
    return symbols, len(code)


def build_harness(path):
    """Same three functions plus a hex printer and a semihosting exit."""
    a = A.Asm()

    a.B("_start")
    a.word(0)                        # padding keeps entry at a 4-byte boundary

    start = a.here()
    a.label("sum_words")
    sum_words(a)
    sum_start = start
    sum_size = a.here() - sum_start

    start = a.here()
    a.label("fib")
    fib(a)
    fib_start = start
    fib_size = a.here() - fib_start

    start = a.here()
    a.label("mix")
    mix(a)
    mix_start = start
    mix_size = a.here() - mix_start

    # puthex: x0 = value, prints 16 lowercase hex digits and a newline
    a.label("puthex")
    a.ADR(21, "buf")
    a.word(A.movz(20, 0))
    a.word(A.mov(20, 0))
    a.word(A.movz(22, 16))
    a.label("ph_loop")
    a.word(A.lsr_imm(23, 20, 60))
    a.word(A.cmp_imm(23, 10))
    a.BCOND(A.LT, "ph_digit")
    a.word(A.add_imm(23, 23, 87))    # 'a' - 10
    a.B("ph_store")
    a.label("ph_digit")
    a.word(A.add_imm(23, 23, 48))    # '0'
    a.label("ph_store")
    a.word(A.strb(23, 21, 0))
    a.word(A.add_imm(21, 21, 1))
    a.word(A.lsl_imm(20, 20, 4))
    a.word(A.sub_imm(22, 22, 1))
    a.CBZ(22, "ph_done")
    a.B("ph_loop")
    a.label("ph_done")
    a.word(A.movz(23, 10))           # newline
    a.word(A.strb(23, 21, 0))
    a.word(A.add_imm(21, 21, 1))
    a.word(A.movz(23, 0))            # NUL so SYS_WRITE0 stops
    a.word(A.strb(23, 21, 0))
    a.ADR(1, "buf")
    a.word(A.movz(0, 4))             # SYS_WRITE0
    a.word(A.hlt(0xF000))
    a.word(A.ret())

    a.label("_start")
    a.ADR(19, "data")
    a.word(A.mov(0, 19))
    a.word(A.movz(1, 8))
    a.BL("sum_words")
    a.BL("puthex")
    a.word(A.movz(0, 30))
    a.BL("fib")
    a.BL("puthex")
    a.word(A.movz(0, 7))
    a.word(A.movz(1, 5))
    a.BL("mix")
    a.BL("puthex")
    a.word(A.movz(0, 0x18))          # SYS_EXIT
    a.word(A.movz(1, 2))             # 0x20026 = ADP_Stopped_ApplicationExit
    a.word(A.lsl_imm(1, 1, 16))
    a.word(A.add_imm(1, 1, 0x26))
    a.word(A.hlt(0xF000))
    a.label("dead")
    a.B("dead")

    while a.here() % 8:             # 64-bit LDR traps on a misaligned address
        a.word(0)
    a.label("data")
    for v in range(1, 9):
        a.word(v)
        a.word(0)                    # low half; 64-bit little-endian value
    a.label("buf")
    for _ in range(24):
        a.word(0)

    code = a.resolve()
    symbols = [("sum_words", sum_start, sum_size), ("fib", fib_start, fib_size),
               ("mix", mix_start, mix_size)]
    blob = A.build_elf(code, symbols, base=0x40000000, executable=True)
    with open(path, "wb") as f:
        f.write(blob)
    return len(code)


def main():
    out = "build/arm64-lab"
    if "--out" in sys.argv:
        out = sys.argv[sys.argv.index("--out") + 1]
    os.makedirs(out, exist_ok=True)

    A.check(A.KNOWN)
    so = os.path.join(out, "guest.so")
    elf = os.path.join(out, "harness.elf")
    symbols, so_bytes = build_guest(so)
    elf_bytes = build_harness(elf)

    expected = {
        "sum_words": sum(range(1, 9)),
        "fib": 832040,
        "mix": 7 * 5 - 5,
    }
    manifest = {
        "guest": os.path.basename(so),
        "guestBytes": os.path.getsize(so),
        "guestCodeBytes": so_bytes,
        "harness": os.path.basename(elf),
        "harnessBytes": os.path.getsize(elf),
        "harnessCodeBytes": elf_bytes,
        "symbols": [{"name": n, "offset": o, "size": s} for n, o, s in symbols],
        "expected": expected,
    }
    with open(os.path.join(out, "manifest.json"), "w", encoding="ascii") as f:
        json.dump(manifest, f, indent=2)
        f.write("\n")

    print("guest.so     %6d bytes (%d bytes of code, %d symbols)" % (manifest["guestBytes"], so_bytes, len(symbols)))
    print("harness.elf  %6d bytes (%d bytes of code)" % (manifest["harnessBytes"], elf_bytes))
    for s in manifest["symbols"]:
        print("  %-10s offset %4d size %4d" % (s["name"], s["offset"], s["size"]))


if __name__ == "__main__":
    main()
