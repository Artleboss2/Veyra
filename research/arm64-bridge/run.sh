#!/usr/bin/env bash
# Build and run the ARM64 bridge proof of concept.
#
#   ./research/arm64-bridge/run.sh [bench-n]
#
# Steps: assemble guest.so + harness.elf from scratch (no ARM toolchain),
# run the harness on qemu-system-aarch64 as ground truth, compile the C#
# interpreter, self-test it, then benchmark it against a native C# loop.
set -u

here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/../.." && pwd)"
out="$root/build/arm64-lab"
csc="/c/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe"
n="${1:-8000000}"
cd "$root" || exit 1
mkdir -p "$out"

fail() { echo "FAIL: $*" >&2; exit 1; }
win() { cygpath -w "$1" 2>/dev/null || printf '%s' "$1"; }

# qemu-system-aarch64 only exists in the build tree, and it relies on its
# working directory for part of its DLL set, so keep the first cwd that starts.
qemu_bin="$root/build/vm/qemu/qemu-system-aarch64.exe"
qemu_cwd=""
if [ -x "$qemu_bin" ]; then
  for d in "$root/build/vm/qemu" "$root/runtime/qemu"; do
    if (cd "$d" && "$qemu_bin" --version >/dev/null 2>&1); then qemu_cwd="$d"; break; fi
  done
fi

echo "== assemble"
python "$here/gen_guest.py" --out "$out" || fail "generator"

echo
echo "== ground truth on qemu-system-aarch64 (AArch64 core model)"
if [ -n "$qemu_cwd" ]; then
  # Semihosting SYS_WRITE0 goes to the guest console, which -nographic sends to
  # stderr; -d in_asm also records what the core decoded, which is how the
  # hand-written encoding was verified instruction by instruction.
  ( cd "$qemu_cwd" && "$qemu_bin" -M virt -cpu cortex-a57 -m 128 \
      -nographic -monitor none -serial mon:stdio \
      -semihosting-config enable=on,target=native \
      -kernel "$out/harness.elf" -d in_asm -D "$out/qemu.log" ) \
    </dev/null >"$out/qemu.out" 2>"$out/qemu.err" &
  pid=$!
  for _ in $(seq 1 40); do
    kill -0 $pid 2>/dev/null || break
    sleep 0.25
  done
  kill -0 $pid 2>/dev/null && kill $pid 2>/dev/null
  wait $pid 2>/dev/null
  cat "$out/qemu.out" "$out/qemu.err" | tr -d '\r' >"$out/qemu.console"
  echo "--- harness output (hex: sum_words, fib(30), mix(7,5))"
  grep -E '^[0-9a-f]{16}$' "$out/qemu.console" || fail "harness produced no numbers"
  python - "$out/qemu.console" <<'PY' || fail "harness disagrees with the expected values"
import re, sys
vals = [int(x, 16) for x in re.findall(r'^[0-9a-f]{16}$', open(sys.argv[1]).read(), re.M)]
want = [36, 832040, 30]
print("    decoded:", " ".join("%d(%s)" % (g, "ok" if g == w else "MISMATCH expected %d" % w)
                                for g, w in zip(vals, want)))
sys.exit(0 if vals == want else 1)
PY
else
  echo "    skipped: qemu-system-aarch64 not usable here (build/vm is not committed)"
fi

echo
echo "== interpret"
# csc is a native Windows binary; hand it Windows paths or it mangles them.
"$csc" -nologo -platform:x64 -optimize+ "-out:$(win "$out/Guest.exe")" "$(win "$here/Guest.cs")" \
  || fail "csc"
"$out/Guest.exe" --dir "$out" || fail "self-test"
"$out/Guest.exe" --dir "$out" --bench "$n" || fail "bench"

echo
echo "artefacts in $out"
