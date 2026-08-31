#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
BUILD_DIR="$ROOT_DIR/.build/luma-universal"
DIST_DIR="$ROOT_DIR/.build/dist"
APP_NAME="Luma Translate"
EXECUTABLE_NAME="LumaTranslate"
APP_PATH="$BUILD_DIR/$APP_NAME.app"
VERSION="${LUMA_VERSION:-1.0.0}"
BUILD_NUMBER="${LUMA_BUILD_NUMBER:-1}"

rm -rf "$BUILD_DIR" "$DIST_DIR"
mkdir -p "$APP_PATH/Contents/MacOS" "$APP_PATH/Contents/Resources" "$DIST_DIR"

echo "Building arm64 + x86_64 release binary…"
swift build \
  --package-path "$ROOT_DIR" \
  -c release \
  --arch arm64 \
  --arch x86_64

BIN_DIR="$(swift build \
  --package-path "$ROOT_DIR" \
  -c release \
  --arch arm64 \
  --arch x86_64 \
  --show-bin-path)"

install -m 755 "$BIN_DIR/$EXECUTABLE_NAME" "$APP_PATH/Contents/MacOS/$EXECUTABLE_NAME"
install -m 644 "$ROOT_DIR/Config/Info.plist" "$APP_PATH/Contents/Info.plist"

/usr/libexec/PlistBuddy -c "Set :CFBundleShortVersionString $VERSION" "$APP_PATH/Contents/Info.plist"
/usr/libexec/PlistBuddy -c "Set :CFBundleVersion $BUILD_NUMBER" "$APP_PATH/Contents/Info.plist"

RESOURCE_DIR="$ROOT_DIR/Sources/LumaTranslate/Resources"
install -m 644 "$RESOURCE_DIR/offline_ecdict_core.tsv" "$APP_PATH/Contents/Resources/offline_ecdict_core.tsv"
install -m 644 "$RESOURCE_DIR/dictionary-manifest.json" "$APP_PATH/Contents/Resources/dictionary-manifest.json"
install -m 644 "$RESOURCE_DIR/ECDICT_LICENSE.txt" "$APP_PATH/Contents/Resources/ECDICT_LICENSE.txt"
install -m 644 "$RESOURCE_DIR/luma-logo.png" "$APP_PATH/Contents/Resources/luma-logo.png"
install -m 644 "$RESOURCE_DIR/luma-logo-icon.png" "$APP_PATH/Contents/Resources/luma-logo-icon.png"

ICONSET="$BUILD_DIR/LumaTranslate.iconset"
mkdir -p "$ICONSET"
ICON_SOURCE="$RESOURCE_DIR/luma-logo-icon.png"
sips -z 16 16     "$ICON_SOURCE" --out "$ICONSET/icon_16x16.png" >/dev/null
sips -z 32 32     "$ICON_SOURCE" --out "$ICONSET/icon_16x16@2x.png" >/dev/null
sips -z 32 32     "$ICON_SOURCE" --out "$ICONSET/icon_32x32.png" >/dev/null
sips -z 64 64     "$ICON_SOURCE" --out "$ICONSET/icon_32x32@2x.png" >/dev/null
sips -z 128 128   "$ICON_SOURCE" --out "$ICONSET/icon_128x128.png" >/dev/null
sips -z 256 256   "$ICON_SOURCE" --out "$ICONSET/icon_128x128@2x.png" >/dev/null
sips -z 256 256   "$ICON_SOURCE" --out "$ICONSET/icon_256x256.png" >/dev/null
sips -z 512 512   "$ICON_SOURCE" --out "$ICONSET/icon_256x256@2x.png" >/dev/null
sips -z 512 512   "$ICON_SOURCE" --out "$ICONSET/icon_512x512.png" >/dev/null
sips -z 1024 1024 "$ICON_SOURCE" --out "$ICONSET/icon_512x512@2x.png" >/dev/null
iconutil -c icns "$ICONSET" -o "$APP_PATH/Contents/Resources/LumaTranslate.icns"

ARCHS="$(lipo -archs "$APP_PATH/Contents/MacOS/$EXECUTABLE_NAME")"
if [[ "$ARCHS" != *"arm64"* || "$ARCHS" != *"x86_64"* ]]; then
  echo "Universal build verification failed. Found: $ARCHS" >&2
  exit 1
fi

if [[ -n "${CODE_SIGN_IDENTITY:-}" ]]; then
  codesign --force --deep --options runtime --timestamp \
    --entitlements "$ROOT_DIR/Config/LumaTranslate.entitlements" \
    --sign "$CODE_SIGN_IDENTITY" "$APP_PATH"
else
  codesign --force --deep --sign - "$APP_PATH"
fi
codesign --verify --deep --strict --verbose=2 "$APP_PATH"

ZIP_PATH="$DIST_DIR/Luma-Translate-macOS-Universal-$VERSION.zip"
DMG_PATH="$DIST_DIR/Luma-Translate-macOS-Universal-$VERSION.dmg"
ditto -c -k --sequesterRsrc --keepParent "$APP_PATH" "$ZIP_PATH"
hdiutil create -quiet -volname "Luma Translate" -srcfolder "$APP_PATH" -ov -format UDZO "$DMG_PATH"

shasum -a 256 "$ZIP_PATH" "$DMG_PATH" > "$DIST_DIR/SHA256SUMS.txt"

echo "Built $APP_PATH"
echo "Architectures: $ARCHS"
echo "Distributables: $DIST_DIR"
