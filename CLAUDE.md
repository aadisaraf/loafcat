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

**1. No geometry, timing or behaviour constant in code.** Everything the cat knows
about its own body and behaviour lives in `assets/themes/<name>/cat.json`. This is
what lets art be regenerated or swapped for a community theme without touching code,
and it is what the Windows app is built on — both builds load the same `cat.json` and
the same part PNGs, verbatim. If you find yourself typing a pixel offset into a
`.swift` or `.cs` file, it belongs in the atlas. **Retuning the cat is a JSON diff,
and it must retune both platforms.**

**2. Features are modules, not edits to `main.swift`.** Implement `CatModule` in its
own file and register it. This keeps parallel work from colliding in one file, and
it means a feature can be removed by deleting one file.

**A module must never hold a `Rig` or a `CatView`.** Both are thrown away and rebuilt
on every theme or scale change while modules live for the whole session, so a
captured reference is a dead one the first time somebody picks another cat. Publish
through `CatStage.shared` instead: per-part offsets, heat, overlay instances, and a
request for a keyframed animation. Read your tuning from `CatStage.shared.atlas` via
`AtlasTuned`, which re-reads on a theme switch. The settings window is held to the
same rule, which is why it reaches the app through the `SettingsHost` protocol.

**3. A new option goes in Settings; the menu bar is for actions.** Add a control to
the relevant `SettingsPane` (or a new pane), have the pane read and write it in
`refresh()`, and expose whatever the module needs to apply it. Menu bar items are
for things you *do* — stretch now, centre, quit — not for things you *set*. An
option reachable from two places is an option that will eventually disagree with
itself.

```
Sources/LoafCat/
  main.swift        app lifecycle, panel, menu bar, the 120Hz tick   <- rarely edit
  Atlas.swift       loads cat.json + part PNGs                        <- rarely edit
  CatStage.swift    the mailbox modules publish through, and the keyframe clock
  Rig.swift         per-part transforms, springs, cursor tracking
  CatView.swift     CALayer compositor, hit mask
  SettingsWindow.swift  every user-facing option, one pane per area
  Branding.swift    asset paths, the icon, theme thumbnails
  Modules/*.swift   one file per feature
windows/LoafCat/       the Windows app -- same filenames, same responsibilities
  Program.cs           <- main.swift        CatWindow.cs   the layered window
  CatView.cs           the software compositor (no Core Animation equivalent)
  Interop/             P/Invoke, input telemetry, monitors -- no macOS counterpart
  Modules/*.cs         one file per feature, mirroring Modules/*.swift
windows/build.ps1      builds dist/loafcat-<version>-win-x64.zip
tools/generate_art.py   the entire art pipeline
tools/generate_icon.py  app icon, menu bar glyph and .ico, from the mono parts
tools/generate_dmg_background.py  the disk image window, same parts, same font
tools/make-dmg.sh       builds dist/loafcat-<version>.dmg
assets/themes/<name>/   one self-contained directory per cat, read by BOTH builds
spikes/                 throwaway experiments + RESULTS.md
```

**The two ports are kept diffable on purpose.** `Rig.swift` and `Rig.cs` are the same
file in two languages, in the same order, with the same comments where the reasoning
is the same. A behaviour change made in one and not the other is a bug, and the way
you catch it is by reading them side by side — so do not "improve" the structure of
one without doing the same to the other.

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
- **`CGWindowListCopyWindowInfo` needs no permission; only `kCGWindowName` does.**
  Measured from an ad-hoc bundle with Screen Recording *and* Accessibility denied:
  bounds, owner, layer and pid came back for all 80 on-screen windows, and 1 of 80
  had a name (46 of 80 from a terminal that *was* granted). So "is a window covering
  this display" is free and "what is that window" is not — which is the right side of
  the line to be on anyway. Test this from a fresh bundle launched by `open`, never
  from your terminal: anything spawned there inherits the terminal's grants and the
  measurement is worthless.
- **`IOPMCopyAssertionsStatus` is the only honest way to ask "is a video playing."**
  It needs no privilege, and `PreventUserIdleDisplaySleep` is taken by every video
  player and by nothing that is merely being typed into. Require it to *enter* a
  "get out of the way" state and not to *stay* in one — a paused film drops the
  assertion, and a cat that walked back in front of the picture on every pause would
  be worse than one that never moved.
