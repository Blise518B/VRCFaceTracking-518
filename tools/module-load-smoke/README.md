# Module loading regression checks

Requires the .NET 10 SDK on Windows. Build the app first:

```powershell
dotnet build VRCFaceTracking.sln -c Release -p:Platform=x64 -p:WindowsPackageType=None
$sandbox = (Resolve-Path 'VRCFaceTracking.ModuleProcess/bin/x64/Release/net10.0-windows10.0.22621.0/win-x64/VRCFaceTracking.ModuleProcess.exe').Path
dotnet run --project tools/module-load-smoke -c Release -p:Platform=x64 -- $sandbox 'C:/path/to/module.dll'
```

Additional DLL arguments check multiple module builds, for example the original
QFTPlus SDK-6 module and a legacy SDK-5 build. No modules are installed or modified.
These checks construct the modules, query their Supported property and resolve
their direct Core/SDK member references. They also launch an isolated sandbox and
complete its handshake/capability request over loopback. They do not call module Initialize,
Update or Teardown, which can start hardware or external runtimes.

The checks also run an isolated sandbox with a non-module DLL, assert its prompt
load-failure exit and that the host turns its output into a specific Output line,
exercise supervisor/hot-plug suppression and manual reset, check that a missing
module file is a load failure, and verify the missing-arguments exit does not crash
its shutdown handler.

Hardware tracking and a complete VR session still need a separate manual test.
