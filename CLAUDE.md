# loafcat — working agreement

An open-source macOS desktop pet: a pixel cat that reacts to your cursor, your
typing, and to Claude Code's work state. Native Swift/AppKit, MIT licensed.

## Branching — required, not optional

**Every feature, fix or experiment gets its own branch off `main`. Never commit
directly to `main`.**

```
feat/<slug>     new capability          feat/agent-integration
fix/<slug>      bug fix                 fix/ear-outline-seam
art/<slug>      art or theme work       art/tuxedo-refinement
docs/<slug>     documentation only      docs/permission-screenshots
chore/<slug>    build, CI, deps         chore/sparkle-updater
```

Workflow:

1. `git switch -c feat/<slug>` from an up-to-date `main`
2. Commit in small, self-contained steps — each commit should build and run
3. Open a PR (`gh pr create`), even when working solo; the diff is the review
4. Merge only when the app builds AND has been run and visually confirmed
5. Delete the branch after merge

Commit messages: imperative mood, explain **why** when it isn't obvious.
`fix: draw ears behind head so the outline seam disappears` beats `fix ears`.

## Architecture — the two rules that matter

**1. No geometry, timing or behaviour constant in Swift.** Everything the cat knows
about its own body and behaviour lives in `assets/themes/<name>/cat.json`. This is
what lets art be regenerated or swapped for a community theme without touching code,
and what would let a future Windows app reuse the same data. If you find yourself
typing a pixel offset into a `.swift` file, it belongs in the atlas.

**2. Features are modules, not edits to `main.swift`.** Implement `CatModule` in its
own file and register it. This keeps parallel work from colliding in one file, and
it means a feature can be removed by deleting one file.

```
Sources/LoafCat/
  main.swift        app lifecycle, panel, menu bar, the 120Hz tick   <- rarely edit
  Atlas.swift       loads cat.json + part PNGs                        <- rarely edit
  Rig.swift         per-part transforms, springs, cursor tracking
  CatView.swift     CALayer compositor, hit mask
  Modules/*.swift   one file per feature
tools/generate_art.py   the entire art pipeline
assets/themes/<name>/   one self-contained directory per cat
spikes/                 throwaway experiments + RESULTS.md
```

## Hard-won facts — do not relearn these

Measured on this machine and written up in `spikes/RESULTS.md`. They contradict what
the documentation and most blog posts say.

- **There is no free per-pixel click-through on macOS.** A transparent `NSWindow`
  hands every click to itself regardless of alpha. Overriding `hitTest` to return
  `nil` is worse — the event reaches *nothing*. The only thing that works is polling
  the cursor at 120Hz, sampling a dilated alpha mask, and toggling
  `ignoresMouseEvents` on boolean transitions. Measured 97% vs 88% for the
  event-driven version every other desktop pet ships.
- **Window level must be 101** (`.popUpMenuWindow`). The Dock is at 20, so
  `.floating` (3) puts the cat behind it.
- **Set `.stationary` in `collectionBehavior`.** Any window with level != 0 defaults
  to `.transient`, which makes it vanish during Mission Control.
- **Use `NSScreen.visibleFrame`, never `.frame`.** This display reports
  `safeAreaInsets.top = 33` — the notch would bisect a cat walking the top edge.
- **`NSApplication` must be initialised before any `NSEvent`/`CGEventSource` call**,
  or the cursor position and event counters silently freeze with no error.
- **Only `CGEventSourceStateID.combinedSessionState` is safe.** `.hidSystemState`
  and `.privateState` block *indefinitely* for an unprivileged process — no error,
  no prompt, just a hang.

## Privacy — a design constraint, not a feature

The app asks for **zero permissions** and must stay that way. Typing reactions come
from `CGEventSource.counterForEventType`, which returns an integer count. There is no
code path by which a keycode could reach us, so content-blindness is structural
rather than a promise.

**Never add `uiohook`, `CGEventTap`, or a global keyboard monitor.** Their taps are
active filters that can read and suppress every keystroke system-wide, which is why
apps using them trigger the "control your computer" dialog most users bounce off.
Global *mouse* monitors are fine and need no permission.

If a feature seems to require a permission, it is the wrong design. Say so.

## Claude Code hooks — the one thing we must not get wrong

A badly written hook stalls the user's actual coding session. Sync hooks **block
Claude's execution**, and the default timeout is **600 seconds**.

Every hook we install must be `"async": true`, carry a short explicit `timeout`, use
a sub-second client-side network timeout, and `exit 0` unconditionally. Never hook
`MessageDisplay` — it holds every streamed batch until the hook returns.

Also: `Stop` does **not** fire on user interrupt (Esc), so anything driven by `Stop`
alone needs an idle-timeout backstop or the cat gets stuck looking busy. Exit code 1
does *not* block; only exit 2 does.

## Legal — the boundary

We clone Comnyang's **behaviour**, which is not copyrightable. We do not touch its
**art**, which is.

- Never open, fetch, trace or vendor any Comnyang asset. Not from
  `dnghngqun/comnyang-linux` (unlicensed, self-described as extracted from the paid
  Windows exe with license checks patched out), not from `comnyang.com/assets/`, not
  from the DMG. There is no clever path here.
- Don't reuse their UI strings — "Mochi Drag", "Overheat Mode", "Peek Mode". The
  behaviours are unprotectable ideas; the naming isn't worth the risk.
- "An open-source alternative to Comnyang" is fine in prose. "Comnyang" in the
  project name, repo name or icon is not.
- All art in `assets/` is generated by `tools/generate_art.py`. Keep it that way, and
  reject contributions of art with unclear provenance.

## Verification before claiming done

- `./build.sh` succeeds AND the app was launched and looked at. A build that
  compiles is not a feature that works.
- Pixel art: run idle for 60s at 2x and 3x. Any crawling or shimmering pixel means a
  fractional transform leaked in — round to whole *logical* pixels before scaling.
- Click-through: click a transparent area beside the cat; the app underneath must
  get it. "Nothing happens" is the correct outcome and looks identical to a bug.
- Hooks: kill the app, then run a long Claude Code task. It must finish at normal
  speed.