- **Never read a window position back off the window as the source of truth.** The
  window server quantises it. An exponential ease toward a target takes smaller and
  smaller steps, so once they fall under the quantum they round away faster than they
  accumulate and the cat stops short *for ever* — measured stalling at x=728 walking
  home to 727. Keep the position in a float you own and round only on the way out.
  This is the pixel-grid rule arriving from the opposite direction: there the danger
  is a fractional value reaching the screen, here it is the screen's integer coming
  back as truth.

## Windows — hard-won facts, same as the ones above

Measured or established while porting; written up at length in `windows/README.md`.
Several of these contradict the macOS findings, which is the point of writing them
down separately rather than assuming the Mac answer transfers.

- **Click-through IS free on Windows, and exact.** A `WS_EX_LAYERED` window presented
  with `UpdateLayeredWindow` is hit-tested by the window manager against the alpha
  channel you just handed it, synchronously, before the click is delivered to anyone.
  The 120Hz polling toggle that macOS needs is not reproduced and must not be added
  back — there is no race to lose a click to. The dilated mask survives only as the
  definition of `CursorOnCat`, which is a proximity question.
- **The window needs four extended styles and each is load-bearing.** `WS_EX_LAYERED`
  (alpha + hit testing), `WS_EX_TOOLWINDOW` (out of Alt-Tab and the taskbar),
  `WS_EX_NOACTIVATE` (clicking the cat must not steal focus from the editor), and
  `WS_EX_TOPMOST`. Reassert topmost periodically: a full-screen app coming and going
  leaves any topmost window behind it in the z-order.
- **Use `MONITORINFO.rcWork`, never `rcMonitor`.** The work area excludes the taskbar.
  This is the exact counterpart of `NSScreen.visibleFrame` versus `.frame`, and
  getting it wrong starts the cat underneath the taskbar, which reads as a failed
  launch.
- **Declare per-monitor DPI awareness v2, in the csproj and not the manifest.**
  Without awareness Windows bitmap-stretches the whole window by the display scale
  factor, bilinear — the exact mush the art pipeline exists to prevent. The Windows
  Forms SDK errors (WFAC010) if the manifest declares it too, so it lives in
  `ApplicationHighDpiMode`.
- **A WinForms `Timer` cannot drive 120Hz.** It is WM_TIMER-based, so it tops out near
  64Hz and jitters. Use a high-resolution waitable timer on its own thread posting to
  the UI thread — and **not** `timeBeginPeriod(1)`, which raises the timer resolution
  for the whole system and costs every other process battery.
- **`UpdateLayeredWindow` wants PREMULTIPLIED alpha**, and it carries the window's
  position and size, so a frame that moved must be presented even when the pixels are
  identical.
- **A low-level hook's delegate must be held in a static field.** If it is only a
  local, the GC collects it while Windows still holds the function pointer and the
  process dies inside user32 with a stack that points nowhere.
- **`GetAsyncKeyState` is banned outright, including for mouse buttons.** The one
  thing it was wanted for — "is the left button still held" — comes from the mouse
  hook instead, so the ban needs no argument about which constant was passed.
- **Timestamp a mouse event with `MSLLHOOKSTRUCT.time`, never `GetTickCount()` in the
  callback.** The second times the *callback*, not the *event*, so it never equals what
  `GetLastInputInfo` reports for that same event — and since keystrokes are inferred by
  eliminating the mouse, every mouse move then counts as typing. Measured at 64 phantom
  keystrokes a second against an `overheat.kps_min` of 4: the cat sat there steaming
  while its owner only moved the cursor. Compare tick counts **signed**; unsigned makes
  a one-millisecond disagreement look like four billion.
- **`GetLastInputInfo` updates before the hook runs.** They are different threads, so a
  poll routinely sees the tick of a mouse event the hook has not recorded yet. Any
  "was that the mouse?" verdict has to be held long enough for the hook to catch up —
  50ms — or the race alone reproduces the bug above.

- **"Not the mouse" is not "a key", and assuming it is has now caused the same bug
  twice in opposite directions.** `GetLastInputInfo` is reset by *any* raw input the
  session receives, and plenty of it never becomes a mouse message: a finger resting on
  a precision touchpad reports at ~125Hz without moving the cursor, so does a hand on a
  high-polling-rate mouse, so does a controller left plugged in. The hook cannot see any
  of it. Shipped, that read as a cat overheating **while the cursor sat still** and
  cooling down the moment the mouse moved — the inverse of the phantom-typing bug, from
  the same false dichotomy. A keystroke has to be positively *shaped* like one: isolated
  by at least 25ms on both sides (which is why the 50ms deferral above is load-bearing
  twice — it is what makes the successor knowable), and part of a stream no faster than
  22/s. Every device that reports while idle runs at 60Hz or more, and anything above
  64Hz collapses onto the `GetTickCount` grid, so the gap test closes all of them.

