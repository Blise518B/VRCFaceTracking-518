# Packages the current Release build into a shareable zip under dist\
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$src = Join-Path $root "VRCFaceTracking\bin\x64\Release\net10.0-windows10.0.22621.0\win-x64"
$dst = Join-Path $root "dist\VRCFaceTracking-Blise518B-Edition"
$zip = Join-Path $root "dist\VRCFaceTracking-Blise518B-Edition.zip"

if (-not (Test-Path (Join-Path $src "VRCFaceTracking.exe"))) {
    Write-Host "No Release build found. Run build.bat (or update.bat) first." -ForegroundColor Red
    exit 1
}

if (Test-Path $dst) { Remove-Item $dst -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }
New-Item -ItemType Directory $dst -Force | Out-Null

Write-Host "Copying build..."
Copy-Item "$src\*" $dst -Recurse -Exclude *.pdb

@"
VRCFaceTracking - Blise518B Edition
====================================

Custom build of VRCFaceTracking (https://github.com/benaclejames/VRCFaceTracking, Apache 2.0)
with extra features:

 - Crash auto-restart: crashed tracking modules AND the app itself restart automatically
   (toggle + statistics in Settings -> Crash Auto-Restart)
 - Range Calibration: per-expression floor/ceiling + sensitivity curves
   (Tracking Mutations page -> Range Calibration)

HOW TO RUN
----------
1. Unzip this folder anywhere (e.g. Documents). Don't run it from inside the zip!
2. Run VRCFaceTracking.exe
3. If Windows SmartScreen complains ("Windows protected your PC"):
   click "More info" -> "Run anyway". That happens because this build isn't code-signed.

Your existing VRCFaceTracking settings and modules are picked up automatically
(they live in %APPDATA%\VRCFaceTracking, not in the app folder).
"@ | Set-Content (Join-Path $dst "README.txt") -Encoding utf8

Write-Host "Zipping (takes a moment)..."
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($dst, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $true)

Write-Host ("Done: {0} ({1:N1} MB)" -f $zip, ((Get-Item $zip).Length / 1MB)) -ForegroundColor Green
explorer /select,$zip
