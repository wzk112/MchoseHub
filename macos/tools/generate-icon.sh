#!/bin/bash
# SVG is the editable source. Ordinary app builds use the checked-in ICNS.
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ASSETS_DIR="$SCRIPT_DIR/../Assets"
if ! command -v rsvg-convert >/dev/null 2>&1; then
  echo "rsvg-convert is required to regenerate icons: brew install librsvg" >&2
  exit 1
fi
ICON_WORK_DIR="$(mktemp -d "${TMPDIR:-/tmp}/mchose-icon.XXXXXX")"
trap 'rm -rf "$ICON_WORK_DIR"' EXIT
ICONSET="$ICON_WORK_DIR/AppIcon.iconset"
mkdir -p "$ICONSET"
for size in 16 32 128 256 512; do
  rsvg-convert --width="$size" --height="$size" "$ASSETS_DIR/AppIcon.svg" -o "$ICONSET/icon_${size}x${size}.png"
  pixels=$((size * 2))
  rsvg-convert --width="$pixels" --height="$pixels" "$ASSETS_DIR/AppIcon.svg" -o "$ICONSET/icon_${size}x${size}@2x.png"
done
iconutil -c icns "$ICONSET" -o "$ICON_WORK_DIR/AppIcon.icns"
cp "$ICON_WORK_DIR/AppIcon.icns" "$ASSETS_DIR/AppIcon.icns"
cp "$ICONSET/icon_512x512@2x.png" "$ASSETS_DIR/AppIcon-preview.png"
echo "Generated AppIcon.icns and AppIcon-preview.png from AppIcon.svg"
