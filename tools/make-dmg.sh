#!/bin/bash
# Builds a distributable disk image.
#
# Run:  ./tools/make-dmg.sh
# Out:  dist/loafcat-<version>.dmg
#
# ---------------------------------------------------------------------------
# About signing, and why the disk image says what it says
# ---------------------------------------------------------------------------
# Getting past Gatekeeper on someone else's Mac needs a Developer ID certificate
# and notarisation, and a Developer ID needs the $99/year Apple Developer Program.
# There is no free path -- a free "Apple Development" certificate is for running
# on your own registered devices and Gatekeeper rejects it for distribution, so
# using one would make the download *worse* (an untrusted certificate fails
# harder than no certificate at all).
#
# So the shipped disk image is ad-hoc signed, and everyone who downloads it has to
# approve it once in System Settings. That is printed on the disk image background
# itself and repeated in the README, because a user who believes a download is
# broken does not go looking for instructions.
#
# The paid path is already wired up. Set both and this script signs and notarises
# with no other change:
#
#   LOAFCAT_SIGN_IDENTITY="Developer ID Application: Your Name (TEAMID)"
#   LOAFCAT_NOTARY_PROFILE=loafcat        # xcrun notarytool store-credentials
#
set -euo pipefail
cd "$(dirname "$0")/.."

VERSION=$(/usr/libexec/PlistBuddy -c "Print CFBundleShortVersionString" \
  build/LoafCat.app/Contents/Info.plist 2>/dev/null || echo "")

echo "==> building the app"
./build.sh
VERSION=$(/usr/libexec/PlistBuddy -c "Print CFBundleShortVersionString" \
  build/LoafCat.app/Contents/Info.plist)

APP="build/LoafCat.app"
DMG="dist/loafcat-${VERSION}.dmg"
mkdir -p dist
rm -f "$DMG"

# ---------------------------------------------------------------------------
# Signing
# ---------------------------------------------------------------------------
if [ -n "${LOAFCAT_SIGN_IDENTITY:-}" ]; then
  echo "==> signing with $LOAFCAT_SIGN_IDENTITY"
  # --options runtime is required for notarisation. --timestamp is too, and it
  # needs network access, which is why it is not on the ad-hoc path.
  codesign --force --deep --options runtime --timestamp \
    --sign "$LOAFCAT_SIGN_IDENTITY" "$APP"
else
  echo "==> ad-hoc signature only (no LOAFCAT_SIGN_IDENTITY set)"
  echo "    Gatekeeper will block this on first launch; the disk image says so."
fi
codesign --verify --strict "$APP"

# ---------------------------------------------------------------------------
# Art
# ---------------------------------------------------------------------------
echo "==> generating disk image art"
python3 tools/generate_dmg_background.py

STAGE=$(mktemp -d)
trap 'rm -rf "$STAGE"' EXIT

# ---------------------------------------------------------------------------
# The image itself
# ---------------------------------------------------------------------------
# dmgbuild rather than hdiutil + AppleScript: it writes the .DS_Store that
# carries the window size, icon positions and background directly, with no Finder
# involved. The AppleScript recipe every other project uses needs Automation
# permission, prompts the user, and cannot run in CI at all.
echo "==> building $DMG"
SETTINGS="$STAGE/settings.py"
cat > "$SETTINGS" <<PY
import os
app = os.path.abspath("build/LoafCat.app")
files = [app]
symlinks = {"Applications": "/Applications"}
badge_icon = os.path.abspath("assets/icon/AppIcon.icns")
# Two icons and nothing else. A third file has nowhere safe to sit: Finder
# window chrome is a global preference, so the bottom of the window may or may
# not exist, and an icon that is sometimes invisible is worse than no icon.
icon_locations = {
    "LoafCat.app": (150, 190),
    "Applications": (410, 190),
}
background = os.path.abspath("assets/dmg/background.tiff")
window_rect = ((200, 200), (560, 460))
icon_size = 128
text_size = 13
default_view = "icon-view"
show_status_bar = False
show_tab_view = False
show_toolbar = False
show_pathbar = False
show_sidebar = False
format = "UDZO"
PY
python3 -m dmgbuild -s "$SETTINGS" -D app="$PWD/$APP" "loafcat" "$DMG"

# ---------------------------------------------------------------------------
# Notarisation, when there is a certificate to notarise with
# ---------------------------------------------------------------------------
if [ -n "${LOAFCAT_NOTARY_PROFILE:-}" ]; then
  echo "==> notarising"
  xcrun notarytool submit "$DMG" --keychain-profile "$LOAFCAT_NOTARY_PROFILE" --wait
  # Stapling puts the ticket inside the image, so a first launch works offline.
  xcrun stapler staple "$DMG"
  xcrun stapler validate "$DMG"
fi

SIZE=$(du -h "$DMG" | cut -f1)
echo
echo "built $DMG ($SIZE)"
spctl -a -vv "$APP" 2>&1 | sed 's/^/  gatekeeper: /' || true
