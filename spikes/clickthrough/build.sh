#!/bin/bash
# Builds the click-through spike into a real .app bundle.
# A loose binary gets different activation/window-level treatment than a bundle,
# and TCC identity is keyed to the bundle, so spikes must be bundled to be honest.
set -euo pipefail

cd "$(dirname "$0")"
APP="build/ClickThroughSpike.app"

rm -rf build
mkdir -p "$APP/Contents/MacOS"

cat > "$APP/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleExecutable</key><string>ClickThroughSpike</string>
  <key>CFBundleIdentifier</key><string>dev.loafcat.spike.clickthrough</string>
  <key>CFBundleName</key><string>ClickThroughSpike</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>0.1</string>
  <key>LSMinimumSystemVersion</key><string>13.0</string>
  <!-- Accessory app: no Dock icon, no Cmd-Tab. Also required to float over fullscreen. -->
  <key>LSUIElement</key><true/>
</dict>
</plist>
PLIST

swiftc -O \
  -o "$APP/Contents/MacOS/ClickThroughSpike" \
  main.swift

# Ad-hoc signature. Apple Silicon SIGKILLs completely unsigned binaries.
codesign --force --sign - "$APP"

echo "built $APP"
echo "run:   $APP/Contents/MacOS/ClickThroughSpike"
