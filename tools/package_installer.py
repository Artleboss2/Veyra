#!/usr/bin/env python3
"""Append the runtime payload to the Veyra installer bootstrap.

The installer reads a 24-byte footer (magic, zip offset, zip length) from the end
of its own executable and mounts the ZIP through a range stream. No temporary
copy of the 1 GB payload is needed at install time.
"""
import os
import struct
import sys
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
APP = os.path.join(ROOT, "Veyra.exe")
SETUP = os.path.join(ROOT, "VeyraSetup.exe")
UNINST = os.path.join(ROOT, "build", "Uninstall.exe")
RUNTIME = os.path.join(ROOT, "runtime")
PAYLOAD = os.path.join(ROOT, "build", "payload.zip")


def add(zf, source, arcname):
    if not os.path.isfile(source):
        raise SystemExit("missing payload file: " + source)
    zf.write(source, arcname)


def main():
    for required in (APP, SETUP, UNINST):
        if not os.path.isfile(required):
            raise SystemExit("missing build artefact: " + required)
    if not os.path.isdir(RUNTIME):
        raise SystemExit("runtime/ is missing; assemble QEMU, Android and adb first")

    with zipfile.ZipFile(PAYLOAD, "w", zipfile.ZIP_STORED, allowZip64=True) as zf:
        add(zf, APP, "Veyra.exe")
        add(zf, UNINST, "Uninstall.exe")
        add(zf, os.path.join(ROOT, "README.md"), "README.md")
        for base, dirs, files in os.walk(RUNTIME):
            dirs.sort()
            files.sort()
            for name in files:
                full = os.path.join(base, name)
                rel = os.path.relpath(full, RUNTIME)
                add(zf, full, "runtime/" + rel.replace(os.sep, "/"))

    with open(SETUP, "rb") as f:
        bootstrap = f.read()
    if bootstrap[-24:-16] == b"VYRAZIP1":
        raise SystemExit("VeyraSetup.exe already has a payload; rebuild the bootstrap first")

    offset = len(bootstrap)
    with open(SETUP, "wb") as out:
        out.write(bootstrap)
        with open(PAYLOAD, "rb") as p:
            while True:
                chunk = p.read(4 * 1024 * 1024)
                if not chunk:
                    break
                out.write(chunk)
        out.write(struct.pack("<8sqq", b"VYRAZIP1", offset, os.path.getsize(PAYLOAD)))

    os.remove(PAYLOAD)
    print("bundled installer: %.2f GB" % (os.path.getsize(SETUP) / (1024.0 ** 3)))


if __name__ == "__main__":
    main()
