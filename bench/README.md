# Veyra Bench

A 10 KB APK that measures what an Android guest actually gives an app: heap
headroom, allocation rate, read bandwidth, full-GC pause, object churn and raw
Canvas throughput. It exists so you can pick a RAM budget, run it, and see
whether the machine holds up at that size.

- **Pure Dalvik.** No `lib/` folder, no ARM code, no native libraries, no
  permissions, no resources, no Play Services. It installs on any x86 or ARM
  Android 7+ device and is the safest thing to drop into the Veyra VM.
- **No widget toolkit.** The whole screen is one `View` painting black
  rectangles, white 1 px lines and monospace text on a plain `Canvas`. That is
  deliberate: the numbers describe the guest, not a UI framework.
- **Real memory, not a number in a text box.** When you press RUN it allocates
  the selected budget in 256 KB blocks and touches every page, so the guest has
  to actually commit the memory. If the heap cannot hold it you get
  `FAIL heap cannot hold this budget` instead of a fake result.

## Using it

1. Drag `VeyraBench.apk` into Veyra and press **Run in Veyra VM** (or
   `adb install -r bench/VeyraBench.apk` on any device).
2. Pick a budget with the slider: 32 MB up to 80 % of the app's heap limit
   (the header prints that limit, typically 192–512 MB on Android 9 x86).
3. **RUN** allocates, measures and prints:

   | Line | Meaning |
   |---|---|
   | `alloc` | how fast the guest commits fresh memory, MB/s |
   | `read` | sequential read back over the committed blocks, MB/s |
   | `gc` | one full `System.gc()` with the budget still held, ms |
   | `churn` | short-lived object allocation rate, obj/s |
   | `draw` | offscreen 256×256 Canvas frames per second |
   | verdict | PASS / TIGHT / FAIL for that budget |

   `PASS` needs ≥150 MB/s allocation and ≥20 fps. `TIGHT` is ≥40 MB/s and
   ≥8 fps. Everything else fails. Under QEMU's software emulation expect TIGHT
   or FAIL even at small budgets — that is the honest answer for this hardware.
4. **GC** runs a collect on demand, **CLEAR** empties the log.

The budget is per run: memory is released when the run ends, so you can step the
slider up until it fails.

## Building it

```bash
python tools/build_bench.py
```

Requires the JDK (`javac`, `jarsigner`, `keytool`) and `tools/r8/r8.jar`
(Google's D8 dexer, 21 MB, downloaded once from `maven.google.com`). The script
compiles against the compile-time-only stubs in `bench/stubs`, dexes the app
classes alone (the stubs never enter the APK), writes a binary
`AndroidManifest.xml` with the same AXML writer the fixtures use, packages the
ZIP and signs it with a local self-signed key (`bench/build/`, ignored).

`bench/VeyraBench.apk` is the committed build; the keystore is not, so a fresh
clone produces an APK signed with a new key. Android treats that as a different
app, which is fine for a benchmark.

`bench/test/BenchHarness.java` exercises the engine on the desktop JVM against
the stubs — allocation, read pass, GC, churn, verdict and formatting all run for
real, only Canvas is a no-op:

```bash
javac -cp "bench/build/stubs;bench/build/classes" -d bench/build/harness bench/test/BenchHarness.java
java -cp "bench/build/stubs;bench/build/classes;bench/build/harness" BenchHarness
```
