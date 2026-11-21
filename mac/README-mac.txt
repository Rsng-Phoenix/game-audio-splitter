GAME AUDIO SPLITTER - macOS
===========================
Plays one app's sound (or all system audio) on extra speakers or headphones
at the same time, with its own volume and left/right balance per device.
Your main output (e.g. IEMs on a USB-C DAC) keeps playing exactly as before.
No drivers, no BlackHole/Loopback needed - it uses Apple's own audio taps.

REQUIREMENTS
------------
- macOS 14.4 (Sonoma) or newer, Apple Silicon or Intel.

BUILDING (only needed if you have the source, not the zip)
--------
1. Install the compiler once (no full Xcode needed):
       xcode-select --install
2. In Terminal, from this "mac" folder:
       chmod +x build.sh
       ./build.sh
3. Result:
       build/Game Audio Splitter.app          the app
       ../release/GameAudioSplitter-macOS.zip  send this to people

FIRST RUN
---------
1. Unzip and drag "Game Audio Splitter" into Applications (optional).
2. Double-click it. Because it is not from the App Store, macOS blocks it once:
     - macOS 15 or newer: click "Done", then open
       System Settings > Privacy & Security, scroll down and click
       "Open Anyway" next to Game Audio Splitter, then confirm.
     - macOS 14: right-click the app > Open > Open.
   (Terminal alternative:  xattr -dr com.apple.quarantine "/Applications/Game Audio Splitter.app")
3. The first time you press Start, macOS asks to allow "Audio Capture"
   (recording audio from other apps). Click Allow. If you clicked Don't Allow,
   turn it on in System Settings > Privacy & Security (on macOS 15+ it is under
   "Screen & System Audio Recording"), then press Stop and Start.
   Until it is allowed, the extra outputs stay silent.
   Note: each rebuild of the app counts as a new app for this permission.

HOW TO USE
----------
1. Source
     - "One app": pick the game/app from the list. If it isn't running yet,
       press Start anyway - it waits and connects by itself when the app
       starts playing sound.
     - "All system audio": everything you hear, except this app itself.
2. "Also play it on": switch on each device that should also play the sound.
     - Device volume, Balance and the mute button ARE that device's own macOS
       settings (the same ones as the menu-bar volume and System Settings >
       Sound). Change them here or there - both always show the same value.
     - Balance moves the sound left/right (double-click to centre).
     - Some devices (many HDMI / USB ones) have no volume control of their own.
       Then the card says "This device has no volume control of its own; set
       here." and the slider sets the level of the copy instead.
     - "Follow main volume": the copy gets louder and quieter with your main
       output's volume keys, and goes silent when the main output is muted.
     - Don't switch on your main output - you'd hear the sound twice.
3. Press Start. Everything can be changed while it plays.
   Devices that are unplugged stay listed (greyed out) and reconnect
   automatically when plugged back in.

Settings are remembered. Closing the window quits the app and stops copying.

APP VOLUME (a per-app volume mixer - macOS doesn't have one)
----------
Switch the top of the window from "Splitter" to "App volume".
- Apps appear here while they play sound, with their own volume slider and
  mute button. Browser tabs and other helper processes are merged into their
  app (e.g. all Chrome tabs = Chrome).
- Double-click or Option-click a slider to put it back to 100%.
  "Reset all" puts every app back to 100%.
- "Saved" lists apps you turned down that aren't playing right now, so you
  can still reset them. Settings are remembered and re-applied automatically
  whenever that app plays again.
- It changes what you hear on your main output. Copies made by the Splitter
  keep their own levels.
- It follows your main output: switch headphones/speakers and the lowered
  apps move with it.
- Only apps you turn below 100% (or mute) are touched. Such an app gets a few
  milliseconds of extra delay; apps at 100% are completely untouched.
- If Game Audio Splitter quits or crashes, macOS removes its audio taps and
  every app goes back to its normal volume by itself.
- Uses the same "Audio Capture" permission as the Splitter. No screen
  recording permission and no extra audio driver.

NOTES
-----
- CPU use is tiny: audio is copied inside Core Audio's own real-time thread,
  the level meters only update while the window is visible, and nothing polls.
- Bluetooth headphones as an extra output: the app asks macOS not to open
  their microphone, so they should stay in high-quality (not call) mode.
  If one drops to call quality, tell the developer.
