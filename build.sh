#!/usr/bin/env bash
# Builds Veyra.exe, Uninstall.exe and VeyraSetup.exe with the .NET Framework 4.8
# compiler already present on this machine. No downloads, no NuGet, no MSBuild.
# If runtime/ exists, the installer is turned into a self-extracting bundle.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
CSC="/c/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe"
OUT="$ROOT/Veyra.exe"
SETUP="$ROOT/VeyraSetup.exe"

if [ ! -x "$CSC" ]; then
  echo "FATAL: csc.exe not found at $CSC" >&2
  exit 3
fi

rm -f "$OUT" "$SETUP" "$ROOT/build/Uninstall.exe"

echo "==> compiling Veyra.exe"
"$CSC" -nologo -target:winexe -optimize+ -platform:anycpu -warn:2 \
  -out:"$(cygpath -w "$OUT")" \
  -r:System.dll -r:System.Core.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll \
  -r:System.Management.dll -r:System.IO.Compression.dll -r:System.IO.Compression.FileSystem.dll \
  -recurse:"$(cygpath -w "$ROOT/src")\\\\*.cs"

if [ ! -s "$OUT" ]; then
  echo "FATAL: compiler produced no output" >&2
  rm -f "$OUT"
  exit 4
fi

echo "==> compiling Uninstall.exe"
mkdir -p "$ROOT/build"
"$CSC" -nologo -target:winexe -optimize+ -platform:anycpu -warn:2 \
  -out:"$(cygpath -w "$ROOT/build/Uninstall.exe")" \
  -r:System.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll \
  "$(cygpath -w "$ROOT/installer/Uninstaller.cs")"

echo "==> compiling VeyraSetup.exe bootstrap"
"$CSC" -nologo -target:winexe -optimize+ -platform:anycpu -warn:2 \
  -out:"$(cygpath -w "$SETUP")" \
  -r:System.dll -r:System.Core.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll \
  -r:System.IO.Compression.dll -r:System.IO.Compression.FileSystem.dll \
  "$(cygpath -w "$ROOT/installer/Installer.cs")" \
  "$(cygpath -w "$ROOT/src/Theme.cs")"

if [ ! -s "$SETUP" ]; then
  echo "FATAL: installer build produced no output" >&2
  rm -f "$SETUP"
  exit 5
fi

if [ -d "$ROOT/runtime" ]; then
  echo "==> bundling runtime payload"
  if ! python "$(cygpath -w "$ROOT/tools/package_installer.py")"; then
    echo "FATAL: packaging failed; removing the incomplete installer" >&2
    rm -f "$SETUP"
    exit 6
  fi
else
  echo "==> runtime/ not present: installer is a bootstrap only"
fi

ls -lh "$OUT" "$SETUP"
echo "==> build ok"
