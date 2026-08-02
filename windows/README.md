# loafcat for Windows

The same cat, the same art, the same behaviour — a native Windows app rather than a
port of the Mac binary.

```powershell
irm https://raw.githubusercontent.com/aadisaraf/loafcat/main/install.ps1 | iex
```

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

A keystroke is then *inferred*: the last-input tick advanced, and it is not the tick the
mouse fired on, therefore the input was not the mouse. The app learns that *a* key was
pressed and when. It cannot learn which, because neither API it called was ever told.

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

### Smaller things

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

**`LoafCat.exe --selftest`** runs on every CI build and checks, for all three themes:

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
- an idle cat re-presents an identical frame more than half the time

**`LoafCat.exe --demo-drag`** runs the scripted grab-hold-shake-release through the same
entry points real mouse events use, and fails the build if the cat has not come
completely to rest three seconds after release. This is the check that the ported
springs and pendulum behave like the Swift original rather than merely compiling.

### Still needing a human at a Windows machine

- Whether it *looks* right: run idle for 60s at 2× and 3× and watch for a crawling or
  shimmering pixel. `--selftest` proves the magnification is exact, which is the usual
  cause, but it cannot prove the result is pleasant.
- Click-through in practice: click a transparent area beside the cat and confirm the app
  underneath gets it. "Nothing happens" is the correct outcome and looks identical to a
  bug.
- The tray icon against a light taskbar, a dark taskbar, and a wallpapered one.
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
