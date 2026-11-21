#!/usr/bin/env bash
# Builds Game Audio Splitter from source and installs it into /Applications
# (or ~/Applications when /Applications is not writable). Needs only the
# Xcode Command Line Tools:  xcode-select --install
#
#   ./install.sh               build + install
#   ./install.sh --uninstall   remove the app
set -euo pipefail
cd "$(dirname "$0")"

APP_NAME="Game Audio Splitter"
BUNDLE_ID="com.gameaudiosplitter.app"
BUILT="build/$APP_NAME.app"

step() { printf '\033[36m==> %s\033[0m\n' "$*"; }

# Quit a running copy first (only if it is running; never launches it).
if pgrep -x GameAudioSplitter >/dev/null 2>&1; then
    step "Quitting the running copy"
    osascript -e "tell application id \"$BUNDLE_ID\" to quit" >/dev/null 2>&1 || true
    sleep 1
fi

if [[ "${1:-}" == "--uninstall" ]]; then
    step "Removing $APP_NAME"
    rm -rf "/Applications/$APP_NAME.app" "$HOME/Applications/$APP_NAME.app"
    echo "Uninstalled. Settings remain in ~/Library/Preferences/$BUNDLE_ID.plist"
    exit 0
fi

step "Building from source"
./build.sh

DEST="/Applications"
if [[ ! -w "$DEST" ]]; then
    DEST="$HOME/Applications"
    mkdir -p "$DEST"
fi

step "Installing to $DEST"
rm -rf "$DEST/$APP_NAME.app"
ditto "$BUILT" "$DEST/$APP_NAME.app"

echo
printf '\033[32mInstalled: %s\033[0m\n' "$DEST/$APP_NAME.app"
echo "Open it from Launchpad, or run:  open -a \"$APP_NAME\""
