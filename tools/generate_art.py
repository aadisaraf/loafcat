#!/usr/bin/env python3
"""Generates the layered parts kit for the cat, plus its atlas.

This is the whole art pipeline. Nothing here is traced, sampled or derived from any
existing sprite -- every shape is generated from the geometry below, so `assets/`
carries an airtight provenance claim.

The design is a layered RIG, not a frame-by-frame sheet: ~16 body parts that the
runtime transforms. Squash-and-stretch, ear twitch, tail sway, head turn and pupil
tracking are all computed at runtime from these parts, which is why 18 animation
states need ~60 drawn cells rather than ~150 mutually-consistent frames -- and why
consistency is structural, since every state reuses the same pixels.

Run:  python3 tools/generate_art.py
Out:  assets/parts/*.png (tight-cropped), assets/cat.json (atlas), assets/preview.png
"""

import json
import os
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
OUT = os.path.join(ROOT, "assets")
PARTS = os.path.join(OUT, "parts")

CANVAS = 48  # logical pixels; rendered at integer scales only (2x/3x/4x)

# Extra transparent canvas around the cat, in logical pixels, so a speech bubble
# and a countdown plate have somewhere to live. Symmetric on purpose: it keeps the
# window's centre and the cat's centre the same point, which every cursor-relative
# calculation in the runtime already assumes, so widening the panel costs no change
# to the tracking maths.
PAD_X = 40
PAD_Y = 43

# ---------------------------------------------------------------------------
# Palette -- 16 indexed colours, locked. Every pixel must be one of these.
#
# Locking the palette is the cheapest consistency enforcement that exists: it
# constrains a human, a generator and any future contributor equally. It is also
# what makes one-file recolour themes possible, and makes the overheat state cost
# zero new art (it is an index remap of COAT_* to HOT_*).
# ---------------------------------------------------------------------------
PALETTE = {
    "none":       (0, 0, 0, 0),
    "outline":    (58, 44, 51, 255),    # #3A2C33 -- the single dark ink
    "outline_lit":(92, 71, 80, 255),    # #5C4750 -- outline on the top-left key-lit arc
    "coat_hi":    (255, 240, 214, 255), # #FFF0D6 -- lit coat
    "coat":       (247, 220, 179, 255), # #F7DCB3 -- base coat
    "coat_sh":    (214, 176, 133, 255), # #D6B085 -- one cel shadow tone, no dithering
    "muzzle":     (255, 250, 238, 255), # #FFFAEE
    "ear_inner":  (240, 168, 168, 255), # #F0A8A8
    "nose":       (224, 122, 130, 255), # #E07A82
    "eye_white":  (255, 255, 255, 255),
    "pupil":      (46, 36, 41, 255),    # #2E2429 -- slightly warmer than the outline
    "shadow":     (58, 44, 51, 64),     # contact shadow, alpha-blended
    "hot":        (226, 94, 82, 255),   # #E25E52 -- overheat coat
    "hot_sh":     (186, 66, 60, 255),   # #BA423C
    "accent":     (140, 200, 214, 255), # #8CC8D6 -- zzz / steam / thinking dots
    "heart":      (240, 122, 152, 255), # #F07A98
    # Speech bubble. Kept out of the coat ramp on purpose: the bubble must stay
    # legible on every theme, including the flat grey one where `muzzle` collapses
    # into the coat and a muzzle-coloured bubble would vanish against the cat.
    "bubble_ink":   (58, 44, 51, 255),   # 1px outline, same ink family as the cat
    "bubble_paper": (255, 250, 238, 255),
    "bubble_lit":   (255, 255, 255, 255),# inner rim on the top-left, shaded themes only
    "bubble_text":  (58, 44, 51, 255),
    # Wellness: the calm tint the coat eases toward during a stretch break.
    "calm":       (127, 199, 154, 255), # #7FC79A
}


THEMES = {
    "cream": {"palette": {}},   # the default defined in PALETTE above

    "tuxedo": {
        "palette": {
            "outline":     (74, 74, 86, 255),    # mid grey: readable on white AND black
            "outline_lit": (108, 108, 122, 255),
            "coat_hi":     (74, 74, 88, 255),
            "coat":        (46, 46, 56, 255),    # charcoal, not pure black
            "coat_sh":     (30, 30, 38, 255),
            "muzzle":      (245, 245, 248, 255), # the white bib
            "ear_inner":   (198, 150, 158, 255),
            "nose":        (232, 150, 158, 255),
            "bubble_ink":  (74, 74, 86, 255),
            "bubble_text": (46, 46, 56, 255),
            "bubble_paper":(245, 245, 248, 255),
        },
        "white_parts": {"paw_l", "paw_r"},
        "white_tail_tip": True,
    },

    # Minimal mono: flat mid-grey, one heavy dark ink, and eyes doing all the work.
    #
    # The design bet is that at 96px on a busy desktop, shading and facial detail are
    # noise -- they cost pixels and read as mud. Removing the shadow tone, the muzzle
    # patch, the nose, mouth and whiskers, then spending the freed budget on eye size,
    # gives a silhouette that stays legible when the cat is small and a face that
    # reads from across the room.
    "mono": {
        "palette": {
            "outline":     (38, 38, 42, 255),
            "outline_lit": (38, 38, 42, 255),   # no lit arc: one flat ink
            "coat_hi":     (138, 138, 142, 255),
            "coat":        (138, 138, 142, 255),  # flat -- hi/base/shadow identical
            "coat_sh":     (138, 138, 142, 255),
            "muzzle":      (138, 138, 142, 255),  # muzzle disappears into the coat
            "ear_inner":   (72, 72, 78, 255),
            "nose":        (138, 138, 142, 255),  # invisible
            "bubble_ink":  (38, 38, 42, 255),
            "bubble_text": (38, 38, 42, 255),
            "bubble_paper":(238, 238, 242, 255),  # off-white, so it lifts off the coat
        },
        # Big eyes are the whole design. maxOffset stays 2.0px so tracking still reads.
        "geometry": {
            "eye_l": dict(cx=17, cy=22, r=5.4),
            "eye_r": dict(cx=31, cy=22, r=5.4),
            "pupil_r_px": 3.2,
        },
        "hide": {"face"},     # no nose, mouth or whiskers
        "flat": True,         # skip all cel shading and lit rims
        # The reference art rings the sclera in the same heavy ink as everything
        # else. On a flat theme with no other facial detail this is what stops the
        # eyes reading as two holes punched in the head.
        "eye_outline": True,
    },
}

# Set per theme; a tuxedo cat's white paws are markings, not lighting.
WHITE_PARTS: set = set()
FLAT = False
HIDDEN: set = set()
THEME_CFG: dict = {}


def apply_theme(name):
    global WHITE_PARTS, FLAT, HIDDEN
    if name not in THEMES:
        raise SystemExit(f"unknown theme {name!r}; have {sorted(THEMES)}")
    t = THEMES[name]
    for k, v in t.get("palette", {}).items():
        PALETTE[k] = v
    for k, v in t.get("geometry", {}).items():
        G[k] = v
    # A theme may retune the cat as well as recolour it -- a big-eyed mono cat
    # arguably wants a wider petting ellipse than a shaded one. Merged per module
    # rather than replaced, so overriding one constant does not drop the rest of
    # that module's block.
    for module, consts in t.get("behaviour", {}).items():
        BEHAVIOUR.setdefault(module, {}).update(consts)
    global THEME_CFG
    THEME_CFG = t
    WHITE_PARTS = t.get("white_parts", set())
    FLAT = t.get("flat", False)
    HIDDEN = t.get("hide", set())


def new_layer():
    return Image.new("RGBA", (CANVAS, CANVAS), PALETTE["none"])


def px(img, x, y, color):
    """Sets one logical pixel. Silently ignores out-of-canvas writes."""
    if 0 <= x < CANVAS and 0 <= y < CANVAS:
        img.putpixel((x, y), PALETTE[color] if isinstance(color, str) else color)


