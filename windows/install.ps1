<#
.SYNOPSIS
    Builds Game Audio Splitter from source and installs it for the current user.

.DESCRIPTION
    No administrator rights and no extra software needed: the build uses the C# compiler
    and WPF that ship with Windows (.NET Framework 4.8).
    Installs to %LOCALAPPDATA%\Programs\GameAudioSplitter and adds Start Menu and Desktop shortcuts
    (-NoShortcut / -NoDesktop to skip them).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File windows\install.ps1
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File windows\install.ps1 -Uninstall
#>
param(
    [switch]$Uninstall,
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\GameAudioSplitter'),
    [switch]$NoShortcut,
    [switch]$NoDesktop
)

$ErrorActionPreference = 'Stop'
$root     = Split-Path -Parent $PSScriptRoot
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Game Audio Splitter.lnk'
$desktop  = Join-Path ([Environment]::GetFolderPath('Desktop'))  'Game Audio Splitter.lnk'

function New-Shortcut([string]$path, [string]$exe, [string]$dir) {
    $link = (New-Object -ComObject WScript.Shell).CreateShortcut($path)
    $link.TargetPath       = $exe
    $link.WorkingDirectory = $dir
    $link.IconLocation     = "$exe,0"
    $link.Description      = 'Play an app''s sound on extra output devices'
    $link.Save()
}

function Write-Step([string]$text) { Write-Host "==> $text" -ForegroundColor Cyan }

if (Get-Process GameAudioSplitter -ErrorAction SilentlyContinue) {
    throw 'Game Audio Splitter is running. Close it, then run this again.'
}

if ($Uninstall) {
    Write-Step 'Removing Game Audio Splitter'
    if (-not $NoShortcut) { Remove-Item $shortcut -ErrorAction SilentlyContinue }
    if (-not $NoDesktop)  { Remove-Item $desktop  -ErrorAction SilentlyContinue }
    Remove-Item $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host 'Uninstalled. Your settings remain in %APPDATA%\GameAudioSplitter (delete that folder to remove them).'
    return
}

Write-Step 'Building from source'
& cmd.exe /c "`"$PSScriptRoot\build.bat`" nopause"
if ($LASTEXITCODE -ne 0) { throw 'Build failed - see the messages above.' }

Write-Step "Installing to $InstallDir"
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item (Join-Path $root 'release\GameAudioSplitter.exe') $InstallDir -Force
$exe = Join-Path $InstallDir 'GameAudioSplitter.exe'

if (-not $NoShortcut) {
    Write-Step 'Adding Start Menu shortcut'
    New-Shortcut $shortcut $exe $InstallDir
}
if (-not $NoDesktop) {
    Write-Step 'Adding Desktop shortcut'
    New-Shortcut $desktop $exe $InstallDir
}

Write-Host ''
Write-Host 'Installed. Open "Game Audio Splitter" from the Desktop or the Start Menu.' -ForegroundColor Green
Write-Host 'Tip: right-click it in Start and choose "Pin to Start" or "Pin to taskbar".'
