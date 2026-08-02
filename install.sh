#!/bin/bash
#
# loafcat installer.
#
#   curl -fsSL https://raw.githubusercontent.com/aadisaraf/loafcat/main/install.sh | bash
#
# ---------------------------------------------------------------------------
# Why this exists, and why it is not a worse idea than the disk image
# ---------------------------------------------------------------------------
# macOS attaches com.apple.quarantine to downloads, and the app that downloads
# the file is what attaches it -- browsers do, curl does not. So an app installed
# by this script is never quarantined in the first place, and simply opens. No
# blocked dialog, no trip through System Settings.
#
# That is not a trick and it is not a bypass. Gatekeeper's question is "did a
# human deliberately choose to run this", and typing an install command is a
# clearer yes than clicking through a warning. It is the same reason Homebrew,
# rustup and every other command line installer behave this way.
#
# What you are trusting is this file. It is short on purpose. Read it first if
# you like -- that is the whole point of it being one readable script:
#
#   curl -fsSL .../install.sh -o install.sh && less install.sh && bash install.sh
#
# ---------------------------------------------------------------------------
set -euo pipefail

REPO="aadisaraf/loafcat"
APP_NAME="LoafCat.app"

# Overridable for testing and for pinning:
#   LOAFCAT_VERSION=v0.1.0   install a specific release
#   LOAFCAT_DMG=./x.dmg      install a local image instead of downloading
VERSION="${LOAFCAT_VERSION:-}"
LOCAL_DMG="${LOAFCAT_DMG:-}"

bold=$(tput bold 2>/dev/null || true)
dim=$(tput dim 2>/dev/null || true)
reset=$(tput sgr0 2>/dev/null || true)
say()  { echo "${bold}==>${reset} $*"; }
note() { echo "    ${dim}$*${reset}"; }
die()  { echo "${bold}error:${reset} $*" >&2; exit 1; }

# --------------------------------------------------------------------------
# 0. Is this even a Mac loafcat runs on
# --------------------------------------------------------------------------
[ "$(uname -s)" = "Darwin" ] || die "loafcat is macOS only."
MAJOR=$(sw_vers -productVersion | cut -d. -f1)
[ "$MAJOR" -ge 13 ] || die "loafcat needs macOS 13 or later (this is $(sw_vers -productVersion))."

case "$(uname -m)" in
  arm64|x86_64) ;;
  *) die "unexpected architecture $(uname -m)." ;;
esac

if [ "${1:-}" = "--uninstall" ]; then
  say "removing loafcat"
  if [ -f "$HOME/.claude/settings.json" ] && grep -q "loafcat" "$HOME/.claude/settings.json" 2>/dev/null; then
    echo
    echo "${bold}Claude Code is still connected.${reset}"
    echo "Open loafcat first and use Settings > Claude Code > Disconnect, so its"
    echo "hook entries come out of ~/.claude/settings.json cleanly. Nothing here"
    echo "will edit that file for you."
    echo
  fi
  pkill -f "$APP_NAME/Contents/MacOS/LoafCat" 2>/dev/null || true
  rm -rf "/Applications/$APP_NAME" "$HOME/Applications/$APP_NAME"
  rm -f "$HOME/.loafcat/endpoint.json"
  # Settings survive by default. Most people who uninstall an app they liked
  # enough to install by hand are about to reinstall it, and silently throwing
  # away their theme and their timers is a rude way to be helpful.
  if [ "${2:-}" = "--purge" ]; then
    defaults delete dev.loafcat.app 2>/dev/null || true
    rm -rf "$HOME/.loafcat"
    say "removed, along with its settings."
  else
    say "removed. Settings kept -- add --purge to delete those too."
  fi
  exit 0
fi

# --------------------------------------------------------------------------
# 1. Find the image
# --------------------------------------------------------------------------
TMP=$(mktemp -d)
trap 'hdiutil detach "$MOUNT" -quiet 2>/dev/null || true; rm -rf "$TMP"' EXIT
MOUNT=""

if [ -n "$LOCAL_DMG" ]; then
  say "using $LOCAL_DMG"
  DMG="$LOCAL_DMG"
