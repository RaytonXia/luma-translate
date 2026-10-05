#!/usr/bin/env bash
set -euo pipefail
ROOT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
DIST_DIR="$ROOT_DIR/.build/dist"
VERIFY_DIR="$ROOT_DIR/.build/verification"
mkdir -p "$VERIFY_DIR"
DMG="$DIST_DIR/Luma-Translate-macOS-Universal-1.1.0.dmg"
MOUNT_DIR="$ROOT_DIR/.build/verify-mount"
INSTALL_DIR="$ROOT_DIR/.build/verify-install"
mkdir -p "$MOUNT_DIR" "$INSTALL_DIR"
hdiutil attach -quiet -nobrowse -readonly -mountpoint "$MOUNT_DIR" "$DMG"
trap 'hdiutil detach -quiet "$MOUNT_DIR" || true' EXIT
test -L "$MOUNT_DIR/Applications"
ditto "$MOUNT_DIR/Luma Translate.app" "$INSTALL_DIR/Luma Translate.app"
APP="$INSTALL_DIR/Luma Translate.app"
codesign --verify --deep --strict --verbose=2 "$APP"
lipo -archs "$APP/Contents/MacOS/LumaTranslate" | tee "$VERIFY_DIR/architectures.txt"
sw_vers > "$VERIFY_DIR/system.txt"
uname -m >> "$VERIFY_DIR/system.txt"
"$APP/Contents/MacOS/LumaTranslate" --verify-runtime "$VERIFY_DIR" > "$VERIFY_DIR/runtime.log" 2>&1 &
APP_PID=$!
for i in {1..90}; do
  if ! kill -0 "$APP_PID" 2>/dev/null; then
    wait "$APP_PID"
    cat "$VERIFY_DIR/runtime.json"
    exit 0
  fi
  sleep 1
done
kill "$APP_PID" 2>/dev/null || true
cat "$VERIFY_DIR/runtime.log"
echo "Runtime verification timed out" >&2
exit 1
