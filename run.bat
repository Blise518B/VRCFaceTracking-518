@echo off
setlocal
rem Starts the newest VRCFaceTracking 518. When the source has changed since the Release
rem build was made, tools\build-if-stale.ps1 rebuilds it first. The 518 Hub and Deck518
rem start this instead of the exe whenever the source is newer than the build.
set "EXEDIR=%~dp0VRCFaceTracking\bin\x64\Release\net10.0-windows10.0.22621.0\win-x64"
set "EXE=%EXEDIR%\VRCFaceTracking.exe"

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\build-if-stale.ps1"
set "BUILD=%errorlevel%"

if not exist "%EXE%" (
    echo.
    echo BUILD FAILED and there is no older Release build to fall back on - see the errors above.
    pause
    exit /b 1
)

rem /d sets the working directory - the app resolves ModuleProcess.exe relative to it
start "" /d "%EXEDIR%" "%EXE%" %*

if not "%BUILD%"=="0" (
    echo.
    echo REBUILD FAILED - see the errors above. The previous build was started instead.
    pause
)
exit /b 0