else
  if [ -n "$VERSION" ]; then
    API="https://api.github.com/repos/$REPO/releases/tags/$VERSION"
  else
    API="https://api.github.com/repos/$REPO/releases/latest"
  fi
  say "looking up the ${VERSION:-latest} release"
  # No jq: it is not on a stock Mac, and needing a package manager to install a
  # thing that avoids needing a package manager would be silly.
  RELEASE=$(curl -fsSL "$API") \
    || die "could not reach GitHub. Check your connection, or download the disk image from https://github.com/$REPO/releases"
  URL=$(printf '%s' "$RELEASE" \
    | grep -o '"browser_download_url"[^,]*\.dmg"' \
    | head -1 | sed 's/.*"\(https[^"]*\)"/\1/')
  [ -n "$URL" ] || die "that release has no disk image attached."
  TAG=$(printf '%s' "$RELEASE" | grep -o '"tag_name": *"[^"]*"' | head -1 | sed 's/.*"\([^"]*\)"$/\1/')

  say "downloading loafcat ${TAG}"
  DMG="$TMP/loafcat.dmg"
  curl -fsSL --progress-bar -o "$DMG" "$URL"

  # Checksums are published next to the image. Verified when present rather than
  # required, so an older release without one still installs.
  SUMS_URL=$(printf '%s' "$RELEASE" \
    | grep -o '"browser_download_url"[^,]*\.sha256"' \
    | head -1 | sed 's/.*"\(https[^"]*\)"/\1/')
  if [ -n "$SUMS_URL" ]; then
    EXPECTED=$(curl -fsSL "$SUMS_URL" | awk '{print $1}' | head -1)
    ACTUAL=$(shasum -a 256 "$DMG" | awk '{print $1}')
    if [ "$EXPECTED" != "$ACTUAL" ]; then
      die "checksum mismatch. Expected $EXPECTED, got $ACTUAL. Not installing."
    fi
    note "sha256 verified"
  fi
fi

# --------------------------------------------------------------------------
# 2. Install it
# --------------------------------------------------------------------------
# /Applications when it is writable, otherwise the per-user one. Never sudo: an
# installer that asks for your password to copy one bundle has not earned it.
DEST="/Applications"
if [ ! -w "$DEST" ]; then
  DEST="$HOME/Applications"
  mkdir -p "$DEST"
  note "/Applications is not writable, using $DEST"
fi

say "mounting"
MOUNT=$(hdiutil attach -nobrowse -readonly -noverify "$DMG" \
  | grep -o '/Volumes/.*' | head -1)
[ -n "$MOUNT" ] || die "could not mount the disk image."
[ -d "$MOUNT/$APP_NAME" ] || die "$APP_NAME is not in that disk image."

if pgrep -f "$APP_NAME/Contents/MacOS/LoafCat" >/dev/null 2>&1; then
  say "quitting the running copy"
  pkill -f "$APP_NAME/Contents/MacOS/LoafCat" || true
  sleep 1
fi

say "installing to $DEST"
rm -rf "$DEST/$APP_NAME"
# ditto, not cp: it preserves the bundle's extended attributes and resource
# forks, and a copy that loses them can invalidate the code signature.
ditto "$MOUNT/$APP_NAME" "$DEST/$APP_NAME"
hdiutil detach "$MOUNT" -quiet
MOUNT=""

# curl does not set com.apple.quarantine, so there should be nothing to strip.
# Done anyway, because "should be" is not a thing to leave a first launch to.
xattr -dr com.apple.quarantine "$DEST/$APP_NAME" 2>/dev/null || true

say "starting loafcat"
open "$DEST/$APP_NAME"

cat <<TXT

${bold}loafcat is installed and running.${reset}

  Where       $DEST/$APP_NAME
  Find it     in the menu bar, as a cat's face -- settings and quit are there
  Turn it off menu bar cat, or Settings; opening the app again turns it back on

It asked for no permissions, and it never will. Source and issues:
https://github.com/$REPO

To remove it:  curl -fsSL https://raw.githubusercontent.com/$REPO/main/install.sh | bash -s -- --uninstall
TXT
