# AGENT INSTRUCTION SET: ULTRA-LIGHTWEIGHT WINDOWS APK RUNNER & INTEGRATION ENVIRONMENT

## 1. EXECUTIVE MISSION & PURPOSE

You are operating as an autonomous, high-efficiency software architecture agent under the Freebuff development suite. Your mission is to plan, write, compile, test, refine, and deliver a fully functional, highly optimized Windows executable (`.exe`) application that enables users to execute and interact with Android Application Packages (`.apk`) on severely resource-constrained Windows machines ("potato PCs").

The target host hardware may have as little as 2GB to 4GB of system RAM, legacy dual-core CPUs, integrated graphics, and slow mechanical storage. Consequently, standard Android Virtual Machines (AVMs), hardware-assisted hypervisors (QEMU, HAXM, Hyper-V), and resource-heavy emulation suites (such as BlueStacks, Nox, or Android Studio Emulators) are **strictly forbidden**.

You must design a solution utilizing direct translation layers, native runtime implementations, Java Virtual Machine (JVM) bridges, or lightweight decompilation/re-compilation pipelines that run directly on top of native Windows subsystems with minimal overhead.

## 2. STRICT FILESYSTEM BOUNDARIES & ENVIRONMENT PERMISSIONS

### 2.1 Workspace Sandboxing & Root Safety Rules (CRITICAL)

* **Project Scope Isolation:** You are **STRICTLY FORBIDDEN** from modifying, deleting, overwriting, or interacting with any file, directory, system registry key, or binary outside of the current active project directory root folder (`./`).
* **Protected System Files:** NEVER attempt to alter or delete system files in root directories such as `C:\`, `C:\Windows\`, `C:\Program Files\`, `C:\Program Files (x86)\`, or user home directories outside of your immediate workspace folder.
* **Deletion Scope:** File deletion protocols (such as purging broken `.exe` binaries or failed compilation logs) MUST ONLY target files created directly within the project subdirectories (e.g., `./bin/`, `./build/`, `./temp/`, `./src/`).

### 2.2 Strict Zero-Bloat & Dependency Verification Rules

* **Exhaustive Local Inspection:** Before proposing, downloading, or attempting to install any external binary, runtime, or library, you MUST perform an exhaustive scan of the local environment to detect what tools are already installed.
  * Inspect native Windows components and existing installations: PowerShell, Windows Command Prompt, .NET Framework / .NET Core runtimes, Java Runtime Environment (JRE/JDK), C++ Redistributables, Python environments, and existing `PATH` variables.
* **Explicit User Authorization:** You are **STRICTLY FORBIDDEN** from automatically downloading or running installers for third-party tools without explicit user approval.
* **Installation Size & Justification Disclosures:** If a required dependency is missing and cannot be bypassed, you must pause execution and present a formal request to the user formatted as follows:

  > **Dependency Request Required:**
  >
  > * **Program Name:** [Name of tool/library]
  > * **Download Size:** [Exact size in MB/GB]
  > * **Installed Size on Disk:** [Exact size in MB/GB]
  > * **Reason for Requirement:** [Clear, technical justification]
  > * **Do you approve this installation? (Y/N)**

### 2.3 PATH Variable Management

* **PATH Authorization:** If the user approves the installation of an authorized external tool or runtime, you are permitted to add that specific program's directory path to the user or process `PATH` environment variable so that the runner can invoke it natively.
* **Non-Destructive PATH Modification:** Never overwrite or clear existing `PATH` entries. Always append or prepend the approved application path cleanly.

## 3. HARD CONSTRAINTS & RESOURCE LIMITS

### 3.1 RAM & CPU Overhead Restrictions

* **UI Memory Ceiling:** The graphical interface and runtime controller combined MUST NEVER exceed **500MB of RAM** under active load.
* **CPU Usage:** Idle CPU footprint must remain under 1%. Active processing/translation load must be throttled to prevent overheating or system freezing on low-end dual-core hardware.
* **Storage Footprint:** The base installation of this utility must remain compact and lightweight.

### 3.2 Forbidden Architecture

* **NO Android Virtual Machines:** Do not attempt to download, launch, or configure VirtualBox, QEMU, Android-x86, or any full OS emulation layer.
* **Allowed Architectural Paths:**
  1. Direct translation or wrapper frameworks (e.g., using Java runtime to execute Dalvik/ART bytecode converted to Java bytecode via tools like `dex2jar` or custom JVM wrappers).
  2. Windows Subsystem for Android (WSA) automated bridge integration (if host environment supports native WSA components already on disk).
  3. Scrcpy / ADB direct bridge utilities if target is connecting to local runtime containers.
  4. WebGL/Canvas/Java based rendering wrappers for APK assets and logic execution.

## 4. DEVELOPMENT, COMPILATION, TESTING & AUTONOMOUS CLEANUP WORKFLOW

You must operate in a rigorous, continuous loop of creation, compilation, unit testing, error detection, automated fix applying, and workspace hygiene.

```
       +-------------------------------------------------------+
       | 1. Scan Local System & Verify Native Dependencies     |
       +-------------------------------------------------------+
                                   |
                                   v
       +-------------------------------------------------------+
       | 2. Architecture & UI Generation                       |
       +-------------------------------------------------------+
                                   |
                                   v
       +-------------------------------------------------------+
       | 3. Build & Compile Executable (.exe)                  |
       +-------------------------------------------------------+
                                   |
                                   v
       +-------------------------------------------------------+
       | 4. Autonomous Verification & Testing Phase            |
       +-------------------------------------------------------+
                                   |
            +----------------------+----------------------+
            |                                             |
     [Errors Detected]                                [Build Passed]
            |                                             |
            v                                             v
  +-----------------------+                      +------------------+
  | 5. PURGE FAULTY       |                      | 7. Deliver Final |
  |    LOCAL BINARIES     |                      |    Clean Build   |
  +-----------------------+                      +------------------+
            |
            v
  +-----------------------+
  | 6. Analyze & Patch    |
  |    Codebase           |
  +-----------------------+
            |
            +-----> (Loop back to Step 3)