def superellipse_spans(cx, cy, w, h, n=2.6):
    """Integer scanline spans for a rounded blob.

    A superellipse (|x/a|^n + |y/b|^n = 1) is the right primitive here: n=2 is a
    plain ellipse and reads too soft, n>=4 reads like a rectangle. n~2.6 gives the
    rounded-square silhouette that cute mascot art lives on, and because we take
    integer spans per row the result is grid-aligned by construction.
    """
    a, b = w / 2.0, h / 2.0
    spans = {}
    for y in range(int(cy - b), int(cy + b) + 1):
        dy = abs((y + 0.5 - cy) / b)
        if dy > 1:
            continue
        dx = (1 - dy ** n) ** (1.0 / n)
        half = a * dx
        x0, x1 = int(round(cx - half)), int(round(cx + half)) - 1
        if x1 >= x0:
            spans[y] = (x0, x1)
    return spans


def fill_spans(img, spans, color):
    for y, (x0, x1) in spans.items():
        for x in range(x0, x1 + 1):
            px(img, x, y, color)


def shade_spans(img, spans, color, side="br", depth=2):
    """One cel shadow tone on the bottom-right, matching a fixed top-left key light.

    A single fixed light direction across every part is what stops a layered rig
    from looking like a collage.
    """
    if FLAT:
        return          # flat themes have no shadow tone at all
    ys = sorted(spans)
    if not ys:
        return
    lo, hi = ys[0], ys[-1]
    for y in ys:
        x0, x1 = spans[y]
        depth_here = depth if y > lo + (hi - lo) * 0.45 else max(1, depth - 1)
        if side == "br":
            for x in range(max(x0, x1 - depth_here + 1), x1 + 1):
                px(img, x, y, color)
            if y >= hi - 1:
                for x in range(x0, x1 + 1):
                    px(img, x, y, color)


def outline(img, color="outline"):
    """Bakes a 1px outline by dilating the part's own alpha.

    Comnyang does this at runtime with an SVG feMorphology filter; we bake it,
    which costs nothing at runtime and keeps the pixels on the grid. Outlining
    each part separately (rather than the composite silhouette) is deliberate --
    it keeps ears readable against the head and arms against the body, and it is
    what lets parts move independently without seams opening up.
    """
    src = img.load()
    ring = []
    for y in range(CANVAS):
        for x in range(CANVAS):
            if src[x, y][3] != 0:
                continue
            for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                nx, ny = x + dx, y + dy
                if 0 <= nx < CANVAS and 0 <= ny < CANVAS and src[nx, ny][3] > 128:
                    ring.append((x, y))
                    break
    for x, y in ring:
        px(img, x, y, color)


# ---------------------------------------------------------------------------
# Geometry. Every number here is a design decision, so they live together.
#
# Proportions follow Kindchenschema -- the cluster of infant features that reads as
# cute across cultures: an oversized head (here ~1.5 head-heights total), eyes set
# low and wide on the face, a rounded silhouette with no hard corners, and minimal
# facial detail so expression comes from the eyes alone.
# ---------------------------------------------------------------------------
G = {
    "head":   dict(cx=24, cy=19, w=28, h=20, n=2.7),
    "body":   dict(cx=24, cy=36, w=22, h=16, n=2.3),
    "eye_l":  dict(cx=18, cy=22, r=4.6),
    "eye_r":  dict(cx=30, cy=22, r=4.6),
    "pupil_r_px": 2.4,
    "ear_l":  [(11, 18), (14, 1), (23, 15)],
    "ear_r":  [(37, 18), (34, 1), (25, 15)],
    "paw_l":  dict(cx=18, cy=43, w=8, h=6, n=2.2),
    "paw_r":  dict(cx=30, cy=43, w=8, h=6, n=2.2),
}


# ---------------------------------------------------------------------------
# Overlays -- the small status glyphs that float beside the head.
#
# They live in the top corners because the cat already fills the 48x48 canvas
# edge to edge (ears touch y=0, the contact shadow touches y=47). There is no
# room *above* the head, and the window is exactly the canvas, so anything drawn
# outside it would be clipped by the window server. The two top corners are the
# only free real estate, and a thought-bubble trail rising to the top-right is
# the reading the arrangement already suggests.
#
# Every coordinate is checked against the head superellipse and the ear polygons
# so a glyph never collides with the silhouette or its baked outline.
# ---------------------------------------------------------------------------
OVERLAY_G = {
    # Thought-bubble trail, drawn cumulatively: 1 dot, then 2, then 3.
    "think_dots": [(38, 13, 1), (41, 9, 2), (44, 4, 2)],   # (x, y, side)
    # Kept clear of the canvas edge: the baked outline needs a pixel on every side,
    # and px() silently drops anything outside 0..CANVAS, which reads as a clipped dot.
    # Four-point sparkles either side of the head. Arms alternate long/short so
    # two frames read as a twinkle rather than a blink.
    "spark_l": (6, 6),
    "spark_r": (43, 7),
    # Sweat drop, top-right. Reads as "oh no" without needing a face change.
    "sad": (42, 7),
    # Exclamation mark for "needs your permission". Deliberately the only red
    # glyph in the set -- it is the one the user has to act on.
    "alert": (43, 2),
}


def block(img, x0, y0, x1, y1, color):
    for y in range(y0, y1 + 1):
        for x in range(x0, x1 + 1):
            px(img, x, y, color)


def star(img, cx, cy, arm, color):
    """A four-point star: centre pixel plus four arms. Pixel-art sparkle."""
    px(img, cx, cy, color)
    for d in range(1, arm + 1):
        px(img, cx - d, cy, color)
        px(img, cx + d, cy, color)
        px(img, cx, cy - d, color)
        px(img, cx, cy + d, color)


def build_overlays():
    """Every sprite drawn ABOVE the cat but not part of it: the agent status glyphs
    and the reaction sprites.

    None of them is in ORDER, so none enters the draw stack, the hit mask, the
    default pose or the silhouette test -- a thought bubble must never make an empty
    corner pixel clickable.

    Each carries its own staging:
      `slots`  how many may be on screen at once. The view preallocates exactly this
               many layers, so an overlay never allocates inside a frame.
      `follow` a body part whose offset the sprite rides. A status glyph pinned to
               the head drifts with the head turn instead of sitting rigidly in the
               corner; steam and hearts carry their own motion and follow nothing.

    Every sprite is drawn INSIDE the 48px canvas on purpose. The cat's own
    coordinates are the contract with the runtime, and a sprite placed outside them
    would depend on how much transparent margin the panel happens to carry.
    """
    ov = {}

    def add(name, img, slots=1, follow=None):
        ov[name] = {"img": img, "slots": slots, "follow": follow}

    # --- thinking: an accumulating trail of dots -------------------------
    for n in (1, 2, 3):
        img = new_layer()
        for (x, y, side) in OVERLAY_G["think_dots"][:n]:
            block(img, x, y, x + side - 1, y + side - 1, "accent")
        outline(img)
        add(f"think_{n}", img, follow="head")

    # --- celebrating: sparkles, alternating which side is bigger ---------
    for i, (al, ar) in enumerate(((2, 1), (1, 2)), start=1):
        img = new_layer()
        star(img, *OVERLAY_G["spark_l"], arm=al, color="heart")
        star(img, *OVERLAY_G["spark_r"], arm=ar, color="heart")
        add(f"spark_{i}", img, follow="head")

    # --- errored: one sweat drop -----------------------------------------
    img = new_layer()
    dx, dy = OVERLAY_G["sad"]
    px(img, dx, dy - 1, "accent")
    block(img, dx - 1, dy, dx + 1, dy + 1, "accent")
    px(img, dx, dy + 2, "accent")
    outline(img)
    add("sad_1", img, follow="head")

    # --- needs permission: a bouncing exclamation mark --------------------
    ax, ay = OVERLAY_G["alert"]
    for i, lift in enumerate((0, 1), start=1):
        img = new_layer()
        top = ay - lift
        block(img, ax, top, ax + 1, top + 4, "hot")      # the bar
        block(img, ax, top + 6, ax + 1, top + 7, "hot")  # the dot
        outline(img)
        add(f"alert_{i}", img, follow="head")

    # --- overheating: three puffs of falling size, curling up and right ---
    # Beside the head rather than above it: the ears already own that space, and
    # this leaves ~11px of clear canvas to rise into.
    img = new_layer()
    for cx, cy, r in ((42, 20, 2.4), (43.4, 17.6, 1.7), (41.4, 16.0, 1.2)):
        fill_spans(img, disc_spans(cx, cy, r), "accent")
    outline(img)
    add("steam", img, slots=3)

    # --- being petted: the classic 7x6 pixel heart, outlined like every
    # other part, anchored on the brow with room to float up.
    img = new_layer()
    rows = [
        ".XX.XX.",
        "XXXXXXX",
        "XXXXXXX",
        ".XXXXX.",
        "..XXX..",
        "...X...",
    ]
    for dy, row in enumerate(rows):
        for dx, ch in enumerate(row):
            if ch == "X":
                px(img, 21 + dx, 14 + dy, "heart")
    outline(img)
    add("heart", img, slots=3)

    return ov


