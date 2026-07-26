@echo off
setlocal
set "EXEDIR=%~dp0VRCFaceTracking\bin\x64\Release\net10.0-windows10.0.22621.0\win-x64"
set "EXE=%EXEDIR%\VRCFaceTracking.exe"

if not exist "%EXE%" (
    echo No Release build found - building first, this takes about half a minute...
    dotnet build "%~dp0VRCFaceTracking.sln" -c Release -p:Platform=x64 -p:WindowsPackageType=None
    if errorlevel 1 (
        echo.
        echo BUILD FAILED - see errors above.
        pause
        exit /b 1
    )
)

rem /d sets the working directory - the app resolves ModuleProcess.exe relative to it
start "" /d "%EXEDIR%" "%EXE%"
exit /b 0
