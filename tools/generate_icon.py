#!/usr/bin/env python3
"""Generates the app icon and the menu bar glyph from the mono cat.

Same provenance rule as the art itself: nothing here is drawn by hand or traced.
The icon composites the *actual* mono theme parts produced by generate_art.py, so
the thing in the Dock and the thing on the desktop can never drift apart -- change
the cat and the icon changes with it.

Run:  python3 tools/generate_icon.py
Out:  assets/icon/AppIcon.iconset/*.png   the macOS icon grid
      assets/icon/AppIcon.icns            built from it by iconutil
      assets/icon/tray.png, tray@2x.png   menu bar glyph (a template mask)
      assets/icon/preview_icon.png        every size, on light and on dark
"""

import os
import subprocess
import sys

from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import generate_art as art  # noqa: E402  -- needs HERE on the path first

ROOT = os.path.dirname(HERE)
OUT = os.path.join(ROOT, "assets", "icon")

# The icon is the mono cat because that is the cat the app opens as. Applied at
# import, before any colour below is read -- apply_theme recolours PALETTE in
# place, so reading a swatch first would silently bake in the cream theme's.
THEME = "mono"
art.apply_theme(THEME)

# The icon is drawn on its own pixel grid, exactly like the cat is. 64 is the one
# size that divides every slot in the macOS icon grid by a power of two, so every
# rendered size is an exact nearest-neighbour scale of one master and no size in
# the set is ever resampled off-grid.
GRID = 64

# Apple's icon grid insets the rounded plate from the canvas; ~10% either side is
# the proportion their template uses and it is what stops icons touching in a
# Finder list. 2px of 64 is a shade tighter, which suits a plate this small.
INSET = 2

# The corner curve. n=4.2 is the squircle end of the superellipse family the cat's
# own head uses at 2.7 -- flat enough to meet the straight side without a visible
# kink, round enough not to read as a circular arc.
PLATE_N = 4.2

# Corner radius as a fraction of the plate. 0.2237 is the ratio Apple's icon
# template uses (185.4 of 824), and an icon that misses it reads as not-quite-right
# next to every other icon in the Dock.
PLATE_RADIUS = 0.2237

INK = art.PALETTE["outline"]
PAPER = art.PALETTE["bubble_paper"]


def convex_spans(spans):
    """Force scanline spans to widen monotonically toward the middle.

    Rounding a superellipse row by row can leave a single row one pixel wider than
    both its neighbours. On the cat that is invisible; on a flat glyph it renders as
    a nub sticking out of an otherwise clean edge. The shape is mathematically
    convex, so clamping toward the widest row restores what the rounding lost.
    """
    if not spans:
        return spans
    rows = sorted(spans)
    widest = max(rows, key=lambda y: spans[y][1] - spans[y][0])
    out = dict(spans)
    for group in (
        [y for y in rows if y <= widest][::-1],   # middle -> top
        [y for y in rows if y >= widest],         # middle -> bottom
    ):
        for prev, y in zip(group, group[1:]):
            x0, x1 = out[y]
            px0, px1 = out[prev]
            out[y] = (max(x0, px0), min(x1, px1))
    return out


def plate_spans(size, inset, n=PLATE_N):
    """A rounded square: straight sides, superellipse corners.

    Not a superellipse across the whole plate. Sampling one row-by-row leaves the
    long sides very slightly barrelled, and a single row of rounding error there
    shows as a nub -- at 1024px that nub is sixteen pixels tall on an edge the eye
    reads as perfectly straight. Composing the shape from flat sides and explicit
    corner arcs makes the straight parts straight by construction.
    """
    lo, hi = inset, size - 1 - inset
    radius = max(1, round((hi - lo + 1) * PLATE_RADIUS))
    spans = {}
    for y in range(lo, hi + 1):
        depth = min(y - lo, hi - y)          # rows into the nearest corner
        if depth >= radius:
            spans[y] = (lo, hi)
            continue
        # t walks 0 -> 1 from where the arc begins to the outermost row.
        t = min(max((radius - 0.5 - depth) / radius, 0.0), 1.0)
        cut = round(radius - radius * (1 - t ** n) ** (1.0 / n))
        spans[y] = (lo + cut, hi - cut)
    return spans


