@echo off
setlocal
echo Rebuilding VRCFaceTracking-518 (Release x64)...
dotnet build "%~dp0VRCFaceTracking.sln" -c Release -p:Platform=x64 -p:WindowsPackageType=None
if errorlevel 1 (
    echo.
    echo BUILD FAILED - see errors above.
    pause
    exit /b 1
)
echo.
echo Build OK. Use run.bat to launch.
pause
