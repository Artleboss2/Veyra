# ARM64 bridge: proof of concept

Veyra boots an x86_64 Android image, so `arm64-v8a` packages cannot run in it.
The only way to change that without an ARM CPU is a **native bridge**: a
user-space translator that turns ARM64 machine code into x86_64 while the app
runs. This folder measures how far a from-scratch bridge actually gets, before
anyone promises it as a feature.

It is a research artefact. Nothing here is linked into `Veyra.exe`, and no APK
runs at the end of it.

## What is here

| File | Role |
|---|---|
| `arm64enc.py` | Hand-written AArch64 encoder and ELF64 writer. No ARM toolchain is installed, so instructions are assembled bitfield by bitfield from the ARM ARM; ten known encodings are asserted in `KNOWN` so a wrong bit offset fails loudly. |
| `gen_guest.py` | Emits `guest.so` (a real AArch64 shared object with four exported functions) and `harness.elf` (a bare-metal executable that calls the same bodies and prints results through semihosting). |
| `Guest.cs` | ELF64 loader, AArch64 interpreter, host-call bridge and benchmark driver. |
| `run.sh` | Assembles both artefacts, runs the harness on `qemu-system-aarch64`, compiles the interpreter, self-tests it and benchmarks it. |

## Why two artefacts

The interpreter is the thing under test, so it cannot be its own oracle. The
same instruction bytes are emitted twice: as the `.so` the C# loader consumes,
and inside a bare-metal ELF that QEMU executes on a real AArch64 core model
(`-M virt -cpu cortex-a57`). QEMU also disassembles what it runs (`-d in_asm`),
which is how the encodings were checked instruction by instruction:

```
0x40001008:  aa1f03e9  mov      x9, xzr
0x4000100c:  f100003f  cmp      x1, #0
0x40001010:  540000c0  b.eq     #0x40001028
0x40001014:  f940000a  ldr      x10, [x0]
0x40001018:  8b0a0129  add      x9, x9, x10
```

## Results

Both paths agree, on the same bytes:

| Function | Expected | qemu-system-aarch64 | C# interpreter |
|---|---|---|---|
| `sum_words(1..8)` | 36 | 36 | 36 |
| `fib(30)` | 832040 | 832040 | 832040 |
| `mix(7,5)` | 30 | 30 | 30 |
| `host_sum(19,23)` via `SVC` | 42 | — (host call not wired in QEMU) | 42 |

Sizes: `guest.so` 4,736 B with 124 B of code (31 instructions, 4 symbols);
`harness.elf` 5,032 B with 456 B of code. The whole four-call self-test executes
**285 guest instructions**, and `run.sh` fails if any of the four results differ.

### Throughput

`fib(n)` is a 7-instruction loop, so instruction counts are exact. Each side runs
three times and the fastest run is reported, because sustained interpretation
throttles this 15 W laptop (one cold 8 M run measured 5.9 s before the policy was
added).

| n | Guest instructions | Interpreter | Native C# | Slowdown | Rate |
|---|---|---|---|---|---|
| 2,000,000 | 14,000,006 | 1.13 s | 1.02 ms | **~1,100x** | 12.4 MIPS |
| 8,000,000 | 56,000,006 | 4.58 s | 3.91 ms | **~1,170x** | 12.2 MIPS |

Both columns move by about 10% between runs on this laptop, so read the
table as one representative run, not three significant figures. 12 MIPS is the
honest number for a switch-based interpreter on this i5-5300U.
For scale: QEMU's TCG on the same machine is roughly an order of magnitude faster
per guest instruction, and a good JIT lands another order below native — 30-100x,
not 1x.

## What the experiment already proves

The `host_sum` symbol traps through `SVC #0` with a selector in `x16`, and the
host answers `a + b`. That is the whole "library wrapping" mechanism in
miniature: the guest carries no implementation, it calls out, and the host
services it. A real bridge does exactly this for `libc`, `libm` and friends
instead of translating them.

It also shows the size of the gap. What is implemented is 14 mnemonics:

`MOVZ`, `MOV` (ORR alias), `ADD`/`SUB`/`ADDS`/`SUBS` immediate, `ADD`/`SUB`/`CMP`
register (LSL #0 only), `MUL`/`MADD`, `LDR`/`STR` 64-bit offset, `LDRB`/`STRB`,
`UBFM` (which covers `LSL`/`LSR`/extract), `ADR`, `B`, `BL`, `B.cond`, `CBZ`,
`CBNZ`, `RET`, `SVC`.

## What a real ARM64 bridge still needs

| Missing piece | Why it is not a weekend |
|---|---|
| Instruction coverage | ARM64 has hundreds of encoding forms. The 14 above cover integer arithmetic; there is no NEON/SIMD, no floating point, no atomics (`LDXR`/`STXR`), no `LDP`/`STP`, no 32-bit forms, no `MOVK`/`ADRP`. Unity and every modern app lean on NEON. |
| JIT backend | Interpreting is ~1000x slower than native. A translator must decode to an IR, allocate x86 registers, model flags and the memory ordering rules, emit blocks into executable memory, and invalidate them on self-modifying code. That is years of work by itself. |
| Real ELF loading | `guest.so` has no relocations. Real libraries need `R_AARCH64_RELATIVE`/`GLOB_DAT`/`JUMP_SLOT`, symbol versioning, `DT_INIT_ARRAY` and the bionic linker namespace. |
| bionic and TLS | `TPIDR_EL0`, `pthread`, `futex`, signal frames, `setjmp`/unwind tables. Apps crash in the unwinder long before they render a frame. |
| ART integration | Android does not call translators directly: ART loads a `libnativebridge` implementation through `android::NativeBridgeCallbacks` (initialize, loadLibrary, getTrampoline, isSupported...). A version mismatch fails at app start. |
| Graphics | The app's `libGLESv2.so` is ARM code talking to an ARM driver. Translation does not help, because the host has no matching driver; that is why emulators ship a guest GPU stack instead. |
| Testing | Thousands of apps, each a compatibility bug. |

A realistic estimate for broad compatibility is **5-15 engineer-years** with a
team, and it still would not run Vulkan-only games on an HD 5500. Google's
`libndk_translation` exists precisely because this is not a solo project.

## Conclusion

The mechanism is demonstrably implementable: 31 hand-assembled instructions run
identically on an AArch64 core and in this interpreter. The *product* is not. The
distance from "add two registers" to "run an APK" is measured in years, not
weeks, and the honest route to ARM64 support remains a prebuilt Android image
that ships Google's translator, with all the licensing that implies.

## Reproduce

```bash
./research/arm64-bridge/run.sh          # benchmark with n = 8,000,000
./research/arm64-bridge/run.sh 2000000  # or any n
```

Needs Python 3, the .NET Framework compiler, and a QEMU build containing
`qemu-system-aarch64` (the `build/vm` tree, not shipped in the repository). If
QEMU is absent the harness step is skipped and the interpreter still runs.
Output lands in `build/arm64-lab/`.
