#!/usr/bin/env bash
# Builds Game Audio Splitter from source and installs it into /Applications
# (or ~/Applications when /Applications is not writable), then adds it to the Dock.
# Needs only the Xcode Command Line Tools:  xcode-select --install
#
#   ./install.sh               build + install + add to Dock (also used to update)
#   ./install.sh --no-dock     build + install, leave the Dock alone
#   ./install.sh --uninstall   remove the app (and its Dock icon)
set -euo pipefail
cd "$(dirname "$0")"

APP_NAME="Game Audio Splitter"
BUNDLE_ID="com.gameaudiosplitter.app"
BUILT="build/$APP_NAME.app"
LSREGISTER="/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister"
PB="/usr/libexec/PlistBuddy"

step() { printf '\033[36m==> %s\033[0m\n' "$*"; }
fail() { printf '\033[31mERROR: %s\033[0m\n' "$*" >&2; exit 1; }

MODE="install"
DOCK=1
for arg in "$@"; do
    case "$arg" in
        --uninstall) MODE="uninstall" ;;
        --no-dock)   DOCK=0 ;;
        -h|--help)   sed -n '2,8p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *)           fail "Unknown option: $arg  (use --no-dock, --uninstall or --help)" ;;
    esac
done

# ---- Dock helpers: edit a copy exported through `defaults`, then import it back,
#      so the preferences daemon never overwrites the change.
dock_export() { defaults export com.apple.dock "$1" 2>/dev/null; }

# Prints the index of our app among the Dock's apps (empty if it isn't there).
dock_index() {
    local plist="$1" i=0 url
    while "$PB" -c "Print :persistent-apps:$i" "$plist" >/dev/null 2>&1; do
        url=$("$PB" -c "Print :persistent-apps:$i:tile-data:file-data:_CFURLString" "$plist" 2>/dev/null || true)
        if [[ "$url" == *"/$APP_NAME.app"* || "$url" == *"/${APP_NAME// /%20}.app"* ]]; then
            echo "$i"; return 0
        fi
        i=$((i + 1))
    done
}

add_to_dock() {
    local app="$1" tmp idx
    tmp="$(mktemp -t gas-dock)"
    dock_export "$tmp" || { echo "Couldn't read the Dock settings; skipping the Dock."; return 0; }
    idx="$(dock_index "$tmp")"
    if [[ -n "$idx" ]]; then
        echo "Already in the Dock."
    else
        step "Adding to the Dock"
        defaults write com.apple.dock persistent-apps -array-add \
            "<dict><key>tile-data</key><dict><key>file-data</key><dict><key>_CFURLString</key><string>$app</string><key>_CFURLStringType</key><integer>0</integer></dict></dict></dict>"
        killall Dock 2>/dev/null || true
    fi
    rm -f "$tmp"
}

remove_from_dock() {
    local tmp idx
    tmp="$(mktemp -t gas-dock)"
    dock_export "$tmp" || return 0
    idx="$(dock_index "$tmp")"
    if [[ -n "$idx" ]]; then
        step "Removing from the Dock"
        "$PB" -c "Delete :persistent-apps:$idx" "$tmp"
        defaults import com.apple.dock "$tmp"
        killall Dock 2>/dev/null || true
    fi
    rm -f "$tmp"
}

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
    remove_from_dock
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

[[ "$DOCK" == 1 ]] && add_to_dock "$DEST/$APP_NAME.app"

echo
printf '\033[32mInstalled: %s\033[0m\n' "$DEST/$APP_NAME.app"
echo "Open it from the Dock or Launchpad, or run:  open \"$DEST/$APP_NAME.app\""