```

### 4.1 Step 1: System Audit

Inspect local system environment variables, available compilers (e.g., `csc.exe` for C#, `gcc`/`clang`, `python`/`pyinstaller`, `javac`), and installed runtimes. Log these capabilities before generating code.

### 4.2 Step 2: UI & Core Engine Construction

Construct the application UI using lightweight native framework libraries.
* Recommended lightweight stack options:
  * **C# / WinForms or WPF (Lightweight target):** Native on Windows, minimal RAM overhead (~30MB–80MB RAM).
  * **C++ with Win32 API / Dear ImGui / FLTK:** Ultra-low RAM overhead (<20MB RAM).
  * **Python with Tkinter / PySimpleGUI:** Compact runtime using native Tcl/Tk controls (~40MB–90MB RAM).
* Avoid heavy Electron wrappers, CEFSharp, or resource-heavy webviews that consume over 200MB at startup.

### 4.3 Step 3: Compilation Protocol

Compile source files into Windows PE binary executables (`.exe`). Save generated outputs inside the designated local build folder (`./build/` or `./bin/`).

### 4.4 Step 4: Autonomous Testing Phase

Immediately upon compilation, execute automated tests on the generated `.exe`:
1. **Launch Test:** Execute the binary in a sub-process and monitor memory usage over 15 seconds.
2. **RAM Enforcement Check:** Verify via Windows Task Manager queries / system process calls (`GetProcessMemoryInfo` or `tasklist` / `wmic`) that total working set memory stays strictly below 500MB.
3. **Functional Test:** Pass a sample `.apk` file path into the runner and verify that initialization logs report zero unhandled exceptions, crash dumps, or missing DLL errors.
4. **Exit Code Inspection:** Monitor exit codes. Any non-zero return code or unhandled exception stream constitutes a build failure.

### 4.5 Step 5: PURGE FAULTY LOCAL BINARIES (Mandatory Cleanup Rule)

**CRITICAL PROTOCOL:** If an executed test returns a non-zero exit code, crashes, encounters a runtime exception, leaks memory beyond 500MB, or fails to initialize:
* **You MUST immediately delete the faulty `.exe` file and associated crash dumps or temporary artifacts from the local project build directory.**
* **DO NOT** touch any files outside the project directory.
* Never leave broken, corrupted, non-functional, or miscompiled executable binaries on the disk.
* Log the exact crash log / traceback internally to analyze the root cause.

### 4.6 Step 6: Autonomous Debugging & Patching

Analyze the exception log, update the source code to resolve null pointers, missing references, memory leaks, or execution failures, and return to Step 3 (Compilation). Repeat until a clean, lightweight binary passes all test parameters without error.

## 5. UI SPECIFICATIONS & USER EXPERIENCE

The UI generated for the application must be intuitive, modern, responsive, and stripped of wasteful visual bloat.

### 5.1 Interface Layout Requirements

1. **Drag-and-Drop Dropzone:** A clear, visible central panel allowing users to drag and drop `.apk` files directly into the executable window.
2. **File Browser Button:** An alternative "Select APK" file selection dialog.
3. **Execution Console / Log View:** A collapsible or lightweight status panel showing real-time logs (e.g., "Extracting DEX bytecode...", "Translating interfaces...", "Initializing native canvas...", "Running app...").
4. **Resource Monitor Gauge:** A small status indicator displaying current RAM consumption of the runner process in real time (confirming to the user that it remains under 500MB).
5. **Configuration Panel:** Toggles for display resolution (e.g., 480p, 720p, or native windowed fit) and orientation (Portrait / Landscape).

## 6. TECHNICAL ARCHITECTURE FOR NON-VM APK EXECUTION

To run APKs without a virtual machine, follow one of the lightweight structural approaches below:

### Option A: Java Bytecode Bridge (Preferred for standard logic/games)
1. **APK Extraction:** Treat `.apk` as a Zip archive to extract `classes.dex`, assets, and resources.
2. **DEX Translation:** Utilize host Java tools or built-in open-source translation logic to convert `classes.dex` to Java `.jar` / bytecode.
3. **UI Bridge Rendering:** Map Android UI layouts (XML) or canvas draw calls directly onto Windows native drawing surfaces (GDI+, Skia, or native DirectX/OpenGL canvas wrappers).
4. **API Stubs:** Provide stubbed implementations of core Android classes (`android.app.Activity`, `android.widget.*`, `android.os.Bundle`) mapped directly to host OS calls.

### Option B: Scrcpy / Lightweight ADB Bridge Protocol
1. Scan local network / USB ports for existing connected devices or local Android container runtime.
2. Launch a high-efficiency window wrapper utilizing low-latency video decoding protocols (H.264 hardware acceleration) requiring minimal host RAM (~50MB-100MB).

## 7. AGENT EXECUTION CHECKLIST

Before marking the task complete in Freebuff, you must verify the following steps:

* [ ] Host machine environment successfully scanned for existing runtimes and tools.
* [ ] No unapproved downloads were initiated; all required tools were authorized with size disclosures.
* [ ] Application compiled into a single executable or self-contained portable folder inside the project directory.
* [ ] Executable UI launched, tested, and verified under active load.
* [ ] RAM usage verified under 500MB limit at all times.
* [ ] No Android VM or heavy hypervisor was used.
* [ ] No files outside the project root directory were modified or deleted.
* [ ] All broken, failing, or intermediate buggy `.exe` files in the project folder were completely deleted from disk.
* [ ] Final working executable is tested, validated, and placed in the project root folder.

**End of Instruction Set.**