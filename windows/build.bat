@echo off
rem Builds release\GameAudioSplitter.exe and release\GameAudioSplitter-Windows.zip.
rem Nothing to install: the C# compiler and WPF ship with Windows (.NET Framework 4.8).
setlocal
cd /d "%~dp0"

set "NET=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
set "CSC=%NET%\csc.exe"
if not exist "%CSC%" (
    echo C# compiler not found at "%CSC%".
    echo Enable ".NET Framework 4.8" in "Turn Windows features on or off".
    pause & exit /b 1
)

tasklist /fi "imagename eq GameAudioSplitter.exe" | find /i "GameAudioSplitter.exe" >nul && (
    echo GameAudioSplitter is running - close it first, then build again.
    pause & exit /b 1
)

if not exist ..\release mkdir ..\release
echo Building GameAudioSplitter.exe ...
"%CSC%" /nologo /target:winexe /unsafe /platform:x64 /optimize+ /langversion:5 ^
    /lib:"%NET%\WPF" ^
    /r:PresentationFramework.dll /r:PresentationCore.dll /r:WindowsBase.dll /r:System.Xaml.dll ^
    /r:System.Drawing.dll /r:System.Core.dll ^
    /win32icon:..\assets\app.ico /win32manifest:app.manifest ^
    /resource:MainWindow.xaml,MainWindow.xaml ^
    /out:..\release\GameAudioSplitter.exe GameAudioSplitter.cs
if errorlevel 1 ( echo. & echo BUILD FAILED - see errors above. & pause & exit /b 1 )

echo Packing release\GameAudioSplitter-Windows.zip ...
powershell -NoProfile -Command "Compress-Archive -Force -Path '..\release\GameAudioSplitter.exe','..\README.txt','..\LICENSE','..\NOTICE' -DestinationPath '..\release\GameAudioSplitter-Windows.zip'"
if errorlevel 1 ( echo Zip failed. & pause & exit /b 1 )

echo.
echo Done.
echo   App:   %~dp0..\release\GameAudioSplitter.exe
echo   Share: %~dp0..\release\GameAudioSplitter-Windows.zip
if "%~1"=="" pause
