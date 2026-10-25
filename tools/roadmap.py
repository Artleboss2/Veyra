#!/usr/bin/env python3
"""Render docs/roadmap.png: Veyra's project timeline.

The image is generated rather than hand-drawn so the roadmap and the README
cannot drift apart: edit the lists below, run

    python tools/roadmap.py

and commit the new docs/roadmap.png. Flat zinc palette, one accent, no
gradients - the same rules the application window follows.
"""

import os
import sys

from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
OUT = os.path.join(ROOT, "docs", "roadmap.png")

BG = (24, 24, 27)
CARD = (31, 31, 35)
CARD_NEXT = (27, 27, 30)
BORDER = (63, 63, 70)
TEXT = (228, 228, 231)
SUBTLE = (212, 212, 216)
MUTED = (161, 161, 170)
DIM = (113, 113, 122)
ACCENT = (88, 101, 242)
FONT = "C:/Windows/Fonts/segoeui.ttf"
FONT_SB = "C:/Windows/Fonts/seguisb.ttf"

W, H = 1840, 832
MARGIN = 64
LABEL_W = 160

SHIPPED = [
    ("24 Oct", "Host scan", "i5-5300U, 8 GB, no WHPX; toolchain fixed"),
    ("24 Oct", "UI shell", "C# single-bitmap window, 52 MB working set"),
    ("25 Oct", "VM runner", "Android-x86 9 boots under QEMU TCG"),
    ("25 Oct", "APK audit", "AXML, ARSC and DEX verdicts before launch"),
    ("25 Oct", "Slim installer", "194 KB setup; 1.1 GiB runtime from GitHub"),
    ("25 Oct", "Beta releases", "0.1 runtime; 0.1.1 licensed build"),
    ("25 Oct", "Limits documented", "Why ARM64 cannot run in the x86 image"),
    ("25 Oct", "ARM64 proof", "31 hand-built instructions, ~12 MIPS"),
]

NEXT = [
    ("0.2", "Persistence", "Keep installed packages, app data and settings across VM restarts"),
    ("0.3", "Snapshots", "Save and resume VM state so relaunch skips the multi-minute boot"),
    ("0.4", "ARM64 spike", "Boot Bliss OS 16.9.7 (Android 13, libndk) on TCG, measure, review licence"),
]

LATER = [
    ("1.0", "Signed build", "Code signing, reproducible builds and a Windows host test matrix"),
    ("1.x", "ARM64 GA", "Only if the spike is usable on two cores and the licence clears"),
]


def font(size, semibold=False):
    return ImageFont.truetype(FONT_SB if semibold else FONT, size)


def wrap(draw, text, f, width):
    words, lines, line = text.split(), [], ""
    for word in words:
        candidate = (line + " " + word).strip()
        if draw.textlength(candidate, font=f) <= width:
            line = candidate
        else:
            if line:
                lines.append(line)
            line = word
    if line:
        lines.append(line)
    return lines


def dashed_rect(draw, box, color, radius=6, dash=6, gap=5):
    """Rounded rectangle with a dashed outline, corner arcs approximated."""
    x0, y0, x1, y1 = box
    for y in (y0, y1):
        x = x0 + radius
        while x < x1 - radius:
            draw.line((x, y, min(x + dash, x1 - radius), y), fill=color)
            x += dash + gap
    for x in (x0, x1):
        y = y0 + radius
        while y < y1 - radius:
            draw.line((x, y, x, min(y + dash, y1 - radius)), fill=color)
            y += dash + gap


