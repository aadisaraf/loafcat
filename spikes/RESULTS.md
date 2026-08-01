# Spike results

Empirical findings that override the research. Measured on this machine, not read in docs.

**Environment:** macOS 26.3 (25D125), arm64 · Swift 6.2.3 · SDK 26.2 · 1710×1107 @2x, `safeAreaTop 33`

---

## S1 — Per-pixel click-through

**Question:** does a transparent `NSWindow` pass clicks through on transparent pixels, and by what mechanism? Research round 2 claimed AppKit gives it free via a `hitTest` override; round 1's verifier called that "unverified folklore."

**Method:** a transparent `NSPanel` at level 101 drawing an opaque disc, with an opaque target window directly beneath it *in the same process* so both ends are self-reporting. A local `NSEvent` monitor logs which window the window server actually chose, before view hit-testing runs — that's what separates "swallowed" from "never clicked."

### Result: **round 1 was right. There is no free per-pixel click-through.**

| Mode | Mechanism | Corner clicks passed | Swallowed | Verdict |
|---|---|---:|---:|---|
| A | plain transparent panel | 0 | 9 / 9 | window server hands every click to the panel regardless of alpha |
| B | `hitTest()` returns nil | 0 | 9 / 9 | **worst case** — view declines, window still consumes, event reaches *nothing* |
| C | alpha-sampled `ignoresMouseEvents`, event-driven | 46 | 6 | 88% — the industry-standard approach, and it visibly leaks |
| D | alpha-sampled, **polled 120Hz + 6px hysteresis** | 36 | 1 | **97% — use this** |

**Mode A settles it.** `NSWindow.ignoresMouseEvents` is binary and whole-window; a transparent pixel is still *the panel's* pixel as far as the window server is concerned. Apple's one-sentence doc has no discussion section, and the "free per-pixel" claim traces to an 8-star, unlicensed, Electron-8-era repo. It is folklore.

**Mode B is actively dangerous** and worth calling out, because it looks like the obvious solution. Returning `nil` from `hitTest` means no *view* handles the event — but the window still receives it, and nothing below ever sees it. Nine clicks vanished. A pet built this way would feel broken in a way that's hard to diagnose.

**Why D beats C.** The race in C: `ignoresMouseEvents` is set from a mouse-moved event, but the window server routes a click using whatever the flag was *when the click arrived*. Cross the silhouette boundary and click before the update lands, and it goes to the wrong place. Two fixes, both needed:

1. **Poll** `NSEvent.mouseLocation` at 120Hz instead of waiting to be told — bounds staleness to one tick.
2. **Dilate** the interactive region by ~6px, asymmetrically (hold interactive until clearly outside) — so we flip to interactive *before* the pointer arrives.

The cost of dilation is a few transparent pixels near the silhouette that stay clickable. At 6px on a 48px sprite that's invisible in use, and it buys most of the reliability.

### Consequences for the plan

- **Swift loses its main technical advantage over Electron.** Both stacks must fake click-through identically. Swift is still the right call for *this* project — Sparkle auto-updates without the $99 Apple fee, ~30–40MB vs 120–200MB, no unmeasured transparent-GPU risk — but not for the reason originally given.
- Poll the cursor for *everything*. The same 120Hz poll drives eye tracking, hunt detection, and petting. One timer, not three event monitors.
- Budget real work for hit-testing against the actual sprite silhouette: precompute a per-frame alpha bitmask at load, dilate at build time, index it at runtime. Never call `getImageData`-equivalent per poll.
- "Nothing happens" when clicking beside the cat is *correct*, and it looks identical to a bug. Keep the target-window harness around as a regression test.

### Confirmed in passing

- Window level **101** (`.popUpMenuWindow`) sits above the Dock (level 20). `.floating` (3) would not.
- `safeAreaInsets.top = 33` on this display — the notch. Use `visibleFrame` (origin y=80), never `frame`, or a cat walking the top edge gets bisected.
- Global monitors for **mouse** events need no permission. Global **keyboard** monitors trigger the Accessibility prompt — avoid them entirely.
- An ad-hoc signature (`codesign -s -`) is enough to run; Apple Silicon SIGKILLs a wholly unsigned binary.

**Reproduce:** `./clickthrough/build.sh && ./clickthrough/build/ClickThroughSpike.app/Contents/MacOS/ClickThroughSpike --mode D --cycle`

---

## S2 — Zero-permission input telemetry

**Question:** can the cat react to cursor, clicks, scrolling and typing without ever showing a TCC permission dialog?

**Method:** a signed `.app` polling `CGEventSource.counterForEventType` and `secondsSinceLastEventType` at 4Hz, plus `NSEvent.mouseLocation`. Contamination guarded by `AXIsProcessTrusted()` and `CGPreflightListenEventAccess()` — both report the grant *without* prompting.

### Result: **yes. Everything we need, zero prompts.**

Run with both permissions reported denied:

| Signal | Observed | API |
|---|---|---|
| Keystroke **rate** (never content) | 439 keystrokes, peak **24.7 kps** | `counterForEventType(.combinedSessionState, .keyDown)` |
| Cursor position | 127 distinct positions | `NSEvent.mouseLocation` |
| Clicks | 10 | `counterForEventType(… .leftMouseDown)` |
| Scroll | 63 | `counterForEventType(… .scrollWheel)` |
| Per-type idle time | 0.0s–142s | `secondsSinceLastEventType` |

The full state machine ran off these alone: `alert → kneading → OVERHEAT 62% → 100% → idle → dozing`.

