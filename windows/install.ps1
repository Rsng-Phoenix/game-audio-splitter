<#
.SYNOPSIS
    Builds Game Audio Splitter from source and installs it for the current user.

.DESCRIPTION
    No administrator rights and no extra software needed: the build uses the C# compiler
    and WPF that ship with Windows (.NET Framework 4.8).
    Installs to %LOCALAPPDATA%\Programs\GameAudioSplitter and adds a Start Menu shortcut.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File windows\install.ps1
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File windows\install.ps1 -Uninstall
#>
param(
    [switch]$Uninstall,
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\GameAudioSplitter'),
    [switch]$NoShortcut
)

$ErrorActionPreference = 'Stop'
$root     = Split-Path -Parent $PSScriptRoot
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Game Audio Splitter.lnk'

function Write-Step([string]$text) { Write-Host "==> $text" -ForegroundColor Cyan }

if (Get-Process GameAudioSplitter -ErrorAction SilentlyContinue) {
    throw 'Game Audio Splitter is running. Close it, then run this again.'
}

if ($Uninstall) {
    Write-Step 'Removing Game Audio Splitter'
    if (-not $NoShortcut) { Remove-Item $shortcut -ErrorAction SilentlyContinue }
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
    $link = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcut)
    $link.TargetPath       = $exe
    $link.WorkingDirectory = $InstallDir
    $link.IconLocation     = "$exe,0"
    $link.Description      = 'Play an app''s sound on extra output devices'
    $link.Save()
}

Write-Host ''
Write-Host 'Installed. Open "Game Audio Splitter" from the Start Menu.' -ForegroundColor Green
