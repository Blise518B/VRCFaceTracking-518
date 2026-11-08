# Packages the current Release build into the two release files under dist\:
#   VRCFaceTracking-518-Windows.exe  one file that unpacks and starts itself
#   VRCFaceTracking-518-Windows.zip  the portable folder, also what the in-app updater installs
# The names never carry a version: the README's download button points at
# releases/latest/download/<name>, and the updater looks for the zip by name.
# GitHub Actions runs this for every build (.github/workflows/build.yml); locally it works the same.
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$ci = $env:GITHUB_ACTIONS -eq "true"
$src = Join-Path $root "VRCFaceTracking\bin\x64\Release\net10.0-windows10.0.22621.0\win-x64"

# Version comes from Directory.Build.props so there is exactly one place to bump it.
$propsPath = Join-Path $root "Directory.Build.props"
$props = [xml](Get-Content $propsPath)
$version = ($props.Project.PropertyGroup.Fork518Version | Where-Object { $_ }) -as [string]
$upstream = ($props.Project.PropertyGroup.UpstreamBaseVersion | Where-Object { $_ }) -as [string]
if ([string]::IsNullOrWhiteSpace($version)) {
    Write-Host "Could not read Fork518Version from Directory.Build.props." -ForegroundColor Red
    exit 1
}
$version = $version.Trim()
$upstream = if ($upstream) { $upstream.Trim() } else { "unknown" }

# The STAGING folder and intermediate zip keep fixed names: the bundle project embeds
# the zip by path, and VRCFaceTracking.Bundle and the in-app updater both expect the app
# in a folder of this name inside it.
$dst = Join-Path $root "dist\VRCFaceTracking-518-Edition"
$zip = Join-Path $root "dist\VRCFaceTracking-518-Edition.zip"
$releaseZip = Join-Path $root "dist\VRCFaceTracking-518-Windows.zip"
$releaseExe = Join-Path $root "dist\VRCFaceTracking-518-Windows.exe"

Write-Host "Packaging 518 Edition v$version (based on upstream $upstream)" -ForegroundColor Cyan

if (-not (Test-Path (Join-Path $src "VRCFaceTracking.exe"))) {
    Write-Host "No Release build found. Run build.bat (or update.bat) first." -ForegroundColor Red
    exit 1
}

# Guard against shipping a stale build under a fresh version number.
$builtVersion = (Get-Item (Join-Path $src "VRCFaceTracking.dll")).VersionInfo.ProductVersion
if ($builtVersion -and $builtVersion.Split('+')[0] -ne $version) {
    Write-Host "Build on disk reports version '$builtVersion' but Directory.Build.props says '$version'." -ForegroundColor Red
    Write-Host "Rebuild first (build.bat), otherwise the release would be misnamed." -ForegroundColor Red
    exit 1
}

if (Test-Path $dst) { Remove-Item $dst -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }
New-Item -ItemType Directory $dst -Force | Out-Null

Write-Host "Copying build..."
# Exclude build-only artifacts, not just symbols. The .appxrecipe in particular is
# MSBuild's packaging manifest: it is useless at runtime and embeds the building
# machine's Windows username and full NuGet cache paths, which would then ship to
# everyone who downloads a release.
$excluded = @("*.pdb", "*.appxrecipe", "*.buildschema.json", "*.pri.xml", "resources.pri.StaticAnalysis.log")
Copy-Item "$src\*" $dst -Recurse -Exclude $excluded

@"
VRCFaceTracking - 518 Edition
v$version  (based on upstream VRCFaceTracking $upstream)
====================================

UNOFFICIAL BUILD. This is a fork, not the official VRCFaceTracking, and it is not
affiliated with or supported by its developers. Please do NOT report problems with this
build to the VRCFaceTracking project, its GitHub or its Discord - report them here:
https://github.com/Blise518B/VRCFaceTracking-518/issues

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
 - Updates itself: a banner says when there is a new release, one click installs it
   (Settings -> Updates)

