#!/usr/bin/env python3
"""Generates the disk image window background.

Same rule as everything else in assets/: drawn by this file from the cat's own
parts and the cat's own pixel font, never by hand. The cat in the disk image is
literally the cat you are installing, and the caption is set in the same typeface
its speech bubbles use.

Run:  python3 tools/generate_dmg_background.py
Out:  assets/dmg/background.png, background@2x.png, background.tiff
"""

import json
import os
import subprocess
import sys

from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import generate_art as art  # noqa: E402  -- needs HERE on the path first

ROOT = os.path.dirname(HERE)
OUT = os.path.join(ROOT, "assets", "dmg")
THEME_DIR = os.path.join(ROOT, "assets", "themes", "mono")

art.apply_theme("mono")

# The Finder window the disk image opens at. The background must be exactly this
# size, and the icon positions in tools/make-dmg.sh are in the same coordinate
# space -- change one and you must change the other.
W, H = 560, 460

# Nothing important goes below this line.
#
# The toolbar, tab bar and status bar are *global* Finder preferences on modern
# macOS, not per-window ones, so a disk image cannot turn them off -- the flags in
# the .DS_Store are a request Finder is free to ignore. Every one of them eats
# height from the content area, and the background is anchored to its top-left, so
# whatever falls past the bottom is simply not there for that user. The slack
# below absorbs roughly 75px of chrome.
SAFE_H = 370

# Where make-dmg.sh drops the two icons. Repeated here rather than shared because
# the arrow has to be drawn *between* them, and nothing else can be allowed to
# land on them or on their labels.
ICON_Y = 190
APP_X, APPLICATIONS_X = 150, 410

PAPER = (246, 246, 249, 255)
INK = art.PALETTE["outline"]
COAT = art.PALETTE["coat"]


def text(line, scale=1, color=INK):
    """Sets one line in the cat's own pixel font.

    Reads the font strip the art pipeline already wrote, so there is exactly one
    typeface in this project and the disk image cannot drift from the speech
    bubbles.
    """
    atlas = json.load(open(os.path.join(THEME_DIR, "cat.json")))
    f = atlas["font"]
    sheet = Image.open(os.path.join(THEME_DIR, f["file"]))

    def glyph(ch):
        return f["glyphs"].get(ch) or f["glyphs"][f["fallback"]]

    width = 0
    for ch in line:
        width += f["space"] if ch == " " else glyph(ch)["w"]
        width += f["tracking"]
    width = max(width - f["tracking"], 1)

    img = Image.new("RGBA", (width, f["cell_h"]), (0, 0, 0, 0))
    x = 0
    for ch in line:
        if ch == " ":
            x += f["space"] + f["tracking"]
            continue
        g = glyph(ch)
        for dy in range(f["cell_h"]):
            for dx in range(g["w"]):
                if sheet.getpixel((g["x"] + dx, dy))[3] > 128:
                    img.putpixel((x + dx, dy), color)
        x += g["w"] + f["tracking"]

    if scale > 1:
        img = img.resize((img.width * scale, img.height * scale), Image.NEAREST)
    return img


def arrow(length, thickness=6, head=14):
    """A chunky pixel arrow, drawn on the same grid as everything else.

    Deliberately blunt: a smooth vector arrow next to pixel art reads as a mistake,
    and the whole point of this window is that it looks like the thing being
    installed.
    """
    img = Image.new("RGBA", (length, head * 2 + 1), (0, 0, 0, 0))
    px = img.load()
    mid = img.height // 2
    for x in range(length - head):
        for y in range(mid - thickness // 2, mid + thickness // 2 + 1):
            px[x, y] = COAT
    for i in range(head + 1):
        x = length - head + i - 1
        if x < 0 or x >= length:
            continue
        for y in range(mid - (head - i), mid + (head - i) + 1):
            px[x, y] = COAT
    return img


def centred(sheet, img, y, cx=None):
    sheet.alpha_composite(img, ((cx or W // 2) - img.width // 2, y))


def build():
    sheet = Image.new("RGBA", (W, H), PAPER)

    # Top band: the cat saying what to do. It is both the instruction and the
    # wordmark -- the app icon Finder draws below is the same cat, so a separate
    # logo would just be the same picture twice.
    art.OUT = THEME_DIR
    atlas = json.load(open(os.path.join(THEME_DIR, "cat.json")))
    cat = art.composite(art.build_parts())
    cat = cat.crop(cat.getbbox())
    cat = cat.resize((cat.width * 2, cat.height * 2), Image.NEAREST)
    sheet.alpha_composite(cat, (36, 16))

    bubble = art.preview_bubble(atlas, "Drag me into Applications")
    if bubble is not None:
        bubble = bubble.resize((bubble.width * 2, bubble.height * 2), Image.NEAREST)
        sheet.alpha_composite(bubble, (36 + cat.width - 6, 22))

    # Wordmark on the right of the same band, balancing the cat on the left.
    mark = text("loafcat", 4)
    sheet.alpha_composite(mark, (W - 40 - mark.width, 42))
    sub = text("an open desktop cat", 2, (150, 150, 158, 255))
    sheet.alpha_composite(sub, (W - 40 - sub.width, 82))

    # The arrow spans the gap between the two icons, stopping clear of both so it
    # can never collide with an icon label.
    gap_start, gap_end = APP_X + 78, APPLICATIONS_X - 78
    a = arrow(gap_end - gap_start)
    sheet.alpha_composite(a, (gap_start, ICON_Y - a.height // 2))

    # The one thing every downloader of an unnotarised app has to be told, before
    # they conclude the download is broken. Control-click-to-open stopped working
    # in macOS 15, so System Settings is now the only route.
    grey = (150, 150, 158, 255)
    centred(sheet, text("macOS blocks unnotarised apps on first launch", 2, grey), 300)
    centred(sheet, text("System Settings, Privacy and Security, Open Anyway", 2, grey), 324)
    centred(sheet, text("github.com/aadisaraf/loafcat", 2, (176, 176, 184, 255)), 352)
    return sheet


def main():
    os.makedirs(OUT, exist_ok=True)
    one = build()
    # Anything drawn below SAFE_H is invisible to anyone whose Finder shows a
    # status bar. Cheaper to fail here than to find out from a screenshot.
    below = one.crop((0, SAFE_H, W, H)).convert("RGB")
    if below.getbbox() and len(below.getcolors(maxcolors=64) or [0, 0]) > 1:
        raise SystemExit(
            f"content below y={SAFE_H} would be clipped by Finder window chrome")
    one.save(os.path.join(OUT, "background.png"))
    two = one.resize((W * 2, H * 2), Image.NEAREST)
    two.save(os.path.join(OUT, "background@2x.png"))

    # Finder picks the right scale out of a multi-representation TIFF; a plain PNG
    # would be point-scaled and blurred on every retina display made since 2012.
    tiff = os.path.join(OUT, "background.tiff")
    try:
        subprocess.run(
            ["tiffutil", "-cathidpicheck",
             os.path.join(OUT, "background.png"),
             os.path.join(OUT, "background@2x.png"),
             "-out", tiff],
            check=True, capture_output=True)
    except (FileNotFoundError, subprocess.CalledProcessError) as e:
        print(f"warning: tiffutil unavailable, retina background not built ({e})")

    print(f"background -> {OUT} ({W}x{H} and @2x)")


if __name__ == "__main__":
    main()