# Which glyph sequence plays for which agent state, and how fast. Timing lives
# here rather than in Swift for the same reason geometry does: a theme should be
# able to restyle the whole reaction set without a rebuild.
OVERLAY_ANIMS = {
    "think":      {"frames": ["think_1", "think_2", "think_3"], "fps": 2.5, "loop": True},
    "celebrate":  {"frames": ["spark_1", "spark_2"], "fps": 6.0, "loop": True},
    "error":      {"frames": ["sad_1"], "fps": 1.0, "loop": True},
    "permission": {"frames": ["alert_1", "alert_2"], "fps": 3.0, "loop": True},
}

# ---------------------------------------------------------------------------
# Body animation curves. Sampled by the runtime; keyframes are [t_seconds, x, y]
# for offsets (logical px, y-DOWN like the rest of the atlas) and [t, factor]
# for squash, where 1.0 is neutral and <1 is compressed.
#
# `hop` is the headline one: a 13-keyframe double-hop over 2.2s peaking at -26px.
# The second bounce is deliberately about half the height of the first and lands
# early -- a symmetric double-hop reads as a bug, an asymmetric one reads as joy.
# ---------------------------------------------------------------------------
ANIM = {
    "hop": {
        "duration": 2.2,
        "loop": False,
        "offset": [
            [0.00, 0, 0], [0.10, 0, -10], [0.22, 0, -20], [0.34, 0, -26],
            [0.46, 0, -20], [0.58, 0, -8], [0.66, 0, 0], [0.74, 0, 3],
            [0.86, 0, -9], [1.00, 0, -14], [1.16, 0, -6], [1.28, 0, 0],
            [2.20, 0, 0],
        ],
        "squash": [
            [0.00, 0.94], [0.08, 1.10], [0.34, 1.02], [0.62, 1.06], [0.70, 0.90],
            [0.80, 1.06], [1.00, 1.02], [1.24, 0.92], [1.34, 1.03], [1.50, 1.00],
            [2.20, 1.00],
        ],
    },
    # Error: sink, hold, then ease part-way back. The hold is what makes it read
    # as dejection rather than a stumble.
    "slump": {
        "duration": 2.0,
        "loop": False,
        "offset": [[0.00, 0, 0], [0.18, 0, 2], [0.50, 0, 3], [1.60, 0, 3], [2.00, 0, 1]],
        "squash": [[0.00, 1.00], [0.20, 0.90], [1.60, 0.90], [2.00, 0.96]],
    },
    # Needs permission: a small repeating startle. Loops until the user answers,
    # because a one-shot would be missed by anyone not looking at that moment.
    "alert": {
        "duration": 0.9,
        "loop": True,
        "offset": [[0.00, 0, 0], [0.18, 0, -3], [0.36, 0, 0], [0.90, 0, 0]],
        "squash": [[0.00, 1.00], [0.12, 1.06], [0.30, 0.96], [0.45, 1.00], [0.90, 1.00]],
    },
    # Thinking: a 1px drift, slower than the breath so the two never phase-lock
    # into one bigger motion.
    "think": {
        "duration": 1.6,
        "loop": True,
        "offset": [[0.00, 0, 0], [0.40, 0, -1], [0.80, 0, 0], [1.20, 0, 1], [1.60, 0, 0]],
        "squash": [[0.00, 1.00], [0.40, 1.01], [0.80, 1.00], [1.20, 0.99], [1.60, 1.00]],
    },
}


def disc_spans(cx, cy, r):
    spans = {}
    for y in range(int(cy - r) - 1, int(cy + r) + 2):
        dy = y + 0.5 - cy
        if abs(dy) > r:
            continue
        half = (r * r - dy * dy) ** 0.5
        x0, x1 = int(round(cx - half)), int(round(cx + half)) - 1
        if x1 >= x0:
            spans[y] = (x0, x1)
    return spans


def tri_fill(img, pts, color):
    """Scanline-fills a polygon. Used for the ears."""
    ys = [p[1] for p in pts]
    for y in range(min(ys), max(ys) + 1):
        xs = []
        for i in range(len(pts)):
            (x1, y1), (x2, y2) = pts[i], pts[(i + 1) % len(pts)]
            if y1 == y2:
                continue
            if min(y1, y2) <= y < max(y1, y2):
                t = (y - y1) / (y2 - y1)
                xs.append(x1 + t * (x2 - x1))
        if len(xs) >= 2:
            xs.sort()
            for x in range(int(round(xs[0])), int(round(xs[-1])) + 1):
                px(img, x, y, color)


