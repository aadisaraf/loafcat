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

## S3 — `CGBitmapContext` row order, and the hit mask it silently mirrored

**Question:** when you draw a `CGImage` into a `CGBitmapContext` to read its alpha, is buffer row 0 the image's top row or its bottom row?

**Method:** `ear_l.png` is a triangle — PIL confirms **1** opaque pixel in its top row and **5** in its bottom. Draw it through the exact call both `CatView.buildHitMask` and `PixelBitmap` use, then count.

### Result: **buffer row 0 is the image's TOP row.** Both readers had it backwards.

```
image 15x19   PIL: top row has 1 px, bottom row has many
memory row 0     -> 1 opaque px
memory row 18    -> 5 opaque px
=> memory row 0 IS the image's TOP row
```

The confusion is real and worth naming: a `CGContext`'s **user space** is y-up with the origin bottom-left, so drawing at `CGRect(0, 0, w, h)` puts the image right way up *in user space*. Its **backing store** is top-down regardless. The two facts are independent, and the y-up one is the one everybody remembers.

**What it cost.** `buildHitMask` indexed `buf[(h - 1 - py) * w + px]`, mirroring every part's alpha inside its own crop box. The 6px dilation smoothed the round parts enough that nobody noticed — the ear triangles were the giveaway, sampled apex-down. Click-through worked; it just didn't match the silhouette near the ears. The same mistake in the new speech-bubble compositor was instant and obvious: every glyph rendered upside down.

### Consequences for the plan

- Read the alpha with `buf[(py * w + px) * 4 + 3]`, `py` counting down from the atlas top. No flip.
- **A bug that a dilation can hide is a bug that will not be found by looking.** The hit mask needs the kind of check that does not depend on a human noticing 6px — see `spikes/hitmask`, which asserts the interactive area is exactly the mask area × scale², at 2x/3x/4x and under the stretch break's magnification.
- Anything that composes pixel art at 1x should be dumped as ASCII and diffed against the generator's own preview. Two implementations of the same layout disagreeing is a much louder signal than one implementation looking slightly off.

**Reproduce:** `swiftc -o /tmp/hitmask Sources/LoafCat/{Atlas,CatStage,CatModule,CatView,Rig,PixelCanvas,SpeechBubble}.swift spikes/hitmask/main.swift && /tmp/hitmask mono`

---

## S4 — Separating a cat-toy wiggle from a fast cursor sweep

**Question:** hunting has to fire when the cursor is waggled like a toy and stay silent when it merely crosses the screen quickly. Is speed enough to tell them apart, and if not, what is?

**Method:** `reactions/` links the real reaction modules and drives them in real time at 120Hz through the same signal path `main.swift` builds — 1.5s sliding keystroke window, cursor in logical pixels relative to the cat's centre, same EMA on velocity. Eleven scenarios, each asserting states that must appear and states that must not.

### Result: **speed is not just insufficient, it points the wrong way.**

| Gesture | Peak speed (logical px/s) | Peak accumulator | Hunts? |
|---|---:|---:|---|
| 1700pt straight sweeps, 0.45s each | **2769** | 0.40 | no |
| ±120pt wiggle at 5Hz | **1416** | 1.13 | yes |
| slow aimless mousing | 287 | 0.00 | no |

Re-run after the merge with the agent and wellness modules linked in alongside: all eleven scenarios still pass, and the numbers move only in the last digit of the speed columns, which is genuine 120Hz jitter — the harness runs in real time on purpose.

**The gesture that must trigger is half the speed of the gesture that must not.** Any threshold on `|v|` gets this exactly backwards. What separates them is direction reversals, so the accumulator's reversal bonus (0.62 per turn) has to dominate its speed term, whose ceiling is bounded by construction at `excess × gain / (1 − decay)` and lands at 0.40 for the fastest plausible sweep.

Two details that are not obvious:

- **Compare headings across ~60ms, not across one frame.** At 120Hz, after the EMA the frame-to-frame angle is mostly smoothing noise.
- **A reversal needs a refractory window.** One real turn spans several frames and otherwise gets paid for four or five times, which lets a single flick trigger a pounce.

