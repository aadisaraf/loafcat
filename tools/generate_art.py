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
}


# ---------------------------------------------------------------------------
# Behaviour -- the tuning table for every reaction, shipped inside cat.json.
#
# It lives here for the same reason the geometry does: rule 1 of the architecture is
# that no behaviour constant is typed into a .swift file. Keeping the numbers in the
# atlas means a theme can ship a lazier or a twitchier cat without a rebuild, a
# tuning pass is a JSON diff rather than a recompile, and a future Windows port reads
# exactly the same table.
#
# Units are seconds, LOGICAL pixels (the 48px canvas, not screen points) and logical
# px/sec. Anything named `*_per_frame` is quoted at a nominal 60fps and normalised by
# dt at runtime, so the 120Hz tick behaves identically.
# ---------------------------------------------------------------------------
BEHAVIOUR = {
    # --- kneading -----------------------------------------------------------
    # The gate: react only once `burst_keys` land inside `burst_window`. Since the
    # runtime hands modules a sliding-window RATE, that condition is exactly
    # kps >= burst_keys / burst_window -- an isolated keypress cannot reach it.
    "typing.burst_keys": 5.0,
    "typing.burst_window": 2.0,
    "typing.release_delay": 0.18,   # back to idle this long after the last key
    "typing.paw_period": 0.17,      # one paw stroke; paws alternate each stroke
    "typing.paw_lift": 2.4,
    "typing.paw_reach": 1.4,
    "typing.body_bob": 0.8,
    "typing.squash": 0.03,
    "typing.attack": 0.10,          # seconds to reach full amplitude
    "typing.decay": 0.16,           # seconds to fall back out of it

    # --- overheat -----------------------------------------------------------
    # A continuous curve, never a binary state. Zero below kps_min so ordinary
    # typing never reddens the cat; 1.0 at kps_max. The exponent front-loads the
    # curve's flat part into the middle of the range, so the tint only becomes
    # obvious when the typing is genuinely frantic.
    "overheat.kps_min": 4.0,
    "overheat.kps_max": 14.0,
    "overheat.curve": 1.5,
    "overheat.ease_per_frame": 0.10,
    "overheat.state_at": 0.55,      # heat above which overheat outranks kneading
    "overheat.steam_at": 0.30,
    "overheat.steam_period": 0.85,
    "overheat.steam_rise": 11.0,

    # --- hunting ------------------------------------------------------------
    # An energy accumulator that rewards REVERSALS, not speed. A straight sweep
    # only ever earns the speed term, whose ceiling (gain * excess / (1 - decay))
    # is deliberately below the trigger; a wiggle earns reversal bonuses on top and
    # crosses it within two or three direction changes.
    "hunt.decay_per_frame": 0.82,
    "hunt.speed_min": 300.0,
    "hunt.speed_gain_per_frame": 0.000034,
    "hunt.reverse_lag": 0.06,       # compare against velocity this long ago
    "hunt.reverse_dot": -0.28,      # cos of ~106 degrees
    "hunt.reverse_speed": 520.0,
    "hunt.reverse_bonus": 0.62,
    "hunt.reverse_refractory": 0.09,
    "hunt.accel_min": 12000.0,
    "hunt.accel_gain_per_frame": 0.0000009,
    "hunt.trigger": 1.0,
    "hunt.reset": 0.35,             # not 0 -- keep stalking cheap to re-trigger
    "hunt.crouch": 1.1,
    "hunt.recover": 0.4,
    "hunt.attack": 0.12,
    "hunt.squash": 0.93,
    "hunt.lean": 2.6,
    "hunt.body_lean": 1.2,
    "hunt.paw_reach": 1.6,
    "hunt.wiggle_hz": 5.5,          # the haunch-wiggle before a pounce
    "hunt.wiggle_amp": 0.7,

    # --- petting ------------------------------------------------------------
    # The hit region is an ellipse over the head, sized from the head part's own
    # rectangle in this atlas rather than from a number typed into Swift.
    "pet.ellipse_scale": 1.0,
    "pet.move_min": 14.0,           # logical px/sec; a parked cursor is not petting
    "pet.stop_delay": 0.42,
    "pet.leave_delay": 0.26,
    "pet.lean": 2.4,
    "pet.purr_hz": 9.0,
    "pet.purr_amp": 0.7,
    "pet.squash": 0.97,
    "pet.attack": 0.14,
    "pet.heart_period": 0.55,
    "pet.heart_rise": 12.0,
    "pet.heart_drift": 2.5,

    # --- scrolling ----------------------------------------------------------
    "scroll.hold": 0.52,
    "scroll.bob": 2.0,
    "scroll.bob_hz": 4.5,
    "scroll.paw": 1.6,
    "scroll.attack": 0.06,
    "scroll.decay": 0.18,
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
    # arguably wants a wider petting ellipse than a shaded one.
    for k, v in t.get("behaviour", {}).items():
        BEHAVIOUR[k] = v
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

# Sprites that are not part of the cat's body: they appear above it, in numbers,
# and move independently. Kept out of ORDER so they never enter the draw stack or
# the click-through mask. `slots` is how many may be on screen at once, which is
# how many layers the view preallocates -- overlays must never allocate per frame.
OVERLAYS = {"steam": 3, "heart": 3}


def build_hot_parts():
    """Re-runs the generator with the coat keys remapped to the hot ones."""
    saved = {k: PALETTE[k] for k in HOT_REMAP}
    for k, v in HOT_REMAP.items():
        PALETTE[k] = PALETTE[v]
    try:
        return build_parts()
    finally:
        PALETTE.update(saved)


def build_overlay_parts():
    """Steam and hearts. Both are drawn INSIDE the 48px canvas on purpose.

    The panel is exactly one canvas square, so anything a module animates outside
    it is clipped by the window server and simply never appears. The anchors below
    are therefore chosen with headroom: steam beside the head (the ears already own
    the space directly above it) and hearts on the brow, each with ~11px of clear
    canvas to rise into.
    """
    out = {}

    # steam: three puffs of falling size, curling up and to the right
    img = new_layer()
    for cx, cy, r in ((42, 20, 2.4), (43.4, 17.6, 1.7), (41.4, 16.0, 1.2)):
        fill_spans(img, disc_spans(cx, cy, r), "accent")
    outline(img)
    out["steam"] = img

    # heart: the classic 7x6 pixel heart, outlined like every other part
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
    out["heart"] = img

    return out


def crop_and_write(parts, hot_parts=None, overlay_parts=None):
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
        "behaviour": dict(sorted(BEHAVIOUR.items())),
    }

    def write(name, img, bbox=None):
        """Writes one part. An explicit bbox pins a variant to its base's grid."""
        box = bbox or img.getbbox()
        if box is None:
            return None
        img.crop(box).save(os.path.join(PARTS, f"{name}.png"))
        atlas["parts"][name] = {
            "file": f"parts/{name}.png",
            "x": box[0], "y": box[1],
            "w": box[2] - box[0], "h": box[3] - box[1],
        }
        return box

    for name in [n for n in ORDER if n not in HIDDEN]:
        bbox = write(name, parts[name])
        if bbox is None:
            continue
        # Overheat variant, if the remap actually changed this part. Cropped with
        # the BASE part's box, not its own, so the two images are the same size at
        # the same origin and the cross-fade cannot shift a pixel.
        hot = (hot_parts or {}).get(name)
        if hot is not None and hot.tobytes() != parts[name].tobytes():
            write(f"{name}_hot", hot, bbox)
            atlas.setdefault("hot", []).append(name)

    atlas["overlays"] = {}
    for name, slots in OVERLAYS.items():
        img = (overlay_parts or {}).get(name)
        if img is None or write(name, img) is None:
            continue
        atlas["overlays"][name] = {"slots": slots}

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
    with open(os.path.join(OUT, "cat.json"), "w") as f:
        json.dump(atlas, f, indent=2)
    return atlas


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
    atlas = crop_and_write(parts, build_hot_parts(), build_overlay_parts())

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

    used = {p for p in atlas["parts"]}
    print(f"wrote {len(used)} parts -> {PARTS}")
    print(f"atlas -> {os.path.join(OUT, 'cat.json')}")
    print(f"preview -> {os.path.join(OUT, 'preview.png')}")
    for n in atlas["order"]:
        if n in atlas["parts"]:
            p = atlas["parts"][n]
            hot = " +hot" if n in atlas.get("hot", []) else ""
            print(f"  {n:<10} {p['w']:>2}x{p['h']:<2} at ({p['x']:>2},{p['y']:>2}){hot}")
    for n, cfg in atlas["overlays"].items():
        p = atlas["parts"][n]
        print(f"  {n:<10} {p['w']:>2}x{p['h']:<2} at ({p['x']:>2},{p['y']:>2})"
              f"  overlay x{cfg['slots']}")
    print(f"  behaviour  {len(atlas['behaviour'])} tuning constants")


if __name__ == "__main__":
    main()
