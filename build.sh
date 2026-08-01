#!/bin/bash
# Builds loafcat into a .app bundle.
#
# Hand-rolled rather than SwiftPM's bundler because the app needs a specific
# Info.plist (LSUIElement) and an ad-hoc signature, and because assets must land in
# Contents/Resources. Command Line Tools is sufficient; full Xcode is not required.
set -euo pipefail
cd "$(dirname "$0")"

APP="build/LoafCat.app"
rm -rf build
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

cat > "$APP/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleExecutable</key><string>LoafCat</string>
  <!-- Locked permanently. Changing it later would invalidate every user's TCC
       grant with no migration path. -->
  <key>CFBundleIdentifier</key><string>dev.loafcat.app</string>
  <key>CFBundleName</key><string>loafcat</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>0.1.0</string>
  <key>LSMinimumSystemVersion</key><string>13.0</string>
  <!-- Accessory app: no Dock icon, no Cmd-Tab. Also required to float over
       fullscreen apps. The menu bar item is therefore the ONLY way to quit. -->
  <key>LSUIElement</key><true/>
</dict>
</plist>
PLIST

# Globbed, not listed: a hardcoded file list means every new module breaks the
# build until someone remembers to add it here. main.swift goes last because it
# holds the top-level code.
SOURCES=$(find Sources/LoafCat -name '*.swift' ! -name 'main.swift' | sort)
swiftc -O \
  -o "$APP/Contents/MacOS/LoafCat" \
  $SOURCES \
  Sources/LoafCat/main.swift 2>&1 | grep -v "^$" || true

if [ ! -f "$APP/Contents/MacOS/LoafCat" ]; then
  echo "BUILD FAILED" >&2
  exit 1
fi

cp -R assets "$APP/Contents/Resources/assets"

# Ad-hoc signature: Apple Silicon SIGKILLs a wholly unsigned binary. A persistent
# self-signed cert comes later -- it gives a stable Designated Requirement so any
# future permission grant survives updates.
codesign --force --sign - "$APP" 2>/dev/null

echo "built $APP"
