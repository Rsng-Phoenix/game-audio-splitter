GAME AUDIO SPLITTER  v2
=======================
Plays one app's sound (or everything from one device) on extra speakers or
headphones at the same time - e.g. game audio on your earphones AND on a
second speaker, capture card or stream device. Built to be extremely light:
about 0.02% CPU while copying, and it never touches your main output.

WINDOWS
-------
Requirements: Windows 10 (2004 or newer) or Windows 11, 64-bit. Nothing to install.

First run
  1. Unzip anywhere and double-click GameAudioSplitter.exe.
  2. If Windows says "Windows protected your PC" (normal for apps not from the
     Store): click "More info" -> "Run anyway". Or right-click the .exe ->
     Properties -> tick "Unblock" -> OK.

How to use
  1. Copy sound from: "One app" (pick the game; start it first so it shows in
     the list) or "Whole device".
  2. Also play it on: switch on one or more devices.
       - Device volume, Balance and Mute are that device's own Windows
         settings. Change them here or in Windows - both always match.
       - "Follow main volume": the copy also gets louder/quieter with your
         main output's volume keys.
  3. Press Start. Changes apply live; press Stop to end.
  Advanced: buffer size (raise it if you hear crackles) and auto-start.

Your choices are saved in %APPDATA%\GameAudioSplitter\settings.ini
To uninstall: delete the .exe and that folder.

MAC
---
See mac\README-mac.txt (macOS 14.4 or newer; built with build.sh on a Mac).

BUILDING FROM SOURCE (optional)
-------------------------------
Windows: double-click windows\build.bat. It uses the C# compiler and WPF that
come with Windows - no Visual Studio needed. Output in release\.
Mac:     cd mac && ./build.sh   (needs Xcode Command Line Tools).

LICENSE
-------
GNU GPL v3 (see LICENSE and NOTICE). Free to use, change and share with credit to the
author: "Based on Game Audio Splitter by Priyanshu Pradhan (BlackWolf)".
