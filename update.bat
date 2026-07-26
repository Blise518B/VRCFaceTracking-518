@echo off
setlocal
echo Stopping VRCFaceTracking to rebuild (face tracking pauses for ~30s)...

rem Kill the watchdog FIRST so it doesn't relaunch the old build mid-rebuild
taskkill /IM VRCFaceTracking.Watchdog.exe /F >nul 2>&1
taskkill /IM VRCFaceTracking.exe /F >nul 2>&1
taskkill /IM VRCFaceTracking.ModuleProcess.exe /F >nul 2>&1

rem We just force-killed the app, which leaves the crash reporter's run marker behind
rem and would be reported as a hard crash on the next start. Clear it - this shutdown
rem was deliberate. (Documents may be redirected to OneDrive, so resolve it properly.)
powershell -NoProfile -Command "$m = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'VRCFaceTracking\crashes\running.marker'; Remove-Item -Force -ErrorAction SilentlyContinue $m" >nul 2>&1

echo Building latest version...
dotnet build "%~dp0VRCFaceTracking.sln" -c Release -p:Platform=x64 -p:WindowsPackageType=None
if errorlevel 1 (
    echo.
    echo BUILD FAILED - see errors above. Your old build was not replaced.
    echo You can relaunch the previous version with run.bat.
    pause
    exit /b 1
)

echo Build OK - relaunching...
set "EXEDIR=%~dp0VRCFaceTracking\bin\x64\Release\net10.0-windows10.0.22621.0\win-x64"
start "" /d "%EXEDIR%" "%EXEDIR%\VRCFaceTracking.exe"
exit /b 0