def chip(draw, x, y, w, h, meta, title, detail, planned=False):
    box = (x, y, x + w, y + h)
    draw.rounded_rectangle(box, radius=6, fill=CARD_NEXT if planned else CARD)
    if planned:
        dashed_rect(draw, box, BORDER)
    else:
        draw.rounded_rectangle(box, radius=6, outline=BORDER, width=1)

    pad = 16
    if planned:
        draw.text((x + pad, y + 14), meta.upper(), font=font(10, True), fill=DIM)
        tag = "NEXT" if meta.startswith("0.") else "LATER"
        tw = draw.textlength(tag, font=font(10, True))
        draw.text((x + w - pad - tw, y + 14), tag, font=font(10, True), fill=MUTED)
    else:
        draw.ellipse((x + pad, y + 18, x + pad + 6, y + 24), fill=ACCENT)
        draw.text((x + pad + 12, y + 13), meta, font=font(11, False), fill=MUTED)

    ty = y + 42
    for line in wrap(draw, title, font(15, True), w - 2 * pad)[:2]:
        draw.text((x + pad, ty), line, font=font(15, True), fill=SUBTLE if planned else TEXT)
        ty += 20

    dy = ty + 6
    for line in wrap(draw, detail, font(12), w - 2 * pad)[:3]:
        draw.text((x + pad, dy), line, font=font(12), fill=MUTED)
        dy += 16


def lane(draw, label, sub, entries, top, planned, widths):
    draw.text((MARGIN, top + 4), label, font=font(12, True), fill=ACCENT if not planned else MUTED)
    draw.text((MARGIN, top + 24), sub, font=font(11), fill=DIM)

    x = MARGIN + LABEL_W
    h = 150
    for i, (meta, title, detail) in enumerate(entries):
        chip(draw, x, top, widths[i], h, meta, title, detail, planned=planned)
        x += widths[i] + 12
    return h


def main():
    img = Image.new("RGB", (W, H), BG)
    d = ImageDraw.Draw(img)

    d.rectangle((MARGIN, 46, MARGIN + 4, 84), fill=ACCENT)
    d.text((MARGIN + 18, 40), "VEYRA", font=font(30, True), fill=TEXT)
    d.text((MARGIN + 18, 82), "Project timeline  \u00b7  what shipped, and what comes next",
           font=font(15), fill=MUTED)

    right = "v0.1.0-alpha"
    d.text((W - MARGIN - d.textlength(right, font=font(15, True)), 46), right,
           font=font(15, True), fill=ACCENT)
    stamp = "updated 25 Oct 2026"
    d.text((W - MARGIN - d.textlength(stamp, font=font(12)), 70), stamp,
           font=font(12), fill=DIM)

    d.line((MARGIN, 122, W - MARGIN, 122), fill=(45, 45, 48), width=1)

    avail = W - 2 * MARGIN - LABEL_W
    gap = 12
    w1 = (avail - gap * (len(SHIPPED) - 1)) // len(SHIPPED)
    widths1 = [w1] * len(SHIPPED)
    y1 = 160
    lane(d, "SHIPPED", "24-25 Oct 2026", SHIPPED, y1, False, widths1)

    cx = MARGIN + LABEL_W // 2
    d.line((cx, y1 + 158, cx, y1 + 196), fill=(63, 63, 70), width=1)
    d.polygon([(cx - 3, y1 + 196), (cx + 3, y1 + 196), (cx, y1 + 202)], fill=(63, 63, 70))

    y2 = 368
    w2 = (avail - gap * 3) // 4
    lane(d, "NEXT", "0.2 - 0.4", NEXT, y2, True, [w2] * len(NEXT))

    d.line((cx, y2 + 158, cx, y2 + 196), fill=(63, 63, 70), width=1)
    d.polygon([(cx - 3, y2 + 196), (cx + 3, y2 + 196), (cx, y2 + 202)], fill=(63, 63, 70))

    y3 = 576
    w3 = w2
    lane(d, "LATER", "1.0+", LATER, y3, True, [w3] * len(LATER))

    d.line((MARGIN, 748, W - MARGIN, 748), fill=(45, 45, 48), width=1)
    foot = ("ARM64 needs Android 13 with Google's libndk_translation; the spike is unverified "
            "and the translator would stay upstream.")
    d.text((MARGIN, 764), foot, font=font(11), fill=DIM)

    counts = "8 shipped   \u00b7   4 next   \u00b7   2 later"
    d.text((W - MARGIN - d.textlength(counts, font=font(11)), 764), counts,
           font=font(11), fill=DIM)

    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    img.save(OUT, "PNG", optimize=True)
    print("wrote %s (%d x %d, %d bytes)" % (os.path.relpath(OUT, ROOT), W, H,
                                            os.path.getsize(OUT)))


if __name__ == "__main__":
    sys.exit(main())
