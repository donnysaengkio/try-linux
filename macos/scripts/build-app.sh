#!/bin/bash
# Builds "Try Linux.app" (Apple silicon) and a zip of it in build/.
set -euo pipefail
cd "$(dirname "$0")/.."

VERSION=$(sed -nE 's/.*static let version = "([^"]+)".*/\1/p' Sources/TryLinux/Main.swift)
APP="build/Try Linux.app"

swift build -c release --arch arm64
BIN="$(swift build -c release --arch arm64 --show-bin-path)/TryLinux"

rm -rf build
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN" "$APP/Contents/MacOS/TryLinux"
sed "s/__VERSION__/$VERSION/g" Resources/Info.plist > "$APP/Contents/Info.plist"

"$APP/Contents/MacOS/TryLinux" --write-iconset build/AppIcon.iconset
iconutil -c icns build/AppIcon.iconset -o "$APP/Contents/Resources/AppIcon.icns"

# Ad-hoc signature with the virtualization entitlement (no Apple Developer ID).
codesign --force --sign - --entitlements Resources/TryLinux.entitlements "$APP"
codesign --verify --verbose "$APP"

ditto -c -k --keepParent "$APP" "build/TryLinux-$VERSION-macOS.zip"
shasum -a 256 "build/TryLinux-$VERSION-macOS.zip" "$APP/Contents/MacOS/TryLinux"
echo "Built $APP ($VERSION)"
