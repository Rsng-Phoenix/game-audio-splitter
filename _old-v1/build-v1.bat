@echo off
rem Builds GameAudioSplitter.exe and a shareable zip. Nothing to install:
rem the C# compiler ships with Windows (.NET Framework 4.x).
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo C# compiler not found at "%CSC%".
    echo Enable ".NET Framework 4.8" in "Turn Windows features on or off".
    pause & exit /b 1
)

tasklist /fi "imagename eq GameAudioSplitter.exe" | find /i "GameAudioSplitter.exe" >nul && (
    echo GameAudioSplitter is running - close it first, then build again.
    pause & exit /b 1
)

echo Building GameAudioSplitter.exe ...
"%CSC%" /nologo /target:winexe /unsafe /platform:x64 /optimize+ ^
    /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
    /out:GameAudioSplitter.exe GameAudioSplitter.cs
if errorlevel 1 ( echo. & echo BUILD FAILED - see errors above. & pause & exit /b 1 )

echo Packing release\GameAudioSplitter.zip ...
if not exist release mkdir release
powershell -NoProfile -Command "Compress-Archive -Force -Path 'GameAudioSplitter.exe','README.txt' -DestinationPath 'release\GameAudioSplitter.zip'"
if errorlevel 1 ( echo Zip failed. & pause & exit /b 1 )

echo.
echo Done.
echo   App:   %~dp0GameAudioSplitter.exe
echo   Share: %~dp0release\GameAudioSplitter.zip
pause
