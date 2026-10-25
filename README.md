# VEYRA

**A local Android 9 machine for Windows, in a 137 KB native shell.**

Drop an `.apk`, inspect it, then run it in a bundled Android-x86 VM accelerated by
Windows Hypervisor Platform. No cloud, no accounts, no telemetry, no Electron, no
webview, no AI. A phone over ADB remains a supported alternative.

`Veyra.exe` — the UI and package auditor, .NET Framework 4.8, native GDI painting.
Current interface: **v0.1.0-alpha**.
`VeyraSetup.exe` — a 190 KB installer. It lays down Veyra and adb, then downloads
the Android image and QEMU (≈ 1.1 GB) from the GitHub release and verifies the
archive against the published SHA-256 before unpacking. No admin rights required.

Designed and tested on a **Lenovo ThinkPad X250** (i5-5300U, 8 GB RAM, HD 5500).

---

## What is inside

| Component | Role | Size on disk |
|---|---|---|
| Veyra.exe | Native UI, APK auditor, VM controller | 137 KB |
| QEMU 11.1 (x86_64 only) | Virtual machine engine | ~150 MB |
| Android-x86 9.0-r2 (x86_64) | The Android kernel, framework and UI | ~950 MB |
| adb (Android platform-tools) | Install, launch and shell into the VM or a phone | ~8 MB |

The Android ramdisk in this build is patched to start `adbd` on TCP 5555 and to
disable ADB authentication, so the installer and the app can drive the machine
without touching the Android UI.

## Requirements

- Windows 10/11 x64, 8 GB RAM recommended (2 GB go to Android).
- **Windows Hypervisor Platform enabled** for usable speed:
  `dism /online /enable-feature /featurename:HypervisorPlatform /all` then reboot.
  Without it the VM still runs, but in software emulation — expect a very slow boot.
- Free disk: ~1.2 GB plus the Android data you create.
- No administrator rights to install; elevation is only needed once for WHPX.

Veyra does not trust the feature list alone. At startup it probes WHPX with a
three-second throw-away QEMU process; if that fails, or if a real boot has not
answered adb within 150 seconds, the machine is restarted once in software mode
instead of leaving you in front of a black window. Measured on the X250
(Windows 11 23H2, VBS active): `WinHvPlatform.dll` exists and WHPX accepts a
partition, yet QEMU 11.1 cannot enable QEMU's in-kernel APIC and the Android
cold boot stalls under QEMU's own APIC emulation, so this particular machine
ends up in the software path. On hosts where WHPX boots Android, the same probe
returns true and the accelerated path is used.

## Install

Run `VeyraSetup.exe`, press **Install**:

- installs Veyra and a working `adb.exe` to `%LOCALAPPDATA%\Programs\Veyra`
- downloads `veyra-runtime.zip` (the Android 9 image, QEMU and adb) from the release
  named in `runtime.json`, resumes an interrupted download instead of starting from
  zero, and refuses to unpack an archive whose SHA-256 does not match the manifest
- appends `...\Veyra\runtime\adb` to the **per-user** `PATH` (existing entries are
  never touched, duplicates are skipped, the change is broadcast to new shells)
- creates Start Menu and optional desktop shortcuts
- registers a normal **Apps & features** entry with a working uninstaller
- **Uninstall** removes the app, runtime, shortcuts, settings, registry entry and
  the `PATH` fragment it added

Because adb ships inside the runtime, the app never needs a copy from anywhere
else: `Settings → ADB path` shows `bundled · runtime\adb\adb.exe`.

The installer carries only the three application files; everything else is fetched
at install time. That is why the installer is 190 KB instead of 1.1 GB, and why a
release can update Veyra without re-uploading the image.

It is testable without writing outside the project:

```bash
VeyraSetup.exe --check --online --check-out build/installer-check.json
VeyraSetup.exe --extract build/installer-test
VeyraSetup.exe --test-install build/install-test --manifest <runtime.json URL>
VeyraSetup.exe --shot build/shots/installer.png
```

`--test-install` runs the whole download, verification and unpack into a chosen
folder and deliberately skips the registry, `PATH` and shortcuts, so the slow and
destructive parts stay out of a test run.

## Running an APK

1. Drop an `.apk` on the window (or press `Ctrl+O`).
2. Veyra decodes the manifest, resource table, DEX headers, native ABIs, signature
   and engine fingerprint, then gives an evidence-based verdict. **Details** opens
   the full package page; **Report** writes the same evidence to a text file.
3. Press **Run in VM**. The app starts QEMU (WHPX when available), waits for
   `adbd` on `127.0.0.1:5555`, installs the package and launches its main activity.
   The Android screen is the QEMU window; keyboard and mouse are forwarded. The
   button is disabled when the audit says the x86 machine cannot install the
   package (ARM-only native code, API newer than 28, corrupt archive, or a missing
   runtime), so the UI never offers a launch that is guaranteed to fail.
