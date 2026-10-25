#!/usr/bin/env python3
"""Draw Veyra's application icon and write assets/veyra.ico.

One flat accent square with a white V, the same mark the window header uses:
no gradient, no glow, nothing that turns to mush at 16 px. Every size is
rendered from a 1024 px master so the small variants stay sharp.
"""
import os
import sys

from PIL import Image, ImageDraw, ImageFont

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ASSETS = os.path.join(ROOT, "assets")
MASTER = 1024
ACCENT = (88, 101, 242, 255)
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
FONT_CANDIDATES = [
    r"C:\Windows\Fonts\segoeuib.ttf",
    r"C:\Windows\Fonts\seguisb.ttf",
    "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",
]


def master():
    img = Image.new("RGBA", (MASTER, MASTER), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle([0, 0, MASTER - 1, MASTER - 1], radius=int(MASTER * 0.22), fill=ACCENT)

    for path in FONT_CANDIDATES:
        if os.path.isfile(path):
            font = ImageFont.truetype(path, int(MASTER * 0.88))
            break
    else:
        raise SystemExit("no bold font found for the icon")
    d.text((MASTER / 2, MASTER * 0.525), "V", font=font, fill=(255, 255, 255, 255), anchor="mm")
    return img


def main():
    base = master()
    os.makedirs(ASSETS, exist_ok=True)

    preview = base.resize((256, 256), Image.LANCZOS)
    preview.save(os.path.join(ASSETS, "veyra-256.png"))

    # Render each size from the master so Pillow stores per-size images instead
    # of one bitmap that Windows rescales by itself.
    frames = [base.resize((s, s), Image.LANCZOS) for s in SIZES]
    frames[-1].save(os.path.join(ASSETS, "veyra.ico"), format="ICO",
                    sizes=[(s, s) for s in SIZES], append_images=frames[:-1])

    ico = os.path.join(ASSETS, "veyra.ico")
    print("icon   %s (%.1f KB, %d sizes)" % (ico, os.path.getsize(ico) / 1024.0, len(SIZES)))
    print("sizes  " + ", ".join(str(s) for s in SIZES))


if __name__ == "__main__":
    main()