def build_parts():
    parts = {}

    # --- body -------------------------------------------------------------
    img = new_layer()
    spans = superellipse_spans(**G["body"])
    fill_spans(img, spans, "coat")
    shade_spans(img, spans, "coat_sh", depth=3)
    # lit rim on the top-left arc
    if not FLAT:
        for y, (x0, x1) in list(spans.items())[:4]:
            for x in range(x0, min(x0 + 4, x1 + 1)):
                px(img, x, y, "coat_hi")
    outline(img)
    parts["body"] = img

    # --- head -------------------------------------------------------------
    img = new_layer()
    spans = superellipse_spans(**G["head"])
    fill_spans(img, spans, "coat")
    shade_spans(img, spans, "coat_sh", depth=2)
    if not FLAT:
        for y, (x0, x1) in list(spans.items())[:3]:
            for x in range(x0, min(x0 + 5, x1 + 1)):
                px(img, x, y, "coat_hi")
        # muzzle: a soft lighter patch low on the face, where a cat's is
        for y, (x0, x1) in disc_spans(24, 27, 5.0).items():
            for x in range(x0, x1 + 1):
                if img.getpixel((x, y))[3] > 0:
                    px(img, x, y, "muzzle")
    outline(img)
    parts["head"] = img

    # --- ears -------------------------------------------------------------
    for name in ("ear_l", "ear_r"):
        img = new_layer()
        tri_fill(img, G[name], "coat")
        # inner ear: the same polygon scaled toward its own centroid
        n_pts = len(G[name])
        cx = sum(p[0] for p in G[name]) / n_pts
        cy = sum(p[1] for p in G[name]) / n_pts
        inner = [(round(cx + (x - cx) * 0.42), round(cy + (y - cy) * 0.52 - 1.5)) for x, y in G[name]]
        tri_fill(img, inner, "ear_inner")
        outline(img)
        parts[name] = img

    # --- eyes: sclera and pupil are SEPARATE layers -----------------------
    # This split is the entire reason cursor tracking is possible without redrawing
    # frames: the runtime translates the pupil inside the sclera and clips.
    for side in ("l", "r"):
        g = G[f"eye_{side}"]
        img = new_layer()
        fill_spans(img, disc_spans(g["cx"], g["cy"], g["r"]), "eye_white")
        # Shaded themes leave the sclera unringed -- a dark ring around a white disc
        # reads as spectacles when there is already a muzzle, nose and whiskers
        # competing for attention. Flat themes have none of that, so the ring is what
        # anchors the eye in the face.
        if THEME_CFG.get("eye_outline"):
            outline(img)
        parts[f"eye_{side}"] = img

        img = new_layer()
        fill_spans(img, disc_spans(g["cx"], g["cy"], G["pupil_r_px"]), "pupil")
        # A single highlight pixel up-left. Without it the eye reads as dead glass.
        px(img, int(g["cx"] - 1), int(g["cy"] - 1), "eye_white")
        parts[f"pupil_{side}"] = img

        # closed lid, for blink and sleep -- a transform cannot fake this
        img = new_layer()
        for x in range(int(g["cx"] - g["r"]), int(g["cx"] + g["r"])):
            px(img, x, int(g["cy"]), "outline")
        px(img, int(g["cx"] - g["r"]) - 1, int(g["cy"]) - 1, "outline")
        px(img, int(g["cx"] + g["r"]), int(g["cy"]) - 1, "outline")
        parts[f"lid_{side}"] = img

    # --- nose + mouth -----------------------------------------------------
    img = new_layer()
    px(img, 23, 26, "nose"); px(img, 24, 26, "nose"); px(img, 23, 27, "nose")
    px(img, 24, 27, "nose")
    # mouth: a tiny w, two pixels either side of the nose
    for x, y in ((22, 28), (25, 28), (23, 29), (24, 29)):
        px(img, x, y, "outline")
    # whiskers, 2 per side, offset so they do not read as a grid
    for x, y in ((13, 26), (12, 25), (14, 29), (13, 30),
                 (34, 26), (35, 25), (33, 29), (34, 30)):
        px(img, x, y, "outline_lit")
    parts["face"] = img

    # --- paws -------------------------------------------------------------
    for name in ("paw_l", "paw_r"):
        img = new_layer()
        spans = superellipse_spans(**G[name])
        white = name in WHITE_PARTS
        fill_spans(img, spans, "muzzle" if white else "coat_hi")
        shade_spans(img, spans, "coat_sh" if white else "coat", depth=1)
        outline(img)
        parts[name] = img

    # --- tail: drawn as a 3-segment strip so the runtime can shear it ------
    img = new_layer()
    tail_path = [(33, 43), (38, 43), (41, 40), (42, 36), (41, 32)]
    for i in range(len(tail_path) - 1):
        (x1, y1), (x2, y2) = tail_path[i], tail_path[i + 1]
        steps = max(abs(x2 - x1), abs(y2 - y1)) * 3
        for s in range(steps + 1):
            t = s / max(steps, 1)
            cx, cy = x1 + (x2 - x1) * t, y1 + (y2 - y1) * t
            thick = 2.4 - 0.7 * (i / len(tail_path))
            for yy in range(int(cy - thick), int(cy + thick) + 1):
                for xx in range(int(cx - thick), int(cx + thick) + 1):
                    if (xx - cx) ** 2 + (yy - cy) ** 2 <= thick * thick:
                        px(img, xx, yy, "coat")
    # White tail tip: a real tuxedo marking, and it makes the tail's sway legible
    # against a dark desktop where a charcoal tail would disappear.
    if THEME_CFG.get("white_tail_tip"):
        # Measured from the path's own endpoint rather than a hardcoded box, so the
        # marking stays glued to the tip if the tail is ever re-shaped. A fixed box
        # read as a separate white square floating beside the cat.
        tip = tail_path[-1]
        tip_r = 3.0
        for yy in range(CANVAS):
            for xx in range(CANVAS):
                if img.getpixel((xx, yy))[3] == 0:
                    continue
                if (xx - tip[0]) ** 2 + (yy - tip[1]) ** 2 <= tip_r * tip_r:
                    px(img, xx, yy, "muzzle")
    outline(img)
    parts["tail"] = img

    # --- contact shadow ---------------------------------------------------
    img = new_layer()
    for y, (x0, x1) in superellipse_spans(cx=24, cy=45, w=26, h=5, n=2.0).items():
        for x in range(x0, x1 + 1):
            px(img, x, y, "shadow")
    parts["shadow"] = img

    return parts


# ---------------------------------------------------------------------------
# Speech bubble -- a 9-slice, because a bubble has to fit its text.
#
# 'P' paper, '#' ink, 'L' the inner lit rim, '.' transparent. The corners carry a
# single-pixel chamfer rather than a rounded arc: at 1x a chamfer is the only
# rounding that exists, and it stays exactly one pixel at 2x/3x/4x because the
# runtime magnifies with nearest-neighbour.
#
# The lit rim follows the same fixed top-left key light as the cat's coat, so a
# shaded theme's bubble sits in the same light as its cat. FLAT themes collapse
# 'L' into paper, which is what the flag means everywhere else in this file.
# ---------------------------------------------------------------------------
BUBBLE_CORNER = 3

BUBBLE_SLICES = {
    "tl": (".##", "#LL", "#LP"),
    "t":  ("#", "L", "P"),
    "tr": ("##.", "LP#", "PP#"),
    "l":  ("#LP",),
    "c":  ("P",),
    "r":  ("PP#",),
    "bl": ("#LP", "#PP", ".##"),
    "b":  ("P", "P", "#"),
    "br": ("PP#", "PP#", "##."),
}

# Overlaps the bubble's bottom row by one: its top row is paper, which punches
# the hole in the outline that makes the tail read as part of the same shape
# rather than a triangle parked underneath it.
BUBBLE_TAIL = ("PPPPP", "#PPP#", ".#P#.", "..#..")
BUBBLE_TAIL_OVERLAP = 1
BUBBLE_TAIL_TIP_X = 2      # which column of the tail the tip sits on


# ---------------------------------------------------------------------------
# Pixel font -- 5x8 cell, cap height 6, x-height 4, two descender rows.
#
# Hand-authored rather than rasterised from a system face. Of everything shipped
# with macOS only Monaco and Geneva still threshold to pure 1-bit at 9px (Menlo,
# SF Mono and Andale Mono all produce 120+ alpha levels and would need to be
# thresholded into mush); but baking Apple's glyph bitmaps into assets/ is exactly
# the unclear-provenance import this project refuses, and a runtime NSFont cannot
# be recoloured per theme or regenerated by this script. 96 glyphs is a cheap
# price for art that is ours, deterministic across OS versions, and on the grid
# by construction.
#
# Each entry is (top_row, rows). `top_row` places the glyph in the 8-row cell:
# 0 for caps and ascenders, 2 for x-height, 2..7 for descenders.
# ---------------------------------------------------------------------------
FONT_H = 8
FONT_BASELINE = 6      # rows 0..5 sit on or above the baseline
FONT_TRACKING = 1      # blank columns between glyphs
FONT_SPACE_W = 3
FONT_LINE_GAP = 1


def _g(top, *rows):
    return (top, rows)


