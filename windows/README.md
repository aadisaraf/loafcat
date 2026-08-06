# loafcat for Windows

The same cat, the same art, the same behaviour — a native Windows app rather than a
port of the Mac binary.

```powershell
irm https://raw.githubusercontent.com/aadisaraf/loafcat/main/install.ps1 | iex
```

Or download **`loafcat.exe`** from
[Releases](https://github.com/aadisaraf/loafcat/releases) and double-click it. One
file, nothing to extract: the art and the hook script are embedded and unpack
themselves on first run.

On that first run it installs itself, in a window that tells you it is doing it — to
`%LOCALAPPDATA%\Programs\loafcat`, with a Start menu entry, which is exactly where and
what `install.ps1` would have put it — and then offers to open what it installed.
Otherwise the app you end up with is a file sitting in Downloads, and a second copy of
it called `loafcat (1).exe` the next time you update by hand. Both download routes
converge on the same layout, and `install.ps1 -Uninstall` removes either. Pass
`--portable` to skip it.

Running a **newer** `loafcat.exe` while loafcat is on is the manual update route, and it
works: the download asks the running copy to stand down, replaces it, and starts it
again. Running the **same** version tells you so and changes nothing. Running an
**older** one offers the downgrade rather than performing it, which is what you want on
the day an update turns out to be bad.

The published executable is named `loafcat.exe`, with no version and no architecture in
it: the *container* carries those, and a bare executable is not a container. The `.dmg`
is `loafcat-<version>.dmg` and holds `LoafCat.app`; the `.zip` is
`loafcat-<version>-win-x64.zip` and holds `loafcat.exe`. Naming the loose binary after
the release is how it ends up introducing itself as `loafcat-0.2.0-win-x64` in every
list that has nothing better to call it.

The `.zip` is the same executable with `assets\` on disk beside it. Anything found
there wins over the embedded copy, which is what makes dropping a community theme into
`assets\themes\` work — so take the zip if you want to edit the art, and the `.exe`
otherwise. The zip never self-installs: someone who unpacked it chose that folder, and
lifting one file out of it would leave the rest behind.

Windows 10 1809 or later, x64. No runtime to install, no permissions, no elevation.

---

## What "the same" actually means

`assets/` is shared verbatim. Both builds load the same `cat.json` and the same part
PNGs, and neither has a single pixel offset or behaviour constant compiled into it —
that was already the rule (`CLAUDE.md`, architecture rule 1), written down long before
there was a second platform, and this is the thing it was written down for.

The file layout mirrors the Swift source one-for-one, so a change to a feature can be
mirrored by reading one file beside its counterpart:

| macOS | Windows |
|---|---|
| `Sources/LoafCat/Atlas.swift` | `windows/LoafCat/Atlas.cs` |
| `Sources/LoafCat/Rig.swift` | `windows/LoafCat/Rig.cs` |
| `Sources/LoafCat/CatView.swift` | `windows/LoafCat/CatView.cs` |
| `Sources/LoafCat/main.swift` | `windows/LoafCat/Program.cs` + `CatWindow.cs` |
| `Sources/LoafCat/Modules/*.swift` | `windows/LoafCat/Modules/*.cs` |
| — | `windows/LoafCat/Interop/*.cs` (no macOS counterpart) |

Every module ported: typing, hunting, petting, scrolling, dragging, the speech bubble,
hydration, pomodoro, stretch breaks, scheduled reminders, pinned notes, and the Claude
Code integration.

---

## Where the two platforms genuinely differ, and why

These are not shortcuts. Each one is a place where doing the same thing would have been
worse.

### Click-through is free here, and better

`spikes/RESULTS.md` records the macOS finding: there is no free per-pixel click-through
on macOS. A transparent `NSWindow` takes every click regardless of alpha, overriding
`hitTest` makes the event reach *nothing*, and the only thing that works is polling the
cursor at 120Hz, sampling a **dilated** alpha mask, and toggling `ignoresMouseEvents` on
boolean transitions. Measured at 97% against 88% for the event-driven version.

None of that applies on Windows. A layered window presented with `UpdateLayeredWindow`
is hit-tested by the window manager against the alpha channel it was just handed —
synchronously, before the click is delivered to anyone. Transparent pixels fall through
for free and there is no race to lose a click to.

So this port deliberately **does not** reproduce the polling toggle, and the 6px
dilation does not apply to clicks. The dilated mask survives as the definition of
`CursorOnCat`, which is a different question — proximity, for petting and for noticing
an alert — and wants to stay generous on both platforms.

### Keystroke counts have to be inferred rather than read

macOS hands over `CGEventSource.counterForEventType(.combinedSessionState, .keyDown)`:
an integer count of key events, per type, permission-free. Windows has no equivalent, so
the same guarantee is rebuilt from two APIs that are each individually incapable of
leaking anything:

1. **`GetLastInputInfo`** returns a struct whose entire payload is a `DWORD` tick count
   of the last input event of any kind. It cannot report what the input was, let alone
   which key.
2. **A low-level *mouse* hook** counts mouse events and records when the last one
   happened. Its callback receives a `MSLLHOOKSTRUCT` — cursor position, wheel delta,
   timestamp, flags. There is no field in that structure that could carry a keystroke.

A keystroke is then *inferred*: the last-input tick advanced, and no mouse event accounts
for it, therefore the input was not the mouse. The app learns that *a* key was pressed and
when. It cannot learn which, because neither API it called was ever told.

**That comparison is where this went wrong once, and it is worth reading twice.** The
first version timestamped each mouse event by calling `GetTickCount()` inside the hook
callback — which times the *callback*, not the *event*, and so never equals what
`GetLastInputInfo` reports for that same event. It also compared the two as unsigned, so
a mouse event that appeared to be a millisecond in the future produced a gap of four
billion. Replaying five seconds of a 125Hz mouse through that logic yields **319 phantom
keystrokes, about 64 a second** — against an `overheat.kps_min` of 4 and a `kps_max` of
14. The cat went to full steam while its owner did nothing but move the cursor.

Two things fix it, and both are in `KeyInference.cs`:

1. **Read the event's own timestamp**, `MSLLHOOKSTRUCT.time`, which is the same quantity
   `GetLastInputInfo` reports for that event.
2. **Do not rule immediately.** `GetLastInputInfo` is updated by the raw input thread the
   instant an event lands; the hook is a callback dispatched to a different thread
   afterwards. A poll routinely sees the tick of a mouse event *before* the hook has
   recorded it. Verdicts are held 50ms, which is far longer than the hook needs and far
   shorter than anything the cat reacts to.

A second, independent test asks whether the hook fired recently on *our own* monotonic
clock, comparing it only against itself. That one assumes nothing about two Win32 clocks
agreeing, so a moving mouse still cannot be read as typing even if the first test is wrong
on hardware nobody here has. Both tests point the same way — towards blaming the mouse —
because under-counting costs a moment of kneading nobody notices, and over-counting
reddens a cat whose owner is not typing.

The cost is that typing *while actively moving the mouse* is not seen. That is a real
thing to be unable to distinguish rather than an approximation of one, and the honest
answer to it is the same as everywhere else here: the fix would be a keyboard hook.

**Then it went wrong a second time, in the opposite direction, from the same mistake.**
Clearing the mouse of a tick had become accurate; what had not been questioned was the
step after it. "Not the mouse, therefore a key" assumes the machine has two input
devices. It does not. `GetLastInputInfo` is reset by *any* raw input the session
receives, and a great deal of that never becomes a mouse message at all — a finger
resting on a precision touchpad reports at ~125Hz whether or not it moves the cursor, a
hand resting on a high-polling-rate mouse reports with nothing to say, a controller left
plugged in reports forever, a pen reports while it hovers. The hook sees none of them.
Reported from a real machine as a cat that **heated up while the cursor was held still
and cooled down as soon as the mouse moved** — because moving it filled the ring with
events that explained the ticks away.

So the inference is no longer "not the mouse, therefore a key" but "not the mouse, *and*
shaped like something a person did". Two shape tests, both about timing, which is all
this code is ever told:

3. **Isolation.** A keystroke has nothing else within 25ms either side of it. A device
   reporting on its own schedule always has a neighbour one `GetTickCount` step away —
   15.6ms, or 8.3ms on a machine where something has raised the timer resolution to 1ms,
   which Chrome and most games do. Checked in *both* directions, which is only possible
   because the 50ms deferral above already holds the verdict longer than the gap: by the
   time anything is ruled on, its successor has arrived and can be looked at. That
   deferral is load-bearing twice over.
4. **A sustained-rate backstop**, for a stream slow enough to pass the gap test: nothing
   above 22 keys a second across a full second is a person. `overheat.kps_max` is 14, so
   the whole of real typing — including the part that is meant to redden the cat — is
   below it.

Measured, five seconds each:

| an idle device reporting every | unfiltered | now |
|---|---|---|
| 8.4ms (a 1ms system timer) | 594 | **0** |
| 15.6ms (the `GetTickCount` grid) | 319 | **0** |
| 20ms | 249 | **0** |
| 33ms | 151 | 22, then written off |

…against jittered typing from 3 to 18 keys a second at ±15% and ±30% wander, 40 seeds
each: worst case **one keystroke lost in a hundred**.

A third test was written and thrown away. Between roughly 3 and 22 reports a second an
idle device is inside human range *and* spaced too far apart to trip the gap test, so
neither test above reaches it. Evenness looks like the answer — a clock repeats its
interval exactly and hands never do — but the stream has already been through an 8.3ms
poll, and that quantisation destroys the jitter the test depends on: at 18 keys a second
with a generous ±15% wander, real typing lands in one or two poll bins and reads as
perfectly even. It discarded **45 of 159 genuine keystrokes**. Two window lengths, same
answer. That band is therefore left open on purpose, and nothing is known to sit in it:
every device that reports while idle runs at 60Hz or faster, and anything above 64Hz
collapses onto the `GetTickCount` grid, which the gap test closes outright.

When a stream is being written off, the log says so, once per episode. A cat that
overheats while nobody types is now two different bug reports, and that line is what
tells them apart.

Without the mouse hook there is nothing to tell a keystroke apart *from*, so the app
infers nothing at all rather than treating every mouse move as typing.

That is strictly **less** information than the macOS build gets, which distinguishes key
events from scroll events at the source. `scripts/check-privacy.sh` blocks
`WH_KEYBOARD_LL`, `GetAsyncKeyState`, `GetKeyState`, `GetKeyboardState`, `ToUnicode`,
`MapVirtualKey`, raw input, journal hooks, `SendInput`, `GetWindowText` and screen
capture at build time, on both platforms.

The one thing this costs: `GetLastInputInfo` does not report input aimed at *elevated*
windows when we are not elevated. Type into an administrator console and the cat will
not knead. That is the correct trade — the fix would be elevating, which is exactly the
prompt this whole design exists to avoid.

### DPI, and why the cat does not scale itself

The app declares per-monitor DPI awareness v2, so Windows never bitmap-stretches the
window. One unit drawn is one physical pixel, and the integer render scale in Settings
is the only magnification in the chain. Without this a 150% display would resample the
cat by 1.5× with bilinear filtering — precisely the mush the art pipeline exists to
prevent.

The cost is that the cat does not automatically get bigger on a high-DPI display, so
first run picks a starting scale from the monitor's DPI (2× at 100%, 3× at 150%) and
your choice in Settings is authoritative after that.

### Getting out of the way is cheaper here, and inverts one rule

macOS has to enumerate every window on screen — eighty-odd dictionaries — to ask whether
one covers the display, so it does that on a background queue at 4Hz. Here the
foreground window answers the same question in three calls, so `PeekModule` polls it
inline and there is nothing to get off the tick. The identical `FullscreenWatch` shape
is kept on both sides anyway, so the two files still read as translations.

- **Compare a candidate full-screen window against `rcMonitor`, not `rcWork`.** This is
  the one place the usual advice inverts, and it is worth stating loudly next to all the
  entries above that say the opposite. Real full screen covers the taskbar; a merely
  maximised window does not. That single difference is the entire reason a maximised
  terminal is not mistaken for a film. Everywhere the cat is *placed* it is still
  `rcWork`.
- **`CallNtPowerInformation(SystemExecutionState)` needs no privilege; `powercfg
  /requests` needs admin.** They answer the same question — is anything holding the
  display awake — and only one of them can be asked by a desktop pet. `ES_DISPLAY_REQUIRED`
  is the flag, and it is the counterpart of macOS's `PreventUserIdleDisplaySleep`.
- **`ClampIntoView` can silently undo a park.** It drags a fully-off-display window back
  on a monitor change, which is exactly right for a cat stranded on an unplugged second
  screen and exactly wrong for one deliberately parked half off the side. It only fires
  when the intersection is *zero*, so a park that leaves any cat on screen is safe — and
  `--demo-peek` asserts that rather than trusting it, because the failure would only
  ever show up on a machine with two monitors.
- **The peek pose is different ART, not different offsets, and the compositor has to
  know it.** `cat.json` carries a `poses` block; while one is active the software
  compositor draws that pose's parts and *nothing else*, through the same
  `CatView.OutOfPose` the plan is asserted against. Both ways this fails are invisible
  in a build log — leave the standing cat on and you get a body behind a peeking head,
  hide one part too many and the window goes empty — so `--selftest` counts opaque
  pixels on the composed surface for every pose: it must draw something, and it must
  draw *less* than the standing cat. A pose that is not smaller is the standing cat
  still being drawn underneath it. The two facings are mirrored **by the generator**;
  do not add a runtime flip, or the ports gain one more thing to disagree about.
- **The indicator uses `Form.Opacity`, not `UpdateLayeredWindow`.** The cat needs
  per-pixel alpha because it is a silhouette; the snap capsule is one flat shape at one
  uniform alpha, and `SetLayeredWindowAttributes` — which is all `Opacity` is — is the
  whole of what that requires. `WS_EX_TRANSPARENT` makes it click-through outright, so
  it never goes near the hit-testing question at all.

### Smaller things

- **The app has to name itself.** A `.app` bundle carries its own name and gets dragged
  to Applications; one loose `.exe` has neither, so Windows falls back to the file name —
  version, CPU architecture and extension included — wherever it has to call the app
  something. `AssemblyTitle` sets `FileDescription`, which is the string Task Manager,
  the startup apps list and the tray icon settings all read, and `SelfInstall.cs` deals
  with the file itself.
- **An install that a running copy can veto is an install that never happens.** Windows
  will not let a running executable be replaced, and the cat is *always* running — that
  is the entire proposition of a desktop pet. So the one moment installing matters, the
  copy on disk is locked. Treating that as "somebody else is here, stand down" reads as
  correct single-instance behaviour and is in fact the bug: a downloaded newer build
  hits the locked file, gives up, installs nothing, says nothing, and brings the *old*
  cat to the front, which is indistinguishable from the download being broken. The
  running copy has to be asked to quit (`Local\dev.loafcat.quit`, so it exits through
  `Application.Exit` and takes its tray icon with it) and ended if it will not.
- **A tray balloon is not a confirmation.** `ShowBalloonTip` is the one piece of Windows
  UI the system may silently discard — Focus Assist, Do Not Disturb and notifications-off
  each drop it with no error and no fallback. It was the whole of what said an install
  had happened, so success and silent refusal looked identical from the outside. Anything
  a user needs to have seen goes in a window.
- **The tray icon is not a template.** macOS takes an alpha mask and tints it to match
  the menu bar. Windows draws the icon as-is on a taskbar that may be light, dark, or
  showing the wallpaper, so the generator emits a real two-tone icon whose mid-grey coat
  holds contrast against all three.
- **The compositor is software.** There is no Core Animation equivalent that also gives
  per-pixel window alpha, so `CatView.cs` blits the frame itself. That turned out to be
  the better trade rather than a compromise: an explicit nearest-neighbour inverse map
  has no interpolation mode to get wrong, and the cat covers ~20k device pixels at 3×.
  Identical frames are dropped before they reach the window manager, which is most
  frames — every position is quantised to a whole logical pixel, so the ambient
  breathing and tail sway spend most of their time producing the same picture twice.
- **Settings key order is preserved.** The macOS build sorts the keys when it writes
  `~/.claude/settings.json`, which silently rearranges a file the user edits by hand.
  This one does not, and it picks a JSON encoder that leaves `\` and `/` in paths alone
  rather than escaping and then un-escaping them.
- **The hook script is PowerShell**, because a `.sh` will not run natively. Same
  contract: `exit 0` unconditionally, sub-second network timeouts, nothing on stdout,
  and a silent no-op when loafcat is not running.
- **Two dead functions were not ported.** `MessageModule.promptForReminder` and
  `promptForNote` are `NSAlert` dialogs left over from when the menu bar owned those
  settings; nothing calls them on macOS any more.

---

## Verification, and what is still unverified

The macOS rule is "`./build.sh` succeeds AND the app was launched and looked at". This
port was written on a Mac, by someone with no Windows machine, so the second half could
not be done — and rather than quietly dropping it, the properties a human would have
been checking are asserted mechanically instead.

**`loafcat.exe --selftest`** runs on every CI build and checks, for all three themes:

- the atlas loads from the same files the macOS build reads
- every part named in the draw order exists
- the speech bubble assembles
- the hit mask is a silhouette — neither empty nor the whole canvas
- 240 frames of ambient motion compose without throwing
- the composed frame is **opaque on the cat and alpha-0 in the margin**, which is
  literally what the window manager hit-tests for click-through
- **3× is byte-for-byte the 1× frame with every pixel tripled** — the pixel-art claim
  stated as an equation. Any interpolation, half-pixel offset or fractional transform
  that leaked into the compositor would break it.
- **a moving mouse is never read as typing.** Five seconds of a 125Hz mouse against the
  120Hz tick, with the hook running a poll late throughout and the two Win32 clocks
  disagreeing by 40ms, must produce zero inferred keystrokes — and typing on a still
  mouse must still be counted exactly. Nobody can move a mouse on a CI runner, so it is
  replayed. The logic is deliberately split out of the P/Invoke so that it can be.
- the byte offset the hook reads `MSLLHOOKSTRUCT.time` from still matches the struct
- an unchanged frame is recognised as unchanged and never sent to the compositor twice.
  How often that happens for a genuinely idle cat is *reported* rather than asserted —
  measured at **97%** (581 of 600 frames), but that depends on where the breathing sine
  sits relative to the pixel grid and is not a number worth failing a build over.

**`loafcat.exe --demo-drag`** runs the scripted grab-hold-shake-release through the same
entry points real mouse events use, and fails the build if the cat has not come
completely to rest three seconds after release.

**`loafcat.exe --install-unattended`** does what a double-click does, with no window in
front of it. CI drives it to prove the three outcomes a download can have — installing
on a clean machine, replacing a copy that is *running*, and correctly doing nothing when
the same version is already there — by building a second loafcat at `0.0.9` to update
from. The window is the only part that differs, because a runner cannot press a button;
the plan, the replacement and the Start menu entry underneath are the code a person's
double-click runs.

Both builds print their peak values at the end, which is what makes the physics
comparable across the port. The line also carries `quietMs` — how long after release the
stretch is still visibly moving. That one exists because the stretch tempo presets scale
*rates* while every other number on the line is an *amplitude*, so without it all four
presets produce an identical peaks line and the comparison could not fail. Measured, same drag feel (`normal`, which was the default when this was taken; it is now
`subtle`, so a fresh run of either build reads `+1.7500` and `22.75` instead), macOS run three times
against one Windows CI run:

| | peak stretch | landing | swing | hang px | squash | lean px |
|---|---|---|---|---|---|---|
| macOS #1 | +2.4150 | −1.2315 | 22.348° | 31.39 | 0.3842 | 5.32 |
| macOS #2 | +2.4150 | −1.2286 | 21.817° | 31.39 | 0.3857 | 5.20 |
| macOS #3 | +2.4150 | −1.2269 | 23.206° | 31.39 | 0.3866 | 5.52 |
| **Windows** | **+2.4150** | **−1.2296** | **22.616°** | **31.39** | **0.3852** | **5.38** |

Peak stretch and hang are identical to four decimal places — both saturate at the same
atlas-defined ceiling. Everything else lands *inside* the spread macOS shows against
itself across three runs of the same binary, which is the strongest statement available
here: the ported springs differ from the original by less than the original differs
from itself.

(The swing wobbles run to run on both platforms because the pendulum is driven by the
*acceleration* of a smoothed velocity, which is sensitive to frame-timing jitter. That
is a property of the design, not of the port.)

The first comparison found a 1.38× discrepancy on every stretch-derived value. It was
not a port bug — the Mac had `dragFeel = subtle` stored from an earlier session, and
1.38 is exactly the ratio between that preset and `normal`. Worth recording because it
is the failure mode this comparison will keep having: check both machines are on the
same drag feel before believing a difference.

### Still needing a human at a Windows machine

- Whether it *looks* right: run idle for 60s at 2× and 3× and watch for a crawling or
  shimmering pixel. `--selftest` proves the magnification is exact, which is the usual
  cause, but it cannot prove the result is pleasant.
- Click-through in practice: click a transparent area beside the cat and confirm the app
  underneath gets it. "Nothing happens" is the correct outcome and looks identical to a
  bug.
- The tray icon against a light taskbar, a dark taskbar, and a wallpapered one.
- **How the snap indicator looks.** `--demo-peek` proves the gesture arms only on a
  dwell and that the cat parks where it claims, but a capsule three logical pixels wide
  with hard-edged rounded ends has never been seen by anyone. Check it reads as an
  affordance against a bright video and a dark one, and that it does not shimmer as the
  cat's Y moves under it.
- **That a parked cat is still grabbable.** Only `reveal_px` of it is on screen by
  design; confirm that is enough to get hold of at 2× and at 4×.
- **That the side-on peek pose reads as a cat** against a real video, at 2× and 4×, on
  both edges. `--selftest` can prove the right sprites are drawn and `--demo-peek` can
  prove the head lands where claimed, but neither can see that it looks like an animal
  looking round a corner — and three earlier versions of this pose passed every check
  they had while looking wrong.
- That the hook does not slow a real Claude Code session — kill the app, run a long
  task, confirm normal speed.
- Behaviour on a mixed-DPI multi-monitor setup, and after unplugging a monitor the cat
  was living on. There is a `ClampIntoView` for the second case; it has never run on
  real hardware.
- Whether SmartScreen behaves as documented for both install routes. The `curl`/browser
  asymmetry on macOS was *measured* (0 vs 183 quarantined files); the Windows equivalent
  is documented behaviour that has not been observed here.

If you hit any of these, an issue with a screenshot is genuinely useful.

---

## Building it

```powershell
pwsh windows\build.ps1
```

Produces `dist\loafcat-<version>-win-x64.zip`. Works on macOS and Linux too — the
project sets `EnableWindowsTargeting`, which is how this port was developed and
reviewed at all.

The single-file build is self-contained, so there is no .NET runtime to install. That
costs about 65MB; "download and run" being literally true is worth more than the
megabytes for an app distributed outside any store.

## SmartScreen

loafcat is not code-signed. An Authenticode certificate costs a few hundred a year, and
SmartScreen additionally wants download reputation that a certificate does not buy on
day one.

`install.ps1` sidesteps it the same way `install.sh` sidesteps Gatekeeper, and for the
same reason: Windows attaches its "mark of the web" based on *what downloaded the file*.
Browsers do; `Invoke-WebRequest` does not. An app installed by the script simply opens.

Downloading the `.zip` from a browser will show **"Windows protected your PC"** on first
launch. Nothing is wrong with the download — click **More info → Run anyway**, or right-
click the zip → **Properties → Unblock** before extracting.
