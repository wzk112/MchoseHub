#!/bin/bash
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PACKAGE_DIR="${1:-$SCRIPT_DIR/../artifacts/macos}"
if [[ -e "$PACKAGE_DIR" ]]; then
  echo "Output already exists: $PACKAGE_DIR. Choose a new directory." >&2
  exit 1
fi
mkdir -p "$(dirname "$PACKAGE_DIR")"
PACKAGE_DIR="$(cd "$(dirname "$PACKAGE_DIR")" && pwd)/$(basename "$PACKAGE_DIR")"
cd "$SCRIPT_DIR"
swift build -c release -Xswiftc -warnings-as-errors
BIN_DIR="$(swift build -c release --show-bin-path)"
APP_DIR="$PACKAGE_DIR/MCHOSE HUB.app"
mkdir -p "$APP_DIR/Contents/MacOS" "$APP_DIR/Contents/Resources"
cp "$BIN_DIR/MchoseHub" "$APP_DIR/Contents/MacOS/MchoseHub"
cp "$BIN_DIR/mchose" "$PACKAGE_DIR/mchose"
cp "$SCRIPT_DIR/Assets/AppIcon.icns" "$APP_DIR/Contents/Resources/AppIcon.icns"
RESOURCE_BUNDLE="$BIN_DIR/MchoseHubMac_MchoseCore.bundle"
if [[ ! -d "$RESOURCE_BUNDLE" ]]; then
  echo "Missing SwiftPM resource bundle: $RESOURCE_BUNDLE" >&2
  exit 1
fi
ditto "$RESOURCE_BUNDLE" "$APP_DIR/Contents/Resources/MchoseHubMac_MchoseCore.bundle"
ditto "$RESOURCE_BUNDLE" "$PACKAGE_DIR/MchoseHubMac_MchoseCore.bundle"
cat > "$APP_DIR/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleExecutable</key><string>MchoseHub</string>
<key>CFBundleIdentifier</key><string>io.github.wzk112.MchoseHub.mac</string>
<key>CFBundleName</key><string>MCHOSE HUB</string>
<key>CFBundleDisplayName</key><string>MCHOSE HUB</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleIconFile</key><string>AppIcon</string>
<key>CFBundleShortVersionString</key><string>0.1.0</string>
<key>CFBundleVersion</key><string>1</string>
<key>LSMinimumSystemVersion</key><string>14.0</string>
<key>NSHighResolutionCapable</key><true/>
<key>NSPrincipalClass</key><string>NSApplication</string>
</dict></plist>
PLIST
cp "$SCRIPT_DIR/README.md" "$PACKAGE_DIR/README.md"
cp "$SCRIPT_DIR/VALIDATION.md" "$PACKAGE_DIR/VALIDATION.md"
mkdir -p "$PACKAGE_DIR/Assets"
cp "$SCRIPT_DIR/Assets/AppIcon.svg" "$SCRIPT_DIR/Assets/AppIcon-preview.png" "$SCRIPT_DIR/Assets/AppIcon.icns" "$PACKAGE_DIR/Assets/"
if [[ -f "$SCRIPT_DIR/../LICENSE" ]]; then cp "$SCRIPT_DIR/../LICENSE" "$PACKAGE_DIR/LICENSE"; fi
codesign --force --sign - "$PACKAGE_DIR/mchose"
codesign --force --sign - "$APP_DIR"
codesign --verify --strict "$APP_DIR"
echo "Built: $PACKAGE_DIR"