Confirmed live against the running app, driven by `CGWarpMouseCursorPosition`: straight sweeps peaked at 2089 logical px/s with the accumulator at 0.39 and **0** hunting frames; a 5Hz wiggle peaked at 1692 and hunted in **42 of 52** sampled frames.

**Trap:** a warp is instantaneous, so moving the cursor between test phases is an infinite-speed reversal and fires the very detector under test. The driver has to glide, not teleport. The first live run "failed" entirely on this.

**Reproduce:** `./reactions/build.sh && ./reactions/build/ReactionSpike` from the repo root.

---

## S5 — Proving the art does not crawl

**Question:** the pixel-art rule is that every offset rounds to a whole *logical* pixel before scaling. After adding per-part module offsets and free-floating overlay sprites, does it still hold at 2x/3x/4x?

**Method:** three approaches, in order of how well they worked.

1. **Screen-grab the running app and check every pixel is on-palette.** Useless here — several cats from parallel sessions sit at the same default screen position, so every capture is a composite of all of them.
2. **Render the layer tree offscreen and check the same thing.** Removes the desktop from the question but measures Core Graphics' colour management and edge anti-aliasing as much as our geometry, and the rig's breathing is a deliberately fractional *scale* that blends edges by design. ~50% "off-palette" pixels, all of it noise. No conclusion available.
3. **Walk the layer tree and assert every position and bound is an exact multiple of the render scale.** This is the actual claim, tested directly.

**A trap the first version fell into.** It walked `view.layer!.sublayers` — one level. Once the padded panel put every part inside a centred *container* layer, that one level was the container and nothing else: 28,800 coordinates, all trivially on-grid, a vacuous PASS. The check now recurses, which is also what brings the overheat coats, the overlay slots and the wellness tint sublayers into it. **A harness that passes for the wrong reason is worse than one that fails.**

### Result: **grid intact — 6.7M coordinates, zero off-grid.**

60 simulated seconds per configuration at 120Hz, 3 themes × 3 scales. The first 30s is a pure idle soak; the second 30s drives every module channel at deliberately fractional amplitudes (`sin(t·3.1)·2.37 + 0.41`), because a rounding bug hides completely at integer offsets.

| | coordinates checked | off-grid | worst error |
|---|---:|---:|---:|
| mono / tuxedo / cream at 2x, 3x, 4x | 6,706,476 | **0** | 0.000000 lpx |

Re-measured after both feature branches were merged, with the recursive walk and the real padded panel size (2x: 256×268pt, not 96×96).

The one fractional transform left is the squash/breathe **scale** on the body and shadow. It is applied about a pivot rather than by translation, it predates this work, and it is what breathing *is*.

**Worth knowing:** don't test this by rendering and comparing colours. The claim is about geometry; test the geometry.

**Reproduce:** `./pixelgrid/build.sh && ./pixelgrid/build/PixelGridSpike` from the repo root.

---

## S6 — Detecting a full-screen video without asking for anything

**Question:** the cat should get out of the way for a full-screen video. Can either half of that — "is a window covering this display" and "is a video playing" — be answered by a process with no permissions at all? If not, the feature is the wrong design and has to be dropped (CLAUDE.md).

**Method.** Two probes. The first ran from the terminal and was *worthless*: iTerm2 has Screen Recording and Accessibility granted, and anything it spawns inherits that. So the second probe was built as its own ad-hoc-signed `.app` with a bundle identifier nothing had ever seen, launched through `open` so launchd — not the terminal — is its responsible process. It reports what it was granted before it reports anything else.

### Result: **both, and with room to spare.**

```
bundle id: dev.loafcat.fsprobe.a1b2c3
ScreenRecording granted: false
Accessibility granted:   false
windows: 80  bounds:80 owner:80 layer:80 pid:80 NAME:1
IOPM ok: PreventUserIdleDisplaySleep=0 PreventUserIdleSystemSleep=1
```

With both permissions **denied**, `CGWindowListCopyWindowInfo` returned bounds, owner, layer and pid for all 80 on-screen windows. Only `kCGWindowName` is gated — 46 of 80 windows had a name when run from the privileged terminal, 1 of 80 from the unprivileged bundle. A desktop pet does not need titles, and `check-privacy.sh` bans the field outright.

