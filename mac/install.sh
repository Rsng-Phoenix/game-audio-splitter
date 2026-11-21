#!/usr/bin/env bash
# Builds Game Audio Splitter from source and installs it into /Applications
# (or ~/Applications when /Applications is not writable). Needs only the
# Xcode Command Line Tools:  xcode-select --install
#
#   ./install.sh               build + install (also used to update)
#   ./install.sh --uninstall   remove the app
set -euo pipefail
cd "$(dirname "$0")"

APP_NAME="Game Audio Splitter"
BUNDLE_ID="com.gameaudiosplitter.app"
BUILT="build/$APP_NAME.app"
LSREGISTER="/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister"

step() { printf '\033[36m==> %s\033[0m\n' "$*"; }
fail() { printf '\033[31mERROR: %s\033[0m\n' "$*" >&2; exit 1; }

MODE="install"
case "${1:-}" in
    "")            ;;
    --uninstall)   MODE="uninstall" ;;
    -h|--help)     sed -n '2,7p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *)             fail "Unknown option: $1  (use --uninstall or --help)" ;;
esac

# Quit a running copy first (only if it is running; never launches it) and wait for it to exit.
if pgrep -x GameAudioSplitter >/dev/null 2>&1; then
    step "Quitting the running copy"
    osascript -e "tell application id \"$BUNDLE_ID\" to quit" >/dev/null 2>&1 || true
    for _ in 1 2 3 4 5 6 7 8 9 10; do
        pgrep -x GameAudioSplitter >/dev/null 2>&1 || break
        sleep 0.5
    done
    pgrep -x GameAudioSplitter >/dev/null 2>&1 && fail "Game Audio Splitter is still running. Quit it, then run this again."
fi

if [[ "$MODE" == "uninstall" ]]; then
    step "Removing $APP_NAME"
    for dir in "/Applications" "$HOME/Applications"; do
        if [[ -d "$dir/$APP_NAME.app" ]]; then
            [[ -x "$LSREGISTER" ]] && "$LSREGISTER" -u "$dir/$APP_NAME.app" >/dev/null 2>&1 || true
            rm -rf "$dir/$APP_NAME.app"
        fi
    done
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

# Make "open -a" / Launchpad use the installed copy, not the build folder copy.
if [[ -x "$LSREGISTER" ]]; then
    "$LSREGISTER" -u "$BUILT" >/dev/null 2>&1 || true
    "$LSREGISTER" -f "$DEST/$APP_NAME.app" >/dev/null 2>&1 || true
fi

echo
printf '\033[32mInstalled: %s\033[0m\n' "$DEST/$APP_NAME.app"
echo "Open it from Launchpad, or run:  open \"$DEST/$APP_NAME.app\""
