# Rebuilds the Release build when the source has changed since it was made.
#
# run.bat calls this before every start. The 518 Hub and Deck518 date run.bat by the
# newest source file in this repo and start it instead of the Release exe whenever that
# source is newer, so this is the step that turns "the source is newer" into "the newest
# code is what runs". Before it existed, every change made after the last manual
# build.bat stayed unlaunched: in Oct 2026 the Sep 15 fixes and the Sep 28 icon were
# still sitting in source while SteamVR and the hub kept starting the Sep 11 build.
#
# Exit 0: the Release build is current (or was just rebuilt).
# Exit 1: the rebuild failed; whatever Release build exists is left as it is.

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$exeDir = Join-Path $root "VRCFaceTracking\bin\x64\Release\net10.0-windows10.0.22621.0\win-x64"
$exe = Join-Path $exeDir "VRCFaceTracking.exe"

# Same rule as the hub and the deck (newest_source_mtime in 518Hub\osc_router\programs.py
# and Deck518\tray\deck_tray\registry.py): build output, virtualenvs and dot-folders are
# not source, and only code files count. Keep the three lists in step.
$excludedDirs = @(".git", ".venv", ".venv-py310", "venv", "node_modules", "__pycache__", "dist",
    "build", ".idea", ".vs", ".vscode", "bin", "obj", ".testdata", "target")
$codeSuffixes = @(".py", ".pyw", ".js", ".mjs", ".ts", ".html", ".css", ".json", ".bat", ".ps1",
    ".ahk", ".cs", ".cpp", ".c", ".h", ".ino", ".spec", ".toml", ".cfg", ".ini", ".yaml", ".yml",
    ".xaml", ".axaml", ".csproj", ".props", ".targets", ".resw", ".sln")

function Get-NewestSourceTime([string]$top) {
    $newest = [datetime]::MinValue
    $stack = New-Object System.Collections.Generic.Stack[string]
    $stack.Push($top)
    while ($stack.Count -gt 0) {
        $dir = $stack.Pop()
        try {
            foreach ($sub in [System.IO.Directory]::EnumerateDirectories($dir)) {
                $name = [System.IO.Path]::GetFileName($sub)
                if ($excludedDirs -notcontains $name -and -not $name.StartsWith(".")) {
                    $stack.Push($sub)
                }
            }
            foreach ($file in [System.IO.Directory]::EnumerateFiles($dir)) {
                if ($codeSuffixes -contains [System.IO.Path]::GetExtension($file).ToLowerInvariant()) {
                    $time = [System.IO.File]::GetLastWriteTimeUtc($file)
                    if ($time -gt $newest) { $newest = $time }
                }
            }
        } catch {
            # Unreadable folder: skip it, like the hub does
        }
    }
    return $newest
}

function Test-Stale {
    if (-not (Test-Path $exe)) { return $true }
    return (Get-NewestSourceTime $root) -gt (Get-Item $exe).LastWriteTimeUtc
}

if (-not (Test-Stale)) { exit 0 }

# One build at a time. A second start while this one builds (a double click on the deck,
# the hub's auto-launch next to a manual start) waits here and then finds the build current.
$mutex = New-Object System.Threading.Mutex($false, "Local\VRCFaceTracking518-Build")
try { [void]$mutex.WaitOne() } catch [System.Threading.AbandonedMutexException] { }
try {
    if (-not (Test-Stale)) { exit 0 }

    Write-Host "VRCFaceTracking 518: the source is newer than the Release build." -ForegroundColor Cyan
    Write-Host "Rebuilding before it starts (about a minute)..." -ForegroundColor Cyan

    # Anything still running out of this repo holds its files open and would make the
    # build fail half-way. Watchdog first, so it can't relaunch the old build mid-build.
    $stoppedApp = $false
    foreach ($name in "VRCFaceTracking.Watchdog", "VRCFaceTracking", "VRCFaceTracking.Lite",
                      "VRCFaceTracking.ModuleProcess") {
        foreach ($p in @(Get-Process -Name $name -ErrorAction SilentlyContinue)) {
            $path = $null
            try { $path = $p.Path } catch { }
            if ($path -and $path.StartsWith($root + "\", [System.StringComparison]::OrdinalIgnoreCase)) {
                Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
                if ($name -eq "VRCFaceTracking") { $stoppedApp = $true }
            }
        }
    }
    if ($stoppedApp) {
        # A force-stopped app leaves the crash reporter's run marker behind, which the next
        # start would report as a hard crash. This stop was deliberate (same as update.bat).
        Start-Sleep -Milliseconds 500
        $marker = Join-Path ([Environment]::GetFolderPath("MyDocuments")) "VRCFaceTracking\crashes\running.marker"
        Remove-Item -Force -ErrorAction SilentlyContinue $marker
    }

    # Continue, not Stop: Windows PowerShell turns a native tool's stderr into error
    # records whenever output is captured, and Stop would end the script on a warning.
    $ErrorActionPreference = "Continue"
    & dotnet build (Join-Path $root "VRCFaceTracking.sln") -c Release -p:Platform=x64 `
        -p:WindowsPackageType=None -nologo -v:minimal
    if ($LASTEXITCODE -ne 0) { exit 1 }

    # An incremental build with nothing to compile leaves the exe's date alone, and the hub
    # and deck would then keep picking run.bat, and this check, on every start. Stamp the
    # build as current.
    $now = Get-Date
    foreach ($name in "VRCFaceTracking.exe", "VRCFaceTracking.Lite.exe") {
        $file = Join-Path $exeDir $name
        if (Test-Path $file) { (Get-Item $file).LastWriteTime = $now }
    }
    Write-Host "Build OK." -ForegroundColor Green
    exit 0
} finally {
    $mutex.ReleaseMutex()
    $mutex.Dispose()
}
