<div align="center">

<img src="assets/icon/AppIcon.iconset/icon_256x256.png" width="128" alt="loafcat">

# loafcat

**A pixel cat that lives on your desktop, watches your cursor, reacts to your
typing, and knows when Claude Code has finished working.**

Native Swift/AppKit · macOS 13+ · MIT licensed · **asks for zero permissions**

</div>

---

## Install

1. Download **`loafcat-<version>.dmg`** from
   [Releases](https://github.com/aadisaraf/loafcat/releases/latest).
2. Open it and drag loafcat onto Applications.
3. **The first launch will be blocked.** See below — this is expected, and it is
   not a sign that anything is wrong with the download.

### Getting past the first launch

loafcat is not notarised. Notarisation requires Apple's $99/year Developer
Program, and this is a free open-source project, so macOS treats it the way it
treats any app it has not seen before.

**In System Settings:**

1. Double-click loafcat in Applications. macOS refuses and offers **Done**. Click it.
2. Open **System Settings → Privacy & Security** and scroll down.
3. Next to *"loafcat was blocked…"*, click **Open Anyway** and confirm.

**Or in a terminal**, which does the same thing in one line:

```sh
xattr -dr com.apple.quarantine /Applications/LoafCat.app
```

Either way, you only do it once.

> If you'd rather not trust a stranger's binary at all, that's reasonable —
> [build it yourself](#building-from-source). It takes about five seconds and
> needs nothing but the Xcode command line tools.

### Where it is once it's running

loafcat has no Dock icon by default, because a menu bar pet has no business
taking a Dock slot. **Look for the cat's face in the menu bar** — Settings, and
Quit, are in that menu.

If you'd rather reach it like any other app, turn on **Show loafcat in the Dock**
in Settings. Clicking its Dock icon then opens Settings.

---

## What it does

| | |
|---|---|
| **Watches your cursor** | Pupils, eyes, head and body track it on four layers, so the turn has depth instead of the whole cat sliding. |
| **Reacts to typing** | Kneads while you type, and overheats — steam and all — when you type fast. |
| **Picks up and stretches** | Drag it around; it hangs, stretches on a yank, and settles. Three feels, from Subtle to Springy. |
| **Purrs when petted** | Stroke it, and it leans into the cursor. It ignores a cursor that's merely parked on it. |
| **Hunts** | Fast, reversing cursor movement gets a pounce. |
| **Knows about Claude Code** | Thinks while a request runs, hops when it finishes, raises an alert when Claude needs you. Optional, reversible, and it cannot slow a session down. |
| **Looks after you** | Optional stretch breaks, hydration nudges, a pomodoro timer, a daily reminder and a pinned note — all off or conservative by default. |
| **Sleeps** | Goes quiet when you do. |

Three cats ship: **mono**, **cream** and **tuxedo**.

<div align="center">
<img src="assets/themes/mono/preview.png" width="420" alt="the mono cat, light and dark">
</div>

---

## Privacy: it asks for nothing

**No Accessibility. No Input Monitoring. No Screen Recording. No prompts at all.**

Typing reactions come from `CGEventSource.counterForEventType`, which returns an
integer count of key events. Not which keys — *how many*. There is no code path
by which a keycode could reach this app, so being unable to read what you type is
**structural**, not a promise you have to take on trust.

This is enforced mechanically, not by review: `scripts/check-privacy.sh` fails the
build if code appears that would trigger a permission prompt — event taps, global
keyboard monitors, screen capture, or anything that reads key identity. It runs on
every build and in CI.

Most desktop pets use `uiohook` or a `CGEventTap`. Those are *active filters* that
can read and suppress every keystroke system-wide, which is why they trigger the
"control your computer" dialog. loafcat will never have one.

---

## Claude Code integration

Settings → Claude Code → **Connect**. This adds hook entries to
`~/.claude/settings.json` (backing up the previous file first) and copies the hook
script to `~/.loafcat/`. Disconnecting removes only loafcat's entries.

Every hook is **asynchronous**, carries a short explicit timeout, uses a sub-second
network timeout and **exits zero whatever happens** — so it cannot stall a Claude
Code session, even with loafcat quit. Nothing is hooked into message display.

The cat talks to itself over `127.0.0.1` on a random port, behind a bearer token
written to `~/.loafcat/endpoint.json` with mode `0600`. Payloads are never logged;
they contain prompt text and shell commands.

---

## Building from source

Needs the Xcode command line tools. Not full Xcode.

```sh
git clone https://github.com/aadisaraf/loafcat.git
cd loafcat
./build.sh          # -> build/LoafCat.app
open build/LoafCat.app
```

An app you built yourself is not quarantined, so none of the Gatekeeper business
above applies.

To build a disk image, `pip install dmgbuild Pillow` then `./tools/make-dmg.sh`.

---

## The art

**Every pixel in `assets/` is generated by `tools/generate_art.py`** — the cat, the
speech bubbles, the pixel font, the app icon and the disk image background. Nothing
is traced, sampled or derived from any existing sprite, which is what lets this
repository make an airtight provenance claim.

The cat is a **layered rig**, not a sprite sheet: about 16 body parts that the
runtime transforms at 120Hz. Squash-and-stretch, ear twitch, tail sway, head turn
and pupil tracking are all computed from those parts, which is why eighteen
animation states need roughly sixty drawn cells instead of a hundred and fifty
mutually-consistent frames — and why consistency is structural, since every state
reuses the same pixels.

A theme is one self-contained directory under `assets/themes/`. Drop one in and it
appears in Settings with a thumbnail; no code knows anything about a specific cat.

```sh
python3 tools/generate_art.py --theme mono
python3 tools/generate_icon.py
python3 tools/generate_dmg_background.py
```

CI regenerates all of it and fails if the result differs by a byte.

---

## Contributing

Read [CLAUDE.md](CLAUDE.md) first — it is the working agreement, and it records
several things measured on real hardware that contradict what the documentation
says (per-pixel click-through, window levels, which event-source states hang).

Every feature, fix or experiment gets its own branch and a pull request.

---

## Legal

loafcat is an open-source alternative to Comnyang. It reproduces **behaviour**,
which is not copyrightable. It contains none of Comnyang's **art**, which is —
no asset has been opened, fetched, traced or vendored from any source, and all
art here is generated by the pipeline above.

MIT licensed. See [LICENSE](LICENSE).