def plate(size, fill=PAPER, border=INK, inset=INSET, n=PLATE_N):
    """The rounded plate the cat sits on, plus its 1px ink ring.

    The ring is not decoration. A paper-coloured plate is invisible against a white
    Finder window and an ink one is invisible against a dark desktop; the ring is
    what makes a single icon work on both, and it echoes the heavy outline that is
    the whole point of the mono cat.
    """
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    px = img.load()
    spans = plate_spans(size, inset, n)

    for y, (x0, x1) in spans.items():
        for x in range(x0, x1 + 1):
            if 0 <= x < size and 0 <= y < size:
                px[x, y] = fill

    if border:
        ring = set()
        for y, (x0, x1) in spans.items():
            for x in range(x0, x1 + 1):
                for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                    nx, ny = x + dx, y + dy
                    if 0 <= nx < size and 0 <= ny < size and px[nx, ny][3] == 0:
                        ring.add((nx, ny))
        for (x, y) in ring:
            px[x, y] = border
    return img


def master():
    """The 64x64 icon: the mono cat's default pose, on its plate.

    Centred on the head rather than on the bounding box. The tail carries real
    width but no visual weight, so bounding-box centring pushes the face
    noticeably left of the plate and reads as a mistake rather than as a pose.
    """
    art.apply_theme("mono")
    cat = art.composite(art.build_parts())          # CANVAS x CANVAS, RGBA
    bbox = cat.getbbox()
    crop = cat.crop(bbox)

    img = plate(GRID)
    head_cx = art.G["head"]["cx"]                   # the face's axis in atlas coords
    x = GRID // 2 - (head_cx - bbox[0])
    y = (GRID - crop.height) // 2
    img.alpha_composite(crop, (x, y))
    return img


# The 16px face, in the same units the cat's own geometry is written in. Kept as a
# table for the same reason G is in generate_art.py: every number is a design
# decision and they only make sense next to each other.
FACE = dict(
    ear_l=[(3, 7), (4, 2), (7, 6)],
    ear_r=[(12, 7), (11, 2), (8, 6)],
    # n=2.4 rather than the cat's 2.7: over eight rows a higher exponent has no room
    # to round and the skull comes out square.
    head=dict(cx=7.5, cy=8.5, w=9, h=8, n=2.4),
    eye_dx=2.3,       # from the head's axis
    eye_cy=9.0,
    # Deliberately oversized -- they run nearly the full width of the face. Smaller
    # sclera round down to a slot rather than a circle, and a slot reads as a visor.
    sclera=1.8,
    pupil=0.9,
)


def face(size=16):
    """Head, ears and eyes on transparent -- the artwork the small sizes use.

    A whole cat downsampled to 16px is a grey smudge: the body and tail cost half
    the pixels and contribute nothing legible. Dropping to the face is the trade
    every system icon makes at this size, and the eyes are what identify this cat
    anyway.
    """
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    k = size / 16.0
    px = img.load()

    def fill(spans, color):
        for y, (x0, x1) in spans.items():
            for x in range(x0, x1 + 1):
                if 0 <= x < size and 0 <= y < size:
                    px[x, y] = color

    coat = art.PALETTE["coat"]
    # Ears first and therefore behind the head, for the same reason the cat's are:
    # an ear outline crossing the skull leaves a seam.
    for key in ("ear_l", "ear_r"):
        art.tri_fill(img, [(round(x * k), round(y * k)) for (x, y) in FACE[key]], coat)
    h = FACE["head"]
    fill(convex_spans(art.superellipse_spans(
        h["cx"] * k, h["cy"] * k, h["w"] * k, h["h"] * k, n=h["n"])), coat)

    # Sclera plus pupil, the same two-part construction as the cat's: a single dark
    # dot at this size reads as a nostril rather than an eye.
    for sign in (-1, 1):
        cx = (h["cx"] + sign * FACE["eye_dx"]) * k
        cy = FACE["eye_cy"] * k
        fill(art.disc_spans(cx, cy, FACE["sclera"] * k), art.PALETTE["eye_white"])
        fill(art.disc_spans(cx, cy, FACE["pupil"] * k), art.PALETTE["pupil"])

    # The cat's own baked outline pass, so the glyph inherits the heavy ink. Before
    # the plate goes on, or there would be no transparent pixels left to dilate into.
    art.outline(img)
    return img


def small_glyph(size=16):
    """The 16pt slot: the face on the plate.

    Drawn at whatever size is asked for rather than drawn once and resampled, which
    is what lets the Windows icon carry a native 20px and 24px frame -- the tray
    sizes at 125% and 150% display scaling, neither of which is a whole multiple of
    anything else we have. `face` and `plate` are both parametric in `size`, so each
    one lands on its own pixel grid with no filtering anywhere.

    The border thickens with the glyph. A 1px inset at 24px reads as a hairline.
    """
    img = plate(size, inset=max(1, round(size / 16)), n=3.4)
    img.alpha_composite(face(size))
    return img


