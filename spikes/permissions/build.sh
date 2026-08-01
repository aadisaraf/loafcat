#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"
APP="build/PermissionSpike.app"
rm -rf build; mkdir -p "$APP/Contents/MacOS"
cat > "$APP/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleExecutable</key><string>PermissionSpike</string>
  <key>CFBundleIdentifier</key><string>dev.loafcat.spike.permissions</string>
  <key>CFBundleName</key><string>PermissionSpike</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>LSUIElement</key><true/>
</dict>
</plist>
PLIST
swiftc -O -o "$APP/Contents/MacOS/PermissionSpike" main.swift
codesign --force --sign - "$APP"
echo "built $APP"
