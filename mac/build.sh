#!/usr/bin/env bash
# Builds "Game Audio Splitter.app" (universal: Apple Silicon + Intel) and a shareable zip.
# Needs only the Xcode Command Line Tools:  xcode-select --install
set -euo pipefail
cd "$(dirname "$0")"

APP_NAME="Game Audio Splitter"
EXE="GameAudioSplitter"
MIN_MACOS="14.4"
BUILD="build"
APP="$BUILD/$APP_NAME.app"
ZIP="../release/GameAudioSplitter-macOS.zip"

fail() { echo; echo "ERROR: $*" >&2; exit 1; }

[ "$(uname -s)" = "Darwin" ] || fail "This script must be run on a Mac."
xcrun --find swiftc >/dev/null 2>&1 || fail "Swift compiler not found. Install the Command Line Tools:  xcode-select --install"

SDK="$(xcrun --sdk macosx --show-sdk-path)"
SDK_VER="$(xcrun --sdk macosx --show-sdk-version)"
# Process taps need the macOS 14.4 SDK or newer (Xcode / Command Line Tools 15.3+).
if ! awk -v v="$SDK_VER" 'BEGIN { split(v, a, "."); exit !((a[1] > 14) || (a[1] == 14 && a[2] >= 4)) }'; then
    fail "macOS SDK $SDK_VER is too old (need 14.4+). Update via System Settings > Software Update, or: xcode-select --install"
fi

echo "==> Cleaning"
rm -rf "$BUILD"
mkdir -p "$BUILD/obj"

for ARCH in arm64 x86_64; do
    echo "==> Compiling ($ARCH)"
    xcrun swiftc -O -parse-as-library -swift-version 5 \
        -sdk "$SDK" -target "$ARCH-apple-macos$MIN_MACOS" \
        GameAudioSplitter.swift -o "$BUILD/obj/$EXE-$ARCH" \
        || fail "Compile failed for $ARCH (see the errors above)."
done

echo "==> Creating universal binary"
lipo -create "$BUILD/obj/$EXE-arm64" "$BUILD/obj/$EXE-x86_64" -output "$BUILD/obj/$EXE" \
    || fail "lipo failed."

echo "==> Assembling $APP_NAME.app"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BUILD/obj/$EXE" "$APP/Contents/MacOS/$EXE"
cp Info.plist "$APP/Contents/Info.plist"
[[ -f ../LICENSE ]] && cp ../LICENSE "$APP/Contents/Resources/LICENSE.txt"
[[ -f ../NOTICE ]] && cp ../NOTICE "$APP/Contents/Resources/NOTICE.txt"
printf 'APPL????' > "$APP/Contents/PkgInfo"

# App icon from ../assets/AppIcon.png (1024 px). Optional: the app builds fine without it.
ICON_SRC="../assets/AppIcon.png"
make_icon() {
    local set="$BUILD/AppIcon.iconset"
    rm -rf "$set"; mkdir -p "$set"
    for s in 16 32 128 256 512; do
        sips -z "$s" "$s" "$ICON_SRC" --out "$set/icon_${s}x${s}.png" >/dev/null || return 1
        sips -z "$((s * 2))" "$((s * 2))" "$ICON_SRC" --out "$set/icon_${s}x${s}@2x.png" >/dev/null || return 1
    done
    iconutil -c icns "$set" -o "$APP/Contents/Resources/AppIcon.icns" || return 1
}
if [ -f "$ICON_SRC" ]; then
    echo "==> Making app icon"
    make_icon || echo "    warning: could not make the icon - continuing without it"
else
    echo "==> No $ICON_SRC - building without a custom icon"
fi

echo "==> Signing (ad-hoc)"
codesign --force --deep --sign - "$APP" || fail "codesign failed."
codesign --verify --verbose=1 "$APP" || fail "Signature check failed."

echo "==> Zipping"
mkdir -p "$(dirname "$ZIP")"
rm -f "$ZIP"
ditto -c -k --keepParent "$APP" "$ZIP" || fail "Zip failed."

echo
echo "Done."
echo "  App:   $(pwd)/$APP"
echo "  Share: $(cd "$(dirname "$ZIP")" && pwd)/$(basename "$ZIP")"