def tray_glyph(size=16):
    """The menu bar item, as a template mask.

    A template image is alpha only -- AppKit throws the colour away and tints the
    mask to match the menu bar, which is what makes one asset work in light mode,
    dark mode and under a tinted wallpaper. So the eyes cannot be white here; they
    have to be holes, or the face fills in solid and the cat becomes a blob.
    """
    src = face(size).load()
    out = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    op = out.load()
    for y in range(size):
        for x in range(size):
            r, g, b, a = src[x, y]
            if a == 0 or (r, g, b, a) == art.PALETTE["eye_white"]:
                continue                      # punched, so the eye reads as an eye
            op[x, y] = (0, 0, 0, 255)
    return out


# The macOS icon grid. Each entry is (filename, pixels, source) where source picks
# which artwork the slot is drawn from -- see small_glyph for why 16pt differs.
SLOTS = [
    ("icon_16x16.png",      16,  "small"),
    ("icon_16x16@2x.png",   32,  "small"),
    ("icon_32x32.png",      32,  "master"),
    ("icon_32x32@2x.png",   64,  "master"),
    ("icon_128x128.png",    128, "master"),
    ("icon_128x128@2x.png", 256, "master"),
    ("icon_256x256.png",    256, "master"),
    ("icon_256x256@2x.png", 512, "master"),
    ("icon_512x512.png",    512, "master"),
    ("icon_512x512@2x.png", 1024, "master"),
]


# The Windows .ico, which carries every size in one file.
#
# Split at 32px for the same reason the macOS grid is: below that a whole cat is a
# grey smudge and the face is the only thing that reads. Above it the plate has room
# for the body and the tail.
#
# 20 and 24 are here because Windows asks for them and macOS never does -- they are
# the notification-area sizes at 125% and 150% display scaling, which is most Windows
# laptops. Neither is a whole multiple of 16 or 64, so both are DRAWN at that size
# rather than resampled from a neighbour.
#
# 48 is deliberately absent: it only appears in Explorer's medium-icon view, it is not
# a whole multiple of the 64px master, and letting Windows derive it from 64 costs
# nothing anyone will see. Inventing a 48px master to avoid one downscale in a file
# browser would be the wrong trade.
WIN_SLOTS = [16, 20, 24, 32, 64, 128, 256]


def resize(img, size):
    """Nearest up, box down. Never anything smoother.

    Bilinear or Lanczos on pixel art blurs every edge the outline pass exists to
    make crisp. Upscaling is always an exact integer multiple here; the one
    downscale is an exact halving, where a box filter is a true 2x2 average rather
    than a resample.
    """
    if size == img.width:
        return img.copy()
    if size > img.width:
        assert size % img.width == 0, f"{size} is not an integer multiple of {img.width}"
        return img.resize((size, size), Image.NEAREST)
    assert img.width % size == 0, f"{img.width} is not an integer multiple of {size}"
    return img.resize((size, size), Image.BOX)


def main():
    os.makedirs(OUT, exist_ok=True)
    iconset = os.path.join(OUT, "AppIcon.iconset")
    os.makedirs(iconset, exist_ok=True)

    big = master()
    small = small_glyph(16)
    sources = {"master": big, "small": small}

    for name, size, src in SLOTS:
        resize(sources[src], size).save(os.path.join(iconset, name))

    big.save(os.path.join(OUT, "icon_master.png"))

    tray = tray_glyph(16)
    tray.save(os.path.join(OUT, "tray.png"))
    resize(tray, 32).save(os.path.join(OUT, "tray@2x.png"))

    # iconutil is part of the macOS command line tools. Producing the .icns here
    # rather than in build.sh keeps it under the same "regenerate and diff" check
    # in CI that the rest of the art is under.
    icns = os.path.join(OUT, "AppIcon.icns")
    try:
        subprocess.run(
            ["iconutil", "-c", "icns", iconset, "-o", icns],
            check=True, capture_output=True)
    except (FileNotFoundError, subprocess.CalledProcessError) as e:
        print(f"warning: iconutil unavailable, .icns not rebuilt ({e})")

    ico = _windows_icon(big)

    _preview(big, small, tray)

    print(f"icon   -> {iconset} ({len(SLOTS)} sizes)")
    print(f"icns   -> {icns}")
    print(f"tray   -> {os.path.join(OUT, 'tray.png')} (16 and 32px template)")
    print(f"ico    -> {ico} ({len(WIN_SLOTS)} sizes: {', '.join(map(str, WIN_SLOTS))})")
    print(f"preview-> {os.path.join(OUT, 'preview_icon.png')}")


