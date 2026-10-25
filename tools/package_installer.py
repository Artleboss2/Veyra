#!/usr/bin/env python3
"""Append the small application payload to the Veyra installer bootstrap.

Only Veyra.exe, Uninstall.exe and the README travel inside VeyraSetup.exe
(~200 KB). The 1.1 GB Android runtime is a release asset the installer
downloads and verifies at install time, so the installer stays small and a
new app build never requires re-uploading the image.

The installer reads a 24-byte footer (magic, zip offset, zip length) from the
end of its own executable and mounts the ZIP through a range stream.
"""
import os
import struct
import sys
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
APP = os.path.join(ROOT, "Veyra.exe")
SETUP = os.path.join(ROOT, "VeyraSetup.exe")
UNINST = os.path.join(ROOT, "build", "Uninstall.exe")
PAYLOAD = os.path.join(ROOT, "build", "app-payload.zip")
FOOTER = b"VYRAZIP1"
MAX_PAYLOAD = 8 * 1024 * 1024


def main():
    for required in (APP, SETUP, UNINST):
        if not os.path.isfile(required):
            raise SystemExit("missing build artefact: " + required)

    with zipfile.ZipFile(PAYLOAD, "w", zipfile.ZIP_DEFLATED, allowZip64=True) as zf:
        zf.write(APP, "Veyra.exe")
        zf.write(UNINST, "Uninstall.exe")
        zf.write(os.path.join(ROOT, "README.md"), "README.md")

    payload_size = os.path.getsize(PAYLOAD)
    if payload_size > MAX_PAYLOAD:
        raise SystemExit("application payload is %.1f MB, larger than the %d MB budget"
                         % (payload_size / (1024.0 * 1024.0), MAX_PAYLOAD // (1024 * 1024)))

    with open(SETUP, "rb") as f:
        bootstrap = f.read()
    if bootstrap[-24:-16] == FOOTER:
        raise SystemExit("VeyraSetup.exe already has a payload; rebuild the bootstrap first")

    offset = len(bootstrap)
    with open(SETUP, "wb") as out:
        out.write(bootstrap)
        with open(PAYLOAD, "rb") as p:
            while True:
                chunk = p.read(1024 * 1024)
                if not chunk:
                    break
                out.write(chunk)
        out.write(struct.pack("<8sqq", FOOTER, offset, payload_size))

    os.remove(PAYLOAD)
    print("installer: %.1f KB with a %.1f KB application payload"
          % (os.path.getsize(SETUP) / 1024.0, payload_size / 1024.0))


if __name__ == "__main__":
    main()