GLYPHS = {
    "A": _g(0, ".###.", "#...#", "#...#", "#####", "#...#", "#...#"),
    "B": _g(0, "####.", "#...#", "####.", "#...#", "#...#", "####."),
    "C": _g(0, ".###.", "#...#", "#....", "#....", "#...#", ".###."),
    "D": _g(0, "####.", "#...#", "#...#", "#...#", "#...#", "####."),
    "E": _g(0, "#####", "#....", "####.", "#....", "#....", "#####"),
    "F": _g(0, "#####", "#....", "####.", "#....", "#....", "#...."),
    "G": _g(0, ".###.", "#...#", "#....", "#..##", "#...#", ".###."),
    "H": _g(0, "#...#", "#...#", "#####", "#...#", "#...#", "#...#"),
    "I": _g(0, "###", ".#.", ".#.", ".#.", ".#.", "###"),
    "J": _g(0, "..###", "...#.", "...#.", "...#.", "#..#.", ".##.."),
    "K": _g(0, "#...#", "#..#.", "###..", "#.#..", "#..#.", "#...#"),
    "L": _g(0, "#....", "#....", "#....", "#....", "#....", "#####"),
    "M": _g(0, "#...#", "##.##", "#.#.#", "#...#", "#...#", "#...#"),
    "N": _g(0, "#...#", "##..#", "#.#.#", "#..##", "#...#", "#...#"),
    "O": _g(0, ".###.", "#...#", "#...#", "#...#", "#...#", ".###."),
    "P": _g(0, "####.", "#...#", "#...#", "####.", "#....", "#...."),
    "Q": _g(0, ".###.", "#...#", "#...#", "#...#", "#..#.", ".##.#"),
    "R": _g(0, "####.", "#...#", "#...#", "####.", "#..#.", "#...#"),
    "S": _g(0, ".####", "#....", ".###.", "....#", "....#", "####."),
    "T": _g(0, "#####", "..#..", "..#..", "..#..", "..#..", "..#.."),
    "U": _g(0, "#...#", "#...#", "#...#", "#...#", "#...#", ".###."),
    "V": _g(0, "#...#", "#...#", "#...#", "#...#", ".#.#.", "..#.."),
    "W": _g(0, "#...#", "#...#", "#...#", "#.#.#", "##.##", "#...#"),
    "X": _g(0, "#...#", ".#.#.", "..#..", "..#..", ".#.#.", "#...#"),
    "Y": _g(0, "#...#", ".#.#.", "..#..", "..#..", "..#..", "..#.."),
    "Z": _g(0, "#####", "....#", "...#.", "..#..", ".#...", "#####"),

    # Full-height right stem, unlike 'o' -- at 4px tall that one column is the only
    # thing keeping the two apart.
    "a": _g(2, ".###", "#..#", "#..#", ".###"),
    "b": _g(0, "#...", "#...", "###.", "#..#", "#..#", "###."),
    "c": _g(2, ".###", "#...", "#...", ".###"),
    "d": _g(0, "...#", "...#", ".###", "#..#", "#..#", ".###"),
    "e": _g(2, ".##.", "#..#", "###.", ".##."),
    "f": _g(0, "..##", ".#..", "###.", ".#..", ".#..", ".#.."),
    "g": _g(2, ".###", "#..#", "#..#", ".###", "...#", "###."),
    "h": _g(0, "#...", "#...", "###.", "#..#", "#..#", "#..#"),
    "i": _g(0, ".#.", "...", "##.", ".#.", ".#.", "###"),
    "j": _g(0, "..#", "...", "..#", "..#", "..#", "..#", "..#", "##."),
    "k": _g(0, "#...", "#...", "#..#", "#.#.", "###.", "#..#"),
    "l": _g(0, "##.", ".#.", ".#.", ".#.", ".#.", "###"),
    "m": _g(2, "#####", "#.#.#", "#.#.#", "#.#.#"),
    "n": _g(2, "###.", "#..#", "#..#", "#..#"),
    "o": _g(2, ".##.", "#..#", "#..#", ".##."),
    "p": _g(2, "###.", "#..#", "#..#", "###.", "#...", "#..."),
    "q": _g(2, ".###", "#..#", "#..#", ".###", "...#", "...#"),
    "r": _g(2, "#.##", "##..", "#...", "#..."),
    "s": _g(2, ".###", "##..", "..##", "###."),
    "t": _g(0, ".#..", ".#..", "###.", ".#..", ".#..", "..##"),
    "u": _g(2, "#..#", "#..#", "#..#", ".###"),
    "v": _g(2, "#..#", "#..#", "#..#", ".##."),
    "w": _g(2, "#.#.#", "#.#.#", "#.#.#", ".#.#."),
    "x": _g(2, "#..#", ".##.", ".##.", "#..#"),
    "y": _g(2, "#..#", "#..#", "#..#", ".###", "...#", "###."),
    "z": _g(2, "####", "..#.", ".#..", "####"),

    "0": _g(0, ".###.", "#..##", "#.#.#", "#.#.#", "##..#", ".###."),
    "1": _g(0, "..#..", ".##..", "..#..", "..#..", "..#..", ".###."),
    "2": _g(0, ".###.", "#...#", "...#.", "..#..", ".#...", "#####"),
    "3": _g(0, "####.", "....#", ".###.", "....#", "....#", "####."),
    "4": _g(0, "#..#.", "#..#.", "#..#.", "#####", "...#.", "...#."),
    "5": _g(0, "#####", "#....", "####.", "....#", "....#", "####."),
    "6": _g(0, ".###.", "#....", "####.", "#...#", "#...#", ".###."),
    "7": _g(0, "#####", "....#", "...#.", "..#..", "..#..", "..#.."),
    "8": _g(0, ".###.", "#...#", ".###.", "#...#", "#...#", ".###."),
    "9": _g(0, ".###.", "#...#", "#...#", ".####", "....#", ".###."),

    ".": _g(5, "#"),
    ",": _g(5, ".#", "#."),
    "!": _g(0, "#", "#", "#", "#", ".", "#"),
    "?": _g(0, ".##.", "#..#", "...#", "..#.", "....", "..#."),
    ":": _g(2, "#", ".", ".", "#"),
    ";": _g(2, ".#", "..", "..", ".#", "#."),
    "'": _g(0, "#", "#"),
    '"': _g(0, "#.#", "#.#"),
    "-": _g(3, "###"),
    "_": _g(6, "####"),
    "=": _g(3, "###", "...", "###"),
    "+": _g(2, ".#.", "###", ".#."),
    "*": _g(0, "#.#", ".#.", "#.#"),
    "/": _g(0, "...#", "..#.", "..#.", ".#..", ".#..", "#..."),
    "(": _g(0, ".#", "#.", "#.", "#.", "#.", ".#"),
    ")": _g(0, "#.", ".#", ".#", ".#", ".#", "#."),
    "%": _g(0, "##..#", "##.#.", "...#.", "..#..", ".#.##", "#..##"),
}


def _cell_image(rows, mapping):
    """Rasterises a row-string cell. Unmapped characters stay transparent."""
    h = len(rows)
    w = max(len(r) for r in rows)
    img = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    for y, row in enumerate(rows):
        for x, ch in enumerate(row):
            c = mapping.get(ch)
            if c is not None:
                img.putpixel((x, y), c)
    return img