4. **Bridge** hands the same package to an authorised USB device over ADB instead.

Settings controls guest RAM (1.5 / 2 / 2.5 / 3 GB), acceleration mode, resolution,
orientation, telemetry rate and whether engine detail lines appear in the console.
`Stop VM` closes the machine; closing Veyra also stops it.

## Interface

One compact window in the spirit of a native developer tool, not a dashboard
demo: a blue-black gradient bed, neutral zinc cards (`#1f1f23`), 1px `#2d2d30`
borders, 8px card corners and 6px controls, Segoe UI at 8-13 pt, and one muted
accent (`#5865f2`) reserved for primary actions. Two very slow aurora lights
drift behind the cards; the animation stops when the window loses focus.

- **Header** — product mark, version tag, section tabs, live VM state and the
  primary *Select APK* action.
- **System strip** — four metric cards (CPU load, installed/free memory, GPU and
  driver, storage) redrawn once per telemetry tick.
- **Package panel** — dashed drop zone, or the loaded package with verdict, SDK,
  ABI and signature summary plus Run / Bridge / Report / Details.
- **Runtime panel** — engine, image, acceleration state, storage path and a
  1.5-3 GB guest memory selector.
- **Console** — a persistent `Consolas` transcript at `#0d0d0d`, with timestamps,
  severity colours, a *Verbose* filter for engine/adb detail and wheel scrollback.
  The runner's own working set is shown against the 500 MB ceiling in the same bar.

The whole window is a single cached 32bpp bitmap. Mouse hover is painted live on
top (a blit plus one overlay), so moving the pointer never triggers a full GDI
redraw. Frames rebuild only when data, layout or console content changes.

## Benchmark APK

[`bench/VeyraBench.apk`](bench/VeyraBench.apk) is a 10 KB, dependency-free
benchmark that runs in the VM (and on any Android 7+ device): a black terminal
screen, a RAM slider from 32 MB to 80 % of the app heap, and RUN / GC / CLEAR
buttons. RUN commits the chosen budget page by page, then reports allocation
MB/s, sequential read MB/s, full-GC pause, object churn, offscreen Canvas fps and
a PASS / TIGHT / FAIL verdict for that budget.

It is pure Dalvik — no `lib/`, no ARM code, no permissions, no resources — and the
entire UI is drawn by one `View` on a plain `Canvas`, so nothing in the numbers
comes from a widget toolkit. Source, stubs and the build script live in
[`bench/`](bench/README.md); `python tools/build_bench.py` rebuilds the APK from
scratch with the JDK plus D8.

## Honest limits

- This is Android 9 (2020), **without Google Play Services or the Play Store**.
  Apps that need Play Services, SafetyNet or a Google account will fail or degrade.
- **ARM-only APKs cannot run.** The VM is x86_64; packages that ship only
  `arm64-v8a`/`armeabi-v7a` native code are rejected with
  `INSTALL_FAILED_NO_MATCHING_ABIS`. Pure-Java packages and x86/x86_64 native
  packages work. Veyra says which one you have.
- Graphics are rendered by the CPU under QEMU. Simple apps are usable; 3D games
  are not. This is not BlueStacks and does not pretend to be.
- First boot is slow: Android compiles its framework. With WHPX it is minutes;
  without WHPX it can take a very long time on two cores. Veyra prints progress in
  the console and keeps the UI responsive throughout; the watchdog switches
  acceleration modes by itself.
- Guest RAM (2 GB default) is separate from the app's own ~60 MB working set. The
  500 MB ceiling applies to Veyra itself, not to the Android machine it launches.
- Data is held in RAM in this build, so packages are reinstalled on each start.
- Android's `init` logs `failed to load /system/etc/modules.blacklist` during boot.
  That file is an optional AOSP module blacklist; Android-x86 does not ship it and
  its absence means "no modules blacklisted". It is not a boot failure, and Veyra
  leaves the read-only Android image untouched rather than patching it away.

## Performance of the shell (measured on the X250)

| Metric | Result (re-measured on this machine) |
|---|---|
| Veyra resident working set | 51.7 MB at 6 s, 52.3 MB at 16 s (backdrop and aurora sprites included) |
| Full window rebuild | 20.9-26.7 ms (`--bench`, 13.7-16.9 ms of it drawing) |
| Cached frame paint (hover, scroll step) | 8.1-9.0 ms |
| Animated backdrop | ~6 ms per 90 ms tick, only while the window is focused |
| Idle CPU | 0.03 % of one core with the window unfocused; 2.1-2.7 % if every second forces a rebuild |
| Self-test working set | 15 MB, with no window created |
| Installer | 190 KB (3 embedded files); 245 runtime entries downloaded on install |