**Why the contamination guard matters.** Launched from a shell, the same binary reported Accessibility **YES** — it inherits the terminal's grants as its responsible process. Launched from Finder with its own bundle id, it reports **no**. Only the Finder-launched number is honest; any spike of this kind run from a terminal is measuring the terminal.

### Consequences for the plan

- **Do not use `uiohook-napi` or any `CGEventTap`.** Comnyang uses uiohook, whose tap is `kCGEventTapOptionDefault` — an *active* filter that can read and suppress every keystroke system-wide, and therefore demands the full "control your computer" prompt. We need none of it. This is a real advantage over the original, and worth saying plainly in the README.
- Content-blindness is **structural, not a promise**. `counterForEventType` returns an integer. There is no code path by which a keycode could reach us, so the privacy claim is provable by inspection rather than by trust.
- One 120Hz poll drives everything — cursor for eye-tracking and hunt, counters for kneading and overheat. Same timer as the click-through sampler from S1.

### Two traps found the hard way

1. **`NSApplication` must be initialised before any `NSEvent` / `CGEventSource` call.** Without it the process has no window-server connection: `mouseLocation` returns a frozen point and the counters never advance — silently, with no error. One full run was wasted on this.
2. **Only `.combinedSessionState` is safe to read.** Querying `.hidSystemState` or `.privateState` **blocks indefinitely** for an unprivileged process — the call never returns and never prompts, so the app just hangs with no diagnostic.

**Reproduce:** `./permissions/build.sh && open ./permissions/build/PermissionSpike.app` — must be `open`, not a shell launch, or the reading is contaminated. Output at `~/loafcat-permission-spike.log`.

---

## S3 — Separating a cat-toy wiggle from a fast cursor sweep

**Question:** hunting has to fire when the cursor is waggled like a toy and stay silent when it merely crosses the screen quickly. Is speed enough to tell them apart, and if not, what is?

**Method:** `reactions/` links the real reaction modules and drives them in real time at 120Hz through the same signal path `main.swift` builds — 1.5s sliding keystroke window, cursor in logical pixels relative to the cat's centre, same EMA on velocity. Eleven scenarios, each asserting states that must appear and states that must not.

### Result: **speed is not just insufficient, it points the wrong way.**

| Gesture | Peak speed (logical px/s) | Peak accumulator | Hunts? |
|---|---:|---:|---|
| 1700pt straight sweeps, 0.45s each | **2768** | 0.40 | no |
| ±120pt wiggle at 5Hz | **1459** | 1.13 | yes, at 0.79s |
| slow aimless mousing | 287 | 0.00 | no |

**The gesture that must trigger is half the speed of the gesture that must not.** Any threshold on `|v|` gets this exactly backwards. What separates them is direction reversals, so the accumulator's reversal bonus (0.62 per turn) has to dominate its speed term, whose ceiling is bounded by construction at `excess × gain / (1 − decay)` and lands at 0.40 for the fastest plausible sweep.

Two details that are not obvious:

- **Compare headings across ~60ms, not across one frame.** At 120Hz, after the EMA the frame-to-frame angle is mostly smoothing noise.
- **A reversal needs a refractory window.** One real turn spans several frames and otherwise gets paid for four or five times, which lets a single flick trigger a pounce.

Confirmed live against the running app, driven by `CGWarpMouseCursorPosition`: straight sweeps peaked at 2089 logical px/s with the accumulator at 0.39 and **0** hunting frames; a 5Hz wiggle peaked at 1692 and hunted in **42 of 52** sampled frames.

**Trap:** a warp is instantaneous, so moving the cursor between test phases is an infinite-speed reversal and fires the very detector under test. The driver has to glide, not teleport. The first live run "failed" entirely on this.

**Reproduce:** `./reactions/build.sh && ./reactions/build/ReactionSpike` from the repo root.

---

## S4 — Proving the art does not crawl

**Question:** the pixel-art rule is that every offset rounds to a whole *logical* pixel before scaling. After adding per-part module offsets and free-floating overlay sprites, does it still hold at 2x/3x/4x?

**Method:** three approaches, in order of how well they worked.

1. **Screen-grab the running app and check every pixel is on-palette.** Useless here — several cats from parallel sessions sit at the same default screen position, so every capture is a composite of all of them.
2. **Render the layer tree offscreen and check the same thing.** Removes the desktop from the question but measures Core Graphics' colour management and edge anti-aliasing as much as our geometry, and the rig's breathing is a deliberately fractional *scale* that blends edges by design. ~50% "off-palette" pixels, all of it noise. No conclusion available.
3. **Walk `view.layer!.sublayers` and assert every position and bound is an exact multiple of the render scale.** This is the actual claim, tested directly.

### Result: **grid intact — 4.4M coordinates, zero off-grid.**

60 simulated seconds per configuration at 120Hz, 3 themes × 3 scales. The first 30s is a pure idle soak; the second 30s drives every module channel at deliberately fractional amplitudes (`sin(t·3.1)·2.37 + 0.41`), because a rounding bug hides completely at integer offsets.

| | coordinates checked | off-grid | worst error |
|---|---:|---:|---:|
| mono / tuxedo / cream at 2x, 3x, 4x | 4,404,036 | **0** | 0.000000 lpx |

The one fractional transform left is the squash/breathe **scale** on the body and shadow. It is applied about a pivot rather than by translation, it predates this work, and it is what breathing *is*.

**Worth knowing:** don't test this by rendering and comparing colours. The claim is about geometry; test the geometry.

**Reproduce:** `./pixelgrid/build.sh && ./pixelgrid/build/PixelGridSpike` from the repo root.