def build_bubble(out_parts):
    """Writes the 9-slice pieces plus the tail. Returns the atlas fragment."""
    lit = "bubble_paper" if FLAT else "bubble_lit"
    mapping = {
        "#": PALETTE["bubble_ink"],
        "P": PALETTE["bubble_paper"],
        "L": PALETTE[lit],
    }
    slices = {}
    for name, rows in BUBBLE_SLICES.items():
        img = _cell_image(rows, mapping)
        fn = f"bubble_{name}.png"
        img.save(os.path.join(out_parts, fn))
        slices[name] = {"file": f"parts/{fn}", "w": img.width, "h": img.height}

    tail = _cell_image(BUBBLE_TAIL, mapping)
    tail.save(os.path.join(out_parts, "bubble_tail.png"))

    # How many lines fit between the cat's ears and the top of the padded canvas.
    # Derived rather than guessed, so changing PAD_Y or the font cell keeps it true
    # and the runtime can truncate instead of silently drawing outside the window.
    text_pad_x, text_pad_y = 4, 3
    gap, anchor_y = 1, 1
    tail_drop = tail.height - BUBBLE_TAIL_OVERLAP
    budget = PAD_Y + anchor_y - gap + 1 - (2 * text_pad_y + tail_drop) + FONT_LINE_GAP
    max_lines = max(1, budget // (FONT_H + FONT_LINE_GAP))

    return {
        "corner": BUBBLE_CORNER,
        "slices": slices,
        "tail": {
            "file": "parts/bubble_tail.png",
            "w": tail.width, "h": tail.height,
            "overlap": BUBBLE_TAIL_OVERLAP, "tip_x": BUBBLE_TAIL_TIP_X,
        },
        # Inner padding around the text, INCLUDING the 1px outline.
        "text_pad": [text_pad_x, text_pad_y],
        "line_gap": FONT_LINE_GAP,
        "max_lines": max_lines,
        # Widest the bubble may grow before text wraps. Sized to the padded panel
        # (see "layout" below) with a 12px margin either side.
        "max_width": 2 * PAD_X + int(CANVAS) - 24,
        "min_width": 2 * BUBBLE_CORNER + 1,
        # Where the tail's tip lands, in cat-canvas coordinates: just above the
        # ears, on the centre line.
        "anchor": [int(CANVAS) // 2, anchor_y],
        "gap": gap,
        "text_color": "#%02X%02X%02X%02X" % PALETTE["bubble_text"],
    }


def build_font(out_parts):
    """Writes one strip PNG holding every glyph, plus its metrics.

    White-on-transparent: the strip is a coverage mask, so the runtime can ink it
    in any colour (bubble text, timer digits) without a second copy per colour.
    """
    order = sorted(GLYPHS)
    widths = {}
    for ch in order:
        _, rows = GLYPHS[ch]
        widths[ch] = max(len(r) for r in rows)

    total = sum(widths[c] + FONT_TRACKING for c in order)
    sheet = Image.new("RGBA", (max(total, 1), FONT_H), (0, 0, 0, 0))
    metrics = {}
    x = 0
    for ch in order:
        top, rows = GLYPHS[ch]
        for dy, row in enumerate(rows):
            for dx, c in enumerate(row):
                if c == "#":
                    sheet.putpixel((x + dx, top + dy), (255, 255, 255, 255))
        metrics[ch] = {"x": x, "w": widths[ch]}
        x += widths[ch] + FONT_TRACKING

    sheet.save(os.path.join(out_parts, "font.png"))
    return {
        "file": "parts/font.png",
        "cell_h": FONT_H,
        "baseline": FONT_BASELINE,
        "tracking": FONT_TRACKING,
        "space": FONT_SPACE_W,
        "line_gap": FONT_LINE_GAP,
        "fallback": "?",
        "glyphs": metrics,
    }


def build_wellness():
    """Timing and staging for the wellness modules.

    These live in the atlas for the same reason the pivots do: they are the cat's
    behaviour, and a Windows port or a community theme should be able to change
    how long a stretch lasts without a compiler.
    """
    return {
        "grow_duration": 0.4,       # seconds to swell to full size
        "stretch_duration": 3.0,    # the stretch itself
        "restore_delay": 0.2,       # pause before shrinking back
        "screen_fraction": 0.9,     # of the display's visibleFrame height
        "tint": "#%02X%02X%02X%02X" % PALETTE["calm"],
        "tint_peak": 0.55,          # maximum tint opacity
        "tint_release_at": 0.7,     # fraction of the stretch where it eases back
        "tint_parts": ["body", "head", "ear_l", "ear_r", "paw_l", "paw_r", "tail"],
        "bob_height": 3,            # logical px for the hydration / message bob
        "flourish_duration": 0.8,   # the "getting to work" bounce
        "away_seconds": 600,        # skip a reminder if the user has been gone this long
        # Countdown plate, in cat-canvas coordinates: right edge and vertical centre.
        "timer": {"right": -4, "cy": 26, "pad": [2, 2]},
    }


# Draw order, back to front. Numeric prefixes keep it deterministic on disk.
ORDER = [
    "shadow", "tail", "body", "paw_l", "paw_r",
    "ear_l", "ear_r", "head",
    "eye_l", "eye_r", "pupil_l", "pupil_r", "lid_l", "lid_r",
    "face",
]

# ---------------------------------------------------------------------------
# Overheat: a palette remap, not a second set of drawings.
#
# The palette was locked with `hot`/`hot_sh` reserved for exactly this, and the
# remap is why the state costs no new art: we re-run the SAME generator with the
# coat keys pointed at the hot ones, so every hot part is pixel-for-pixel its base
# part with different ink. Because only colours change, the alpha -- and therefore
# the crop box -- is identical, which is what lets the runtime cross-fade the two
# images as one stationary layer pair.
#
# `coat_hi` maps to `hot` rather than to a third red: adding a `hot_hi` would take
# the locked palette to 17 colours, and a flushed cat losing its key-lit rim is the
# correct read anyway.
# ---------------------------------------------------------------------------
HOT_REMAP = {"coat_hi": "hot", "coat": "hot", "coat_sh": "hot_sh"}

# ---------------------------------------------------------------------------
# Per-module behaviour tuning, emitted into the atlas so that no timing or
# magnitude constant has to live in Swift (see CLAUDE.md, architecture rule 1).
# Keeping the numbers here means a theme can ship a lazier or a twitchier cat
# without a rebuild, a tuning pass is a JSON diff rather than a recompile, and a
# future Windows port reads exactly the same table.
#
# The runtime reads these by module id, then by constant name. A module that needs
# a key the theme does not carry says so loudly on stderr; the drag module, which
# predates that rule, still falls back to a default compiled into itself.
#
# Units are seconds, LOGICAL pixels (the 48px canvas, not screen points) and
# logical px/sec. Anything named `*_per_frame` — including the drag springs — is
# quoted at a nominal 60fps and normalised by dt at runtime, so the 120Hz tick
# behaves identically.
# ---------------------------------------------------------------------------
BEHAVIOUR = {
    "drag": {
        # A click only becomes a drag once the pointer clears this, or every
        # click-to-pet turns into an accidental lift.
        "deadzone_px": 4,
        # Stretch is driven by how long the cat has hung, NOT by drag distance.
        "stretch_hold_ms": 900,
        # Distance that must be travelled ACROSS the head before petting counts is
        # in the "pet" block; this block is the drag.
        # Gravity droop while held: springs here and stays, so a motionless cat
        # hangs a little instead of sitting at full stretch forever.
        "hang_rest": 0.60,
        "hang_rate": 6.0,   # exponential approach; a spring here would overshoot
        # How hard it is being thrown around. Rises fast, relaxes slower, decays
        # to nothing when the pointer stops.
        "yank_speed_ref": 380,
        "yank_attack": 14.0,
        "yank_release": 3.2,
        "stretch_max": 1.75,
        # Release spring. Authored at 60Hz as v += -0.13*x; v *= 0.78; x += v --
        # stiffness is that 0.13 expressed per second squared (0.13 * 60 * 60).
        "release_stiffness": 468,
        "release_damping": 0.78,
        "release_velocity_gain": 0.35,
        "release_settle_eps": 0.0001,
        "landing_squash_gain": 0.5,
        # The scruff. Wherever the body is grabbed, the hang is anchored into
        # this band, so a paw-grab still has a body length below it to stretch.
        "grab_min_y": 26,
        "grab_max_y": 34,
        "head_lag_px": 1.5,
        "head_swing_share": 0.08,
        "shadow_shrink": 0.65,
        # Pendulum. Angle is simulated in radians and rendered as an integer
        # horizontal shear of sin(angle) * swing_length_px.
        "swing_length_px": 14,
        "swing_max_deg": 45,
        "swing_impulse": 0.0012,
        "swing_accel_cap": 20,
        "swing_vel_smoothing": 0.35,
        "swing_spring_drag": 0.018,
        "swing_spring_free": 0.003,
        "swing_damping_drag": 0.86,
        "swing_damping_free": 0.962,
        "swing_settle_eps_rad": 0.007,
        "swing_settle_eps_vel": 0.003,
        # The cat's ink fills the 48px canvas edge to edge, so a hanging stretch
        # or a swing has nowhere to go. The panel grows by this much on every
        # side for the duration of a drag; see DragModule.
        "pad_px": 12,
    },

    # --- kneading -----------------------------------------------------------
    # The gate: react only once `burst_keys` land inside `burst_window`. Since the
    # runtime hands modules a sliding-window RATE, that condition is exactly
    # kps >= burst_keys / burst_window -- an isolated keypress cannot reach it.
    "typing": {
        "burst_keys": 5.0,
        "burst_window": 2.0,
        "release_delay": 0.18,   # back to idle this long after the last key
        "paw_period": 0.17,      # one paw stroke; paws alternate each stroke
        "paw_lift": 2.4,
        "paw_reach": 1.4,
        "body_bob": 0.8,
        "squash": 0.03,
        "attack": 0.10,          # seconds to reach full amplitude
        "decay": 0.16,           # seconds to fall back out of it
    },

    # --- overheat -----------------------------------------------------------
    # A continuous curve, never a binary state. Zero below kps_min so ordinary
    # typing never reddens the cat; 1.0 at kps_max. The exponent front-loads the
    # curve's flat part into the middle of the range, so the tint only becomes
    # obvious when the typing is genuinely frantic.
    "overheat": {
        "kps_min": 4.0,
        "kps_max": 14.0,
        "curve": 1.5,
        "ease_per_frame": 0.10,
        "state_at": 0.55,        # heat above which overheat outranks kneading
        "steam_at": 0.30,
        "steam_period": 0.85,
        "steam_rise": 11.0,
    },

    # --- hunting ------------------------------------------------------------
    # An energy accumulator that rewards REVERSALS, not speed. A straight sweep
    # only ever earns the speed term, whose ceiling (gain * excess / (1 - decay))
    # is deliberately below the trigger; a wiggle earns reversal bonuses on top and
    # crosses it within two or three direction changes.
    "hunt": {
        "decay_per_frame": 0.82,
        "speed_min": 300.0,
        "speed_gain_per_frame": 0.000034,
        "reverse_lag": 0.06,     # compare against velocity this long ago
        "reverse_dot": -0.28,    # cos of ~106 degrees
        "reverse_speed": 520.0,
        "reverse_bonus": 0.62,
        "reverse_refractory": 0.09,
        "accel_min": 12000.0,
        "accel_gain_per_frame": 0.0000009,
        "trigger": 1.0,
        "reset": 0.35,           # not 0 -- keep stalking cheap to re-trigger
        "crouch": 1.1,
        "recover": 0.4,
        "attack": 0.12,
        "squash": 0.93,
        "lean": 2.6,
        "body_lean": 1.2,
        "paw_reach": 1.6,
        "wiggle_hz": 5.5,        # the haunch-wiggle before a pounce
        "wiggle_amp": 0.7,
    },

    # --- petting ------------------------------------------------------------
    # The hit region is an ellipse over the head, sized from the head part's own
    # rectangle in this atlas rather than from a number typed into Swift.
    "pet": {
        "ellipse_scale": 1.0,
        "move_min": 14.0,        # logical px/sec; a parked cursor is not petting
        # Distance that must actually be travelled ACROSS the head before it counts
        # as petting. Without it, merely crossing the cat on the way somewhere else
        # starts a purr, because crossing satisfies "inside" and "moving" at once.
        # 18 logical px is a deliberate stroke, roughly two-thirds of the head.
        "stroke_min_px": 18.0,
        "stop_delay": 0.42,
        "leave_delay": 0.26,
        "lean": 2.4,
        "purr_hz": 9.0,
        "purr_amp": 0.7,
        "squash": 0.97,
        "attack": 0.14,
        "heart_period": 0.55,
        "heart_rise": 12.0,
        "heart_drift": 2.5,
    },

    # --- scrolling ----------------------------------------------------------
    "scroll": {
        "hold": 0.52,
        "bob": 2.0,
        "bob_hz": 4.5,
        "paw": 1.6,
        "attack": 0.06,
        "decay": 0.18,
    },
}


def write_part(img, name, bbox=None):
    """Tight-crops one layer to a PNG and returns its atlas entry, or None if the
    layer is empty.

    An explicit `bbox` pins a variant to its base part's grid instead of cropping
    it to its own, which is what keeps an overheat coat exactly on top of the coat
    it replaces.
    """
    box = bbox or img.getbbox()
    if box is None:
        return None
    img.crop(box).save(os.path.join(PARTS, f"{name}.png"))
    return {
        "file": f"parts/{name}.png",
        "x": box[0], "y": box[1],
        "w": box[2] - box[0], "h": box[3] - box[1],
    }


def build_hot_parts():
    """Re-runs the generator with the coat keys remapped to the hot ones."""
    saved = {k: PALETTE[k] for k in HOT_REMAP}
    for k, v in HOT_REMAP.items():
        PALETTE[k] = PALETTE[v]
    try:
        return build_parts()
    finally:
        PALETTE.update(saved)


def crop_and_write(parts, overlays, hot_parts=None):
    """Writes tight-cropped PNGs plus the atlas.

    Cropping keeps the atlas small, and the recorded offset is what lets the runtime
    place each part back on the 48x48 grid exactly.
    """
    os.makedirs(PARTS, exist_ok=True)
    atlas = {
        "canvas": CANVAS,
        "palette": {k: "#%02X%02X%02X%02X" % v for k, v in PALETTE.items()},
        "order": [n for n in ORDER if n not in HIDDEN],
        "parts": {},
    }
    for name in [n for n in ORDER if n not in HIDDEN]:
        entry = write_part(parts[name], name)
        if entry is None:
            continue
        atlas["parts"][name] = entry
        # Overheat variant, if the remap actually changed this part. Cropped with
        # the BASE part's box, not its own, so the two images are the same size at
        # the same origin and the cross-fade cannot shift a pixel.
        hot = (hot_parts or {}).get(name)
        if hot is not None and hot.tobytes() != parts[name].tobytes():
            bbox = (entry["x"], entry["y"],
                    entry["x"] + entry["w"], entry["y"] + entry["h"])
            hot_entry = write_part(hot, f"{name}_hot", bbox)
            if hot_entry is not None:
                atlas["parts"][f"{name}_hot"] = hot_entry
                atlas.setdefault("hot", []).append(name)

    # Overlays live in their own table rather than in "parts", and are kept OUT of
    # "order" on purpose: order drives both the draw loop and the click-through hit
    # mask, and a thought bubble must not make empty corner pixels clickable.
    ov_parts = {}
    for name, spec in sorted(overlays.items()):
        entry = write_part(spec["img"], name)
        if entry is None:
            continue
        entry["slots"] = spec["slots"]
        if spec["follow"]:
            entry["follow"] = spec["follow"]
        ov_parts[name] = entry
    atlas["overlays"] = {"parts": ov_parts, "anims": OVERLAY_ANIMS}
    atlas["anim"] = ANIM

    # Pivots: the point each part rotates/scales about. Named, because a magic
    # number in the runtime is unreadable and a wrong pivot is invisible in a
    # static mockup but obvious in motion.
    atlas["pivots"] = {
        "body":   [24, 44],   # floor contact -- squash scales from the ground up
        "head":   [24, 28],   # neck join
        "ear_l":  [15, 13], "ear_r": [33, 13],
        "tail":   [33, 43],   # root, where the shear chain starts
        "paw_l":  [18, 46], "paw_r": [30, 46],
        "pupil_l":[18, 22], "pupil_r": [30, 22],
    }
    # How far a pupil may travel from centre before it would leave the sclera.
    atlas["eye"] = {
        "sclera_r": G["eye_l"]["r"],
        "pupil_r": G["pupil_r_px"],
        "max_offset": round(G["eye_l"]["r"] - G["pupil_r_px"] - 0.2, 2),
        "centers": {"l": [18, 22], "r": [30, 22]},
    }
    # Sorted so a retune shows up as a diff of the numbers, not of the ordering.
    atlas["behaviour"] = {
        module: dict(sorted(consts.items()))
        for module, consts in sorted(BEHAVIOUR.items())
    }

    # Transparent margin the runtime adds around the cat canvas. The cat's own
    # coordinates never change -- everything above is still in 0..CANVAS -- so a
    # module can keep thinking in cat space while the window is larger than the cat.
    atlas["layout"] = {"pad_x": PAD_X, "pad_y": PAD_Y}

    # A theme may drop the bubble entirely by listing "bubble" in its `hide` set;
    # the runtime then simply never shows one.
    if "bubble" not in HIDDEN:
        atlas["bubble"] = build_bubble(PARTS)
        atlas["font"] = build_font(PARTS)
    atlas["wellness"] = build_wellness()

    with open(os.path.join(OUT, "cat.json"), "w") as f:
        json.dump(atlas, f, indent=2)
        f.write("\n")   # so a regenerate is a no-op diff, not a one-byte one
    return atlas


def preview_bubble(atlas, text):
    """Assembles a bubble the way the runtime does, purely so it can be looked at.

    Deliberately a second implementation of the same layout rather than a shared
    one: if the Swift compositor and this one disagree, the preview shows it.
    """
    if "bubble" not in atlas:
        return None
    b, f = atlas["bubble"], atlas["font"]
    padx, pady = b["text_pad"]
    corner = b["corner"]

    def gw(ch):
        if ch == " ":
            return f["space"]
        g = f["glyphs"].get(ch) or f["glyphs"][f["fallback"]]
        return g["w"]

    def word_w(word):
        return sum(gw(c) for c in word) + f["tracking"] * max(len(word) - 1, 0)

    limit = b["max_width"] - 2 * padx
    lines, cur = [], ""
    for word in text.split(" "):
        trial = word if not cur else cur + " " + word
        if word_w(trial) <= limit or not cur:
            cur = trial
        else:
            lines.append(cur)
            cur = word
    if cur:
        lines.append(cur)

    text_w = max(word_w(l) for l in lines)
    text_h = len(lines) * f["cell_h"] + (len(lines) - 1) * f["line_gap"]
    W = max(text_w + 2 * padx, b["min_width"])
    H = max(text_h + 2 * pady, 2 * corner + 1)

    tail_def = b["tail"]
    img = Image.new("RGBA", (W, H + tail_def["h"] - tail_def["overlap"]), (0, 0, 0, 0))
    sl = {k: Image.open(os.path.join(OUT, v["file"])) for k, v in b["slices"].items()}

    def blit(piece, x, y):
        img.paste(piece, (x, y), piece)

    blit(sl["tl"], 0, 0)
    blit(sl["tr"], W - corner, 0)
    blit(sl["bl"], 0, H - corner)
    blit(sl["br"], W - corner, H - corner)
    for x in range(corner, W - corner):
        blit(sl["t"], x, 0)
        blit(sl["b"], x, H - corner)
    for y in range(corner, H - corner):
        blit(sl["l"], 0, y)
        blit(sl["r"], W - corner, y)
        for x in range(corner, W - corner):
            blit(sl["c"], x, y)
    tail = Image.open(os.path.join(OUT, tail_def["file"]))
    blit(tail, (W - tail_def["w"]) // 2, H - tail_def["overlap"])

    sheet = Image.open(os.path.join(OUT, f["file"]))
    ink = tuple(int(b["text_color"][i:i + 2], 16) for i in (1, 3, 5, 7))
    for li, line in enumerate(lines):
        cx = padx + (text_w - word_w(line)) // 2   # centred
        cy = pady + li * (f["cell_h"] + f["line_gap"])
        for ch in line:
            if ch == " ":
                cx += f["space"] + f["tracking"]
                continue
            g = f["glyphs"].get(ch) or f["glyphs"][f["fallback"]]
            for dy in range(f["cell_h"]):
                for dx in range(g["w"]):
                    if sheet.getpixel((g["x"] + dx, dy))[3] > 128:
                        img.putpixel((cx + dx, cy + dy), ink)
            cx += g["w"] + f["tracking"]
    return img


def composite(parts, names=None, scale=1):
    out = Image.new("RGBA", (CANVAS, CANVAS), (0, 0, 0, 0))
    for name in (names or ORDER):
        if name.startswith("lid_"):
            continue  # lids are the blink frame, not the default pose
        out = Image.alpha_composite(out, parts[name])
    if scale > 1:
        out = out.resize((CANVAS * scale, CANVAS * scale), Image.NEAREST)
    return out


def main():
    import sys
    global OUT, PARTS
    theme = "mono"
    if "--theme" in sys.argv:
        theme = sys.argv[sys.argv.index("--theme") + 1]
    # One directory per theme: generating a theme must never clobber another one.
    OUT = os.path.join(ROOT, "assets", "themes", theme)
    PARTS = os.path.join(OUT, "parts")
    os.makedirs(PARTS, exist_ok=True)
    apply_theme(theme)
    print(f"theme: {theme}")
    parts = build_parts()
    overlays = build_overlays()
    atlas = crop_and_write(parts, overlays, build_hot_parts())

    # Contact sheet: the default pose at 1x/2x/4x/8x, on light and dark, plus the
    # silhouette. These are the readability tests -- if the cat fails any of them
    # it fails on a real desktop, and that is much cheaper to learn now.
    poses = composite(parts, scale=8)
    sheet = Image.new("RGBA", (CANVAS * 8 * 2 + 48, CANVAS * 8 + 32), (255, 255, 255, 255))
    sheet.paste(poses, (16, 16), poses)
    dark = Image.new("RGBA", (CANVAS * 8, CANVAS * 8), (28, 28, 32, 255))
    dark = Image.alpha_composite(dark, poses)
    sheet.paste(dark, (CANVAS * 8 + 32, 16))
    sheet.save(os.path.join(OUT, "preview.png"))

    # Silhouette test: fill every opaque pixel with ink. Species and pose must
    # still read, or the shape is wrong no matter how good the colours are.
    sil = composite(parts)
    solid = Image.new("RGBA", sil.size, (0, 0, 0, 0))
    for y in range(CANVAS):
        for x in range(CANVAS):
            if sil.getpixel((x, y))[3] > 40:
                solid.putpixel((x, y), PALETTE["outline"])
    solid.resize((CANVAS * 8, CANVAS * 8), Image.NEAREST).save(
        os.path.join(OUT, "preview_silhouette.png"))

    # Bubble sheet: the readability test for the UI half of the art. If the text is
    # illegible here it is illegible on a desktop, and this costs nothing to look at.
    bub = preview_bubble(atlas, "Time to stretch! Stand up and roll your shoulders.")
    if bub is not None:
        one = preview_bubble(atlas, "25:00 focus")
        pad = 12
        w = max(bub.width, one.width) * 4 + pad * 2
        h = (bub.height + one.height) * 4 + pad * 3
        sheet = Image.new("RGBA", (w, h), (250, 250, 250, 255))
        sheet.alpha_composite(bub.resize((bub.width * 4, bub.height * 4), Image.NEAREST),
                              (pad, pad))
        dark = Image.new("RGBA", (one.width * 4, one.height * 4), (28, 28, 32, 255))
        dark.alpha_composite(one.resize((one.width * 4, one.height * 4), Image.NEAREST))
        sheet.paste(dark, (pad, bub.height * 4 + pad * 2))
        sheet.save(os.path.join(OUT, "preview_bubble.png"))

    # Overlay contact sheet: the glyphs composited over the default pose, so a
    # collision with the ears or the head outline is visible at a glance rather
    # than only once the app is running.
    base = composite(parts)
    strip = Image.new("RGBA", (CANVAS * len(overlays), CANVAS), (0, 0, 0, 0))
    for i, name in enumerate(sorted(overlays)):
        strip.paste(
            Image.alpha_composite(base, overlays[name]["img"]), (CANVAS * i, 0))
    dark = Image.new("RGBA", strip.size, (28, 28, 32, 255))
    Image.alpha_composite(dark, strip).resize(
        (strip.width * 4, strip.height * 4), Image.NEAREST
    ).save(os.path.join(OUT, "preview_overlays.png"))

    used = {p for p in atlas["parts"]}
    print(f"wrote {len(used)} parts -> {PARTS}")
    print(f"overlays -> {sorted(atlas['overlays']['parts'])}")
    print(f"atlas -> {os.path.join(OUT, 'cat.json')}")
    print(f"preview -> {os.path.join(OUT, 'preview.png')}")
    for n in atlas["order"]:
        if n in atlas["parts"]:
            p = atlas["parts"][n]
            hot = " +hot" if n in atlas.get("hot", []) else ""
            print(f"  {n:<10} {p['w']:>2}x{p['h']:<2} at ({p['x']:>2},{p['y']:>2}){hot}")
    for n, p in sorted(atlas["overlays"]["parts"].items()):
        follow = f" follows {p['follow']}" if "follow" in p else ""
        print(f"  {n:<10} {p['w']:>2}x{p['h']:<2} at ({p['x']:>2},{p['y']:>2})"
              f"  overlay x{p['slots']}{follow}")
    n_tune = sum(len(v) for v in atlas["behaviour"].values())
    print(f"  behaviour  {n_tune} tuning constants in "
          f"{len(atlas['behaviour'])} blocks")


if __name__ == "__main__":
    main()