- **Do not add an evenness test to that, however obvious it looks.** A clock repeats its
  interval exactly and hands never do — but the stream has already been through an 8.3ms
  poll, and that quantisation destroys the jitter the test needs. Measured: at 18 keys a
  second with a generous ±15% wander it discarded 45 of 159 genuine keystrokes. Tried at
  two window lengths, same answer both times.

- **A snap gesture cannot use a modifier key, and the dwell is better anyway.**
  `GetAsyncKeyState`, `GetKeyState` and `GetKeyboardState` are banned outright, so
  there is no way to read Alt that both ports could share — and macOS itself moved to
  dwell-to-tile. Make the affordance appear *only* once the snap is armed, so "no line
  means no snap" is a fact the user can see rather than a promise. A dwell is also the
  only design where brushing an edge on the way past is naturally distinct from
  meaning it.
- **`CallNtPowerInformation(SystemExecutionState)` is the `IOPMCopyAssertionsStatus`
  counterpart** and needs no privilege. `powercfg /requests`, the obvious thing to
  reach for, needs admin. The full-screen half is much cheaper here than on macOS —
  the foreground window's rect answers it in three calls, against enumerating every
  window on screen — so this port polls it inline and that one does not.
- **Compare a candidate full-screen window against `rcMonitor`, not `rcWork`.** This
  is the one place the usual advice inverts: real full screen covers the taskbar and a
  merely maximised window does not, and that difference is the entire reason a
  maximised terminal is not mistaken for a film. Everywhere the cat is *placed*, it is
  still `rcWork`.
- **The cat is always running, so an install that defers to a running copy never runs.**
  Windows refuses to replace a running executable, and a desktop pet is running by
  definition — so at the one moment installing matters, the file on disk is locked.
  Reading that as "another instance is here, stand down" looks like correct
  single-instance behaviour and is the bug: a newer download hit the locked file, gave
  up, installed nothing, reported nothing, and brought the **old** cat forward. Shipped
  in v0.2.0, and CI could not have caught it because every Windows check returns before
  `SelfInstall` — `--selftest` exits first by design, so not one line of it had ever
  executed. Ask the running copy to quit through its own event so it exits cleanly and
  takes its tray icon with it; end it only if it will not.
- **A tray balloon is not a confirmation of anything.** `ShowBalloonTip` is discarded
  without error by Focus Assist, Do Not Disturb, or notifications being off. It was the
  entire announcement that an install had happened, which made success and silent
  refusal look the same. If a user has to have seen it, it is a window.
- **One loose `.exe` has no name of its own.** macOS gets this free: a `.app` is a
  bundle you drag to Applications. On Windows it takes three things, and all three are
  load-bearing. Set `AssemblyTitle` (it becomes `FileDescription`, which Task Manager and
  the startup list read) and `AssemblyName` (the file name, which is what everything
  *else* falls back to — spell it `loafcat`, the way the project spells it). Publish the
  bare executable as `loafcat.exe`: the version and the architecture belong to the
  container, so the `.zip` and the `.dmg` carry them and the loose binary does not. And
  have it install itself to `%LOCALAPPDATA%\Programs\loafcat` on first run, then *say
  so* — an install nobody was told about looks identical to no install, and the user goes
  on launching the download forever.
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

## Updates — the one place that runs downloaded code

`Updater.swift` / `Updater.cs` check GitHub a few times a day, and this is the only
part of the app that fetches something and arranges for it to run. Treat it as the
security surface it is.

- **A checksum is not provenance.** The `.sha256` sits beside the file it describes,
  on the same host, so it proves the download was not corrupted and nothing else —
  anyone who can replace the release can replace both halves. Every update therefore
  also carries an **ECDSA P-256 signature** over the file.
- **An unsigned or wrongly-signed release is never installed.** The app reports that a
  new version exists and stops. That degradation is deliberate: it is what makes it
  safe to have shipped this before any key existed, and what makes a lost key a
  nuisance rather than an emergency.
