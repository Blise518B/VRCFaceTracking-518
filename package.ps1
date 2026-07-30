# Packages the current Release build into a shareable zip under dist\
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$src = Join-Path $root "VRCFaceTracking\bin\x64\Release\net10.0-windows10.0.22621.0\win-x64"
$dst = Join-Path $root "dist\VRCFaceTracking-518-Edition"
$zip = Join-Path $root "dist\VRCFaceTracking-518-Edition.zip"

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
VRCFaceTracking - 518 Edition
====================================

Custom build of VRCFaceTracking (https://github.com/benaclejames/VRCFaceTracking, Apache 2.0)
with these additions:

RELIABILITY
 - Crash auto-restart: crashed tracking modules AND the app itself restart
   automatically (toggle + statistics in Settings -> Crash Auto-Restart)
 - Crash reports: every crash writes a readable report to
   Documents\VRCFaceTracking\crashes\ explaining what died and why, including
   whether VRChat / SteamVR / SRanipal were running at the time

TUNING
 - Range Calibration: per-expression floor/ceiling + sensitivity curves
   (Tracking Mutations page -> Range Calibration)
 - Curve Editor: draw the full input/output response curve for 100 channels,
   including eye gaze and head rotation, which no other mutation can reach.
   Two editors side by side, with a live dot showing where your face is right
   now - great for matching one eye's openness to the other.

SETUP AND DIAGNOSIS
 - Sources page: works out which tracking sources are actually live and starts
   only the matching modules, so an unused module cannot grab a slot it has no
   hardware for. Every decision comes with a readable reason.
 - Launch buttons: store a launch command per source (an .exe or a steam:// URI)
   and start it from the Sources page, or have it start automatically with
   VRCFaceTracking.
 - Tracking Debug page: a live table of all 88 unified expression shapes plus
   the eye signals - value before and after your mutations, where each one comes
   from, and how long ago it last moved. Filter box and an "only moving" toggle.

LOOK AND EXTRAS
 - Neon518 theme matching VRC Parameter Relay
 - Lite Console: switch the running session to a tiny console monitor and back
 - OSC Fallback Standard client for when VRChat's own discovery stops seeing us

HOW TO RUN
----------
1. Unzip this folder anywhere (e.g. Documents). Don't run it from inside the zip!
2. Run VRCFaceTracking.exe
3. If Windows SmartScreen complains ("Windows protected your PC"):
   click "More info" -> "Run anyway". That happens because this build isn't code-signed.

Your existing VRCFaceTracking settings and modules are picked up automatically
(they live in %APPDATA%\VRCFaceTracking, not in the app folder), so this build
sits alongside a normal install rather than replacing it.

Not code-signed, not affiliated with the upstream project - report problems here,
not to the VRCFaceTracking developers.
"@ | Set-Content (Join-Path $dst "README.txt") -Encoding utf8

Write-Host "Zipping (takes a moment)..."
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($dst, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $true)
Write-Host ("Zip: {0} ({1:N1} MB)" -f $zip, ((Get-Item $zip).Length / 1MB))

Write-Host "Building single-file exe (app embedded inside)..."
$bundleOut = Join-Path $root "dist\bundle-out"
dotnet publish (Join-Path $root "VRCFaceTracking.Bundle\VRCFaceTracking.Bundle.csproj") -c Release -r win-x64 -o $bundleOut -v q --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Host "Bundle build failed - the zip above is still usable." -ForegroundColor Red
    explorer /select,$zip
    exit 1
}
$exe = Join-Path $root "dist\VRCFaceTracking-518-Edition.exe"
Copy-Item (Join-Path $bundleOut "VRCFaceTracking-518-Edition.exe") $exe -Force

Write-Host ("Done: {0} ({1:N1} MB)" -f $exe, ((Get-Item $exe).Length / 1MB)) -ForegroundColor Green
Write-Host "Send your friends the .exe - they double-click it and it unpacks + starts itself."
explorer /select,$exe