HOW TO RUN
----------
1. Unzip this folder somewhere you can write to (e.g. Documents). Don't run it from
   inside the zip, and avoid Program Files - the app can't update itself there.
2. Run VRCFaceTracking.exe
3. If Windows SmartScreen complains ("Windows protected your PC"):
   click "More info" -> "Run anyway". That happens because this build isn't code-signed.

Your existing VRCFaceTracking settings and modules are picked up automatically
(they live in %APPDATA%\VRCFaceTracking, not in the app folder), so this build
sits alongside a normal install rather than replacing it.

Not code-signed, not affiliated with the upstream project - report problems at the
link above, not to the VRCFaceTracking developers.
"@ | Set-Content (Join-Path $dst "README.txt") -Encoding utf8

# Marks this folder as a packaged release. The in-app updater only ever replaces a folder
# that carries this file, so it can never overwrite a build from source in bin\.
[ordered]@{
    version  = $version
    upstream = $upstream
    repo     = "Blise518B/VRCFaceTracking-518"
    asset    = "VRCFaceTracking-518-Windows.zip"
} | ConvertTo-Json | Set-Content (Join-Path $dst "518-release.json") -Encoding utf8

# Fail loudly rather than shipping a build that carries the author's identity.
$leaks = Get-ChildItem $dst -Recurse -File | Where-Object {
    $_.Extension -notin @(".dll",".exe",".winmd",".so",".dylib",".pri",".xbf",".png",".jpg",".ico")
} | Where-Object {
    (Select-String -Path $_.FullName -Pattern ([regex]::Escape($env:USERNAME)) -SimpleMatch -Quiet -ErrorAction SilentlyContinue)
}
if ($leaks) {
    Write-Host "Refusing to package: these staged files contain this machine's username:" -ForegroundColor Red
    $leaks | ForEach-Object { Write-Host ("  " + $_.FullName.Substring($dst.Length + 1)) -ForegroundColor Red }
    Write-Host "Add them to the exclusion list in package.ps1." -ForegroundColor Red
    exit 1
}

Write-Host "Zipping (takes a moment)..."
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($dst, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $true)
Write-Host ("Zip: {0} ({1:N1} MB)" -f $zip, ((Get-Item $zip).Length / 1MB))

Write-Host "Building single-file exe (app embedded inside)..."
$bundleOut = Join-Path $root "dist\bundle-out"
dotnet publish (Join-Path $root "VRCFaceTracking.Bundle\VRCFaceTracking.Bundle.csproj") -c Release -r win-x64 -o $bundleOut -v q --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Host "Bundle build failed - the zip above is still usable." -ForegroundColor Red
    if (-not $ci) { explorer /select,$zip }
    exit 1
}
Copy-Item (Join-Path $bundleOut "VRCFaceTracking-518-Edition.exe") $releaseExe -Force

# The bundle has embedded the zip by now, so the intermediate name is free to become
# the versioned artifact people actually download.
if (Test-Path $releaseZip) { Remove-Item $releaseZip -Force }
Move-Item $zip $releaseZip -Force

Write-Host ""
Write-Host ("518 Edition v{0} ready:" -f $version) -ForegroundColor Green
Write-Host ("  {0} ({1:N1} MB)   <- single file, unpacks and starts itself" -f (Split-Path $releaseExe -Leaf), ((Get-Item $releaseExe).Length / 1MB))
Write-Host ("  {0} ({1:N1} MB)   <- portable zip, also what the in-app updater installs" -f (Split-Path $releaseZip -Leaf), ((Get-Item $releaseZip).Length / 1MB))
if (-not $ci) {
    Write-Host ""
    Write-Host "Releases are built by GitHub: bump <Fork518Version> in Directory.Build.props, add the"
    Write-Host "CHANGELOG section, push, then push the tag 518-vX.Y.Z (see CHANGELOG.md)."
    Write-Host "These local files are for sharing a test build by hand."
    explorer /select,$releaseExe
}