`IOPMCopyAssertionsStatus` — the table `pmset -g assertions` prints — needs no privilege either.

### Why the assertion is the interesting half

"Is a video playing" cannot be asked directly without recording the screen. `PreventUserIdleDisplaySleep` is the closest honest proxy: **every** video player takes it so the picture cannot dim mid-scene, and nothing that is merely being typed into does. That is the whole difference between parking the cat for a film and parking it for a full-screen text editor. Measured at 0 on an idle machine, 1 under `caffeinate -d`.

It is required to **enter** the parked state and not to **stay** in it, because a paused film drops the assertion and a cat that walked back out in front of the picture on every pause would be worse than one that never moved.

**Windows counterpart:** `CallNtPowerInformation(SystemExecutionState)` → `ES_DISPLAY_REQUIRED`, which is the same question and also needs no privilege — unlike `powercfg /requests`, which needs admin. The window half is cheaper there: the foreground window's rect answers it in three calls, so that port polls inline while this one does the 80-dictionary enumeration on a background queue.

### Verified end to end

A synthetic film — `caffeinate -d` plus a layer-0 window at exactly the display bounds, held at 3% alpha so testing it does not black out the machine — then removing each half in turn:

| | fs | awake | parked | window x |
|---|---:|---:|---:|---:|
| nothing playing | 0 | 0 | 0.00 | 727 |
| full screen + awake | 1 | 1 | 1.00 | 1572 |
| "paused" — assertion dropped, still full screen | 1 | 0 | **1.00** | 1572 |
| left full screen | 0 | 0 | 0.00 | **727** |

**One real bug, found only because the last column was measured.** The cat first walked home to x=**728** and stopped there for good, one point short. The slide eases exponentially toward its target and read its own position back off the window each frame — but the window server quantises that position, so the last sub-point steps were rounded away faster than they could accumulate. Keep the slide in a float of your own and round only on the way out. It is the same lesson as the pixel grid in S5, arriving from the opposite direction: there the danger was a fractional value reaching the screen, here it was the screen's integer value being read back as truth.

**Reproduce:** `./build/LoafCat.app/Contents/MacOS/LoafCat --demo-peek`, and on Windows `loafcat.exe --demo-peek`. Both print the same summary line.

### The test that locked in the wrong pose

Shipped, the parked cat looked like a whole face hanging beside the screen edge with two grey nubs under it. Nothing was behind the edge, so nothing read as hiding.

The number was `reveal_px: 28` against a head whose ink is **30px wide** — 93% of the head on screen. But the number was not the mistake. The mistake was the assertion that produced it: *"A WHOLE FACE is the point of this pose, so both eyes have to clear the edge."* Twenty-eight is the smallest reveal that satisfies it, so the check did its job perfectly and its job was wrong.

That assertion was written for the **previous** pose, where the whole cat was clipped vertically and a second eye on screen genuinely did mean the window had sliced through the animal. When the pose changed to head-and-paws with the body absent rather than clipped, the check came along unexamined — and a passing test is a much stickier way to be wrong than a magic number, because it argues back.

The correct claim is the near eye out and the far eye **wholly** behind the edge, which is 15 — exactly half the head:

| reveal | head hidden | reads as |
|---:|---:|---|
| 28 | 7% | a floating head |
| 20 | 33% | a head with a bite out of it |
| **15** | **50%** | one ear, one eye, one paw — a peek |

Stated as *where the far eye's near rim falls*, not as `!shows(eye_r)`: the shows/hides helpers carry a one-pixel tolerance, and a far eye with a single column poking out would satisfy the negation while still looking like two eyes.

The second half was the paws. `paw_rise_px: 10` stopped them 9px short of the head's box, and since paws are drawn *before* the head, clearing it means they float in the gap under the jaw instead of the chin resting on them. Thirteen puts the paw 4px inside the box. Asserted directly — paw top against head bottom — because "looks attached" is not a thing a demo can see.

**A design decision encoded as a passing check needs the reasoning stored beside it, or the next pose inherits it.** Both flipped checks now carry why the old one existed and why it no longer applies.