The 500 MB ceiling is enforced in code: the memory bar turns amber at 400 MB and
the console records a breach, which the self-test treats as a failure.

The UI caches one full-window bitmap and rebuilds it only when something changes
(new console line, scan results, hover-independent state). Hover feedback is a
small live overlay. Text goes through GDI+ `DrawString` with a cached measurement
table: using `TextRenderer` against a memory bitmap forced a GDI DC switch per
string and made rebuilds eleven times slower on this CPU.

The backdrop is a blue-black diagonal gradient rendered once per window size
(never during interaction) with two premultiplied aurora sprites drifting over it.
The sprites are clipped to the gaps between cards, so nothing is tinted that
should stay neutral, and the 90 ms timer stops as soon as the window loses focus
or is minimised. That is why idle CPU stays at a fraction of a percent.

## Build

Uses the C# compiler shipped with .NET Framework 4.8; no NuGet, no MSBuild:

```bash
```bash
./build.sh            # app, uninstaller, installer with the embedded app payload
./build.sh --runtime  # also build build/release/veyra-runtime.zip and runtime.json
```

`tools/package_installer.py   embeds the app payload
tools/make_runtime_asset.py  release runtime asset + manifest
behind a 24-byte footer, which the installer reads in place through a range stream.
`tools/make_runtime_asset.py` packs `runtime/` into the release asset and writes
`runtime.json` (byte count and SHA-256) beside it.

`assets/veyra.ico` is the application icon: a flat accent square with a white V,
rendered at nine sizes (16-256 px). `build.sh` embeds it in `Veyra.exe`,
`VeyraSetup.exe` and `Uninstall.exe` with `-win32icon`, and the window loads the
same resource for the title bar and taskbar. Regenerate it with
`python tools/make_icon.py` (needs Pillow); the result is committed, so a build
never depends on it.

Assemble `runtime/` from the components (not committed):

```
runtime/qemu/      qemu-system-x86_64.exe, qemu-img.exe, DLLs, share/
runtime/android/   kernel, initrd.img, android-x86.iso
runtime/adb/       adb.exe, AdbWinApi.dll, AdbWinUsbApi.dll
```

Nothing in `runtime/` is committed — binaries never are. Attach the installer and
the runtime asset to one GitHub **Release** (each asset stays under the 2 GB limit):

```bash
gh release create v0.1.0-alpha build/release/veyra-runtime.zip build/release/runtime.json \
  VeyraSetup.exe --title "Veyra v0.1.0-alpha" --notes "First public build."
```

The installer reads `runtime.json` from that tag first and falls back to
`releases/latest`, so a new app build only needs a new installer.

Tooling used to build the image (kept in `build/vm/`, ignored):

- `pack_production.py` — rebuilds Android's ramdisk from `iso/ramdisk.cpio` with
  `service.adb.tcp.port=5555`, `ro.adb.secure=0`, an early `adbd` start and a small
  service that puts the slirp address on the emulated NIC. CPIO modes and offsets
  are preserved; debug-only console services are not included.
- `pack_ramdisk.py` — the debug variant used during bring-up (adds a serial shell).
- `pack_initrd.py` — embeds a patched ramdisk in a custom initrd that extracts it
  before `switch_root`, leaving the original ISO untouched.

## Diagnostics

```bash
Veyra.exe --shot out.png --page dashboard|runner|settings|about [--apk app.apk] [--size 1180x708]
Veyra.exe --selftest build/fixtures/veyra-fixture.apk
Veyra.exe --bench
Veyra.exe --apk app.apk
```

`VEYRA_RUNTIME` overrides the runtime directory; `VEYRA_VMDATA` overrides the VM
data directory. Useful when testing a portable folder.

## Repository layout

```
Veyra.exe                 final UI binary
VeyraSetup.exe            installer, app payload embedded (ignored by git)
src/
  Vm/LocalVm.cs           QEMU launch, WHPX probe, ADB wait/install
  Apk/                    AXML, ARSC, DEX parsers and verdict engine
  Ui/                     native custom-painted UI
  Hardware.cs             host scan and adaptive profile
  NetProbe.cs             keyless speed test + settings
assets/veyra.ico         application icon, embedded in every executable
installer/                installer UI, bundled-archive reader, uninstaller
tools/package_installer.py
.github/workflows/build.yml
```

## Licensing

QEMU is GPLv2, Android-x86 is GPLv2 and adb is Apache-2.0. Their notices are
included in `runtime/` when assembled. Veyra itself ships without a license file;
add one before publishing the source.