- **The public key is compiled into both builds, never read from `assets/`.**
  Everything in `assets/` is meant to be replaceable by whoever owns the machine; a
  trust anchor that can be swapped by editing a file next to the binary is not one.
  `scripts/check-update-key.sh` fails the build if the two ports disagree, or if the
  value is not a key openssl can read — a silent mismatch means every installed copy
  quietly stops updating and nobody finds out for months.
- **P-256, not Ed25519.** .NET 8 has no Ed25519. A scheme both standard libraries can
  verify, against `openssl`-produced signatures, is worth more than the newer curve.
- **Nothing is ever swapped under a running app.** A verified download is *staged*; the
  swap happens at the next launch, before any window exists, by renaming the running
  binary out of the way. Both platforms allow renaming a running image and neither
  allows deleting one. If the move fails after the rename, put the old one back — an
  app that fails to update is a nuisance, one that deletes itself trying is a support
  request with no way to answer it.
- This is **not** Gatekeeper or SmartScreen signing, which cost money and are about the
  first install. This is free and protects the update channel.

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
- All art in `assets/` is generated by `tools/generate_art.py` (the icon by
  `tools/generate_icon.py`, from those same parts). Keep it that way, and
  reject contributions of art with unclear provenance.

## Shipping — what a downloader actually hits

`./tools/make-dmg.sh` builds the disk image. Tag `vX.Y.Z` and push to publish one.

**There is no free way past Gatekeeper.** Notarisation needs a Developer ID, which
needs the $99/year programme. A *free* "Apple Development" certificate is worse
than nothing — Gatekeeper rejects it for distribution, and an untrusted signature
fails harder than an ad-hoc one. So the shipped image is ad-hoc signed and says so
on its own background. `LOAFCAT_SIGN_IDENTITY` and `LOAFCAT_NOTARY_PROFILE` are
already wired through the script and the release workflow for the day that changes.

Measured on macOS 26.3, end to end: quarantine propagates from the image to the
copied app (183 files), `spctl` rejects it, and `xattr -dr com.apple.quarantine`
makes it launch. Control-click-to-open **stopped working in macOS 15** — System
Settings › Privacy & Security › Open Anyway is now the only GUI route, so don't
write the old instructions.

**`install.sh` is the recommended route, and it sidesteps all of that.** The
quarantine flag is attached by whatever *downloads* the file; `curl` attaches
none, so an app it installs is never quarantined and simply opens. Verified —
a curl-fetched file carries `com.apple.provenance` and nothing else. This is not
a bypass: typing an install command is a clearer deliberate choice than clicking
through a warning, which is why Homebrew and rustup work the same way. Keep that
script short and readable; it is the thing users are being asked to trust.

**Finder window chrome is a global user preference, not a per-window one.** The
toolbar, tab bar and status bar flags in a `.DS_Store` are a request Finder is
free to ignore, and each one eats height from the content area. The background is
anchored top-left, so anything low in it is simply absent for that user — which is
why `generate_dmg_background.py` fails the build if content lands below `SAFE_H`.

## Verification before claiming done

- `./build.sh` succeeds AND the app was launched and looked at. A build that
  compiles is not a feature that works.
- **Windows: `dotnet build` succeeds, `loafcat.exe --selftest` passes, and
  `--demo-drag` reports PASS.** The self-test asserts the things a human would
  otherwise be checking by eye — the composed frame is opaque on the cat and alpha-0
  in the margin, 3x is byte-for-byte the 1x frame with every pixel tripled, the hit
  mask is a real silhouette. If you change the compositor and cannot explain why one
  of those moved, you have broken it.
- **A behaviour change touches both ports or it is a bug.** After changing physics or
  tuning, run `--demo-drag` on both and compare the `peaks` line; the two should agree
  to within the run-to-run spread each shows against itself (measured at a few percent
  on the swing, exact on the saturating channels). Check both machines are on the same
  drag feel first — that is what the first comparison got wrong.
- Pixel art: run idle for 60s at 2x and 3x. Any crawling or shimmering pixel means a
  fractional transform leaked in — round to whole *logical* pixels before scaling.
- Click-through: click a transparent area beside the cat; the app underneath must
  get it. "Nothing happens" is the correct outcome and looks identical to a bug.
- Hooks: kill the app, then run a long Claude Code task. It must finish at normal
  speed.