def _windows_icon(big):
    """One .ico carrying every size Windows asks for.

    Unlike .icns, this is written by Pillow rather than by a platform tool, so it goes
    through the same regenerate-and-diff check in CI as the PNGs. That matters: it is
    the app icon, the taskbar icon and the notification-area icon all at once, and
    "the icon drifted from the cat" is exactly the failure `scripts/check-assets.sh`
    exists to catch.

    Pillow's ICO writer resamples with LANCZOS for any size it has to derive, which
    would blur every edge the outline pass exists to keep hard. `append_images` makes
    it use the frames handed to it instead, and the base image has to be the LARGEST
    of them -- the plugin silently drops any requested size bigger than the base.
    """
    frames = {
        size: (small_glyph(size) if size <= 32 else resize(big, size))
        for size in WIN_SLOTS
    }
    ordered = [frames[s] for s in sorted(WIN_SLOTS, reverse=True)]
    path = os.path.join(OUT, "loafcat.ico")
    ordered[0].save(
        path, format="ICO",
        sizes=[(s, s) for s in WIN_SLOTS],
        append_images=ordered[1:])

    # Read it back and prove every frame is the artwork we handed over rather than
    # something Pillow resampled on our behalf. A blurred tray icon is subtle enough
    # to ship unnoticed, and this is one assert.
    for size in WIN_SLOTS:
        with Image.open(path) as check:
            # Assigning `.size` is how the ICO plugin selects a frame. `getimage()`
            # did the same thing and was removed in Pillow 12.
            check.size = (size, size)
            got = check.convert("RGBA")
        want = frames[size].convert("RGBA")
        if got.tobytes() != want.tobytes():
            raise SystemExit(
                f"generate_icon: the {size}px frame in loafcat.ico is not the "
                f"artwork we supplied -- Pillow resampled it. Check that this "
                f"Pillow supports append_images for ICO.")
    return path


def _preview(big, small, tray):
    """Every rendered size at true scale on light and on dark, plus the glyphs big.

    The point is the small end: an icon that only ever gets looked at at 512px is
    an icon nobody has checked, because the sizes users actually see are 16 and 32
    in a Finder list.
    """
    shown = [16, 32, 64, 128, 256]
    pad = 12
    w = sum(s + pad for s in shown) + pad
    row = max(shown) + pad * 2
    sheet = Image.new("RGBA", (w, row * 2), (250, 250, 250, 255))
    dark = Image.new("RGBA", (w, row), (32, 32, 36, 255))

    x = pad
    for s in shown:
        src = small if s <= 16 else big
        img = resize(src, s)
        y = pad + (max(shown) - s)          # bottom-aligned, so the ramp reads
        sheet.alpha_composite(img, (x, y))
        dark.alpha_composite(img, (x, y))
        x += s + pad
    sheet.alpha_composite(dark, (0, row))

    # The tray glyph at 8x, on both menu bar tints, since a template image is a
    # mask and a mask can only be judged filled in.
    big_tray = resize(tray, 128)
    strip = Image.new("RGBA", (128 * 2 + pad * 3, 128 + pad * 2), (0, 0, 0, 0))
    for i, bg in enumerate([(250, 250, 250, 255), (32, 32, 36, 255)]):
        tile = Image.new("RGBA", (128 + pad * 2, 128 + pad * 2), bg)
        ink = Image.new("RGBA", big_tray.size,
                        (0, 0, 0, 255) if i == 0 else (255, 255, 255, 255))
        ink.putalpha(big_tray.getchannel("A"))
        tile.alpha_composite(ink, (pad, pad))
        strip.alpha_composite(tile, (pad + i * (128 + pad), 0))

    out = Image.new("RGBA", (max(sheet.width, strip.width), sheet.height + strip.height),
                    (250, 250, 250, 255))
    out.alpha_composite(sheet)
    out.alpha_composite(strip, (0, sheet.height))
    out.save(os.path.join(OUT, "preview_icon.png"))


if __name__ == "__main__":
    main()
