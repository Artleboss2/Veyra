#!/usr/bin/env python3
"""Build the runtime asset that the installer downloads.

The Android image, QEMU and adb are ~1.1 GB. They never belong inside
VeyraSetup.exe: the installer stays a few hundred kilobytes and pulls this
archive from a GitHub release, then verifies its SHA-256 before unpacking.

Outputs, in build/release/:

  veyra-runtime.zip   runtime/ tree, stored (the ISO is already compressed)
  runtime.json        name, version, byte count, SHA-256 and download URL
"""
import hashlib
import json
import os
import sys
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RUNTIME = os.path.join(ROOT, "runtime")
OUT_DIR = os.path.join(ROOT, "build", "release")
ASSET = os.path.join(OUT_DIR, "veyra-runtime.zip")
MANIFEST = os.path.join(OUT_DIR, "runtime.json")
REPO = "https://github.com/Artleboss2/Veyra"
VERSION = "beta-0.21"
TAG = "Beta-0.21"

REQUIRED = [
    os.path.join("adb", "adb.exe"),
    os.path.join("android", "android-x86.iso"),
    os.path.join("android", "initrd.img"),
    os.path.join("android", "kernel"),
    os.path.join("qemu", "qemu-system-x86_64.exe"),
]


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        while True:
            chunk = f.read(4 * 1024 * 1024)
            if not chunk:
                break
            h.update(chunk)
    return h.hexdigest()


def main():
    if not os.path.isdir(RUNTIME):
        raise SystemExit("runtime/ is missing; assemble QEMU, Android and adb first")
    for rel in REQUIRED:
        if not os.path.isfile(os.path.join(RUNTIME, rel)):
            raise SystemExit("runtime is incomplete, missing " + rel)

    os.makedirs(OUT_DIR, exist_ok=True)
    if os.path.exists(ASSET):
        os.remove(ASSET)

    entries = 0
    unpacked = 0
    with zipfile.ZipFile(ASSET, "w", zipfile.ZIP_STORED, allowZip64=True) as zf:
        for base, dirs, files in os.walk(RUNTIME):
            dirs.sort()
            files.sort()
            for name in files:
                full = os.path.join(base, name)
                rel = os.path.relpath(full, RUNTIME)
                arc = "runtime/" + rel.replace(os.sep, "/")
                zf.write(full, arc)
                entries += 1
                unpacked += os.path.getsize(full)

    size = os.path.getsize(ASSET)
    digest = sha256(ASSET)
    url = REPO + "/releases/download/" + TAG + "/veyra-runtime.zip"
    manifest = {
        "schema": 1,
        "name": "Veyra Android runtime",
        "version": VERSION,
        "file": "veyra-runtime.zip",
        "bytes": size,
        "sha256": digest,
        "entries": entries,
        "unpackedBytes": unpacked,
        "url": url,
        "homepage": REPO,
    }
    with open(MANIFEST, "w", encoding="ascii", newline="\n") as f:
        json.dump(manifest, f, indent=2)
        f.write("\n")

    print("asset    %s" % ASSET)
    print("bytes    %d (%.2f GB)" % (size, size / (1024.0 ** 3)))
    print("entries  %d" % entries)
    print("sha256   %s" % digest)
    print("manifest %s" % MANIFEST)


if __name__ == "__main__":
    main()
