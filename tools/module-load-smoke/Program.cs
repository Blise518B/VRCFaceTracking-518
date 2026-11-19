using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using Microsoft.Extensions.Logging;
using VRCFaceTracking;
using VRCFaceTracking.Core.Contracts.Services;
using VRCFaceTracking.Core.Library;
using VRCFaceTracking.Core.Sandboxing.IPC;
using VRCFaceTracking.ModuleProcess;

// No module Initialize/Update/Teardown methods run here: those may start hardware,
// external runtimes or native SDKs. This checks binding and isolated failure paths.
if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: <sandbox-exe> <module-dll> [additional-module-dll ...]");
    return 1;
}

using var factory = LoggerFactory.Create(builder => builder.AddConsole());
var logger = factory.CreateLogger<ModuleProcessMain>();
foreach (var path in args.Skip(1))
{
    var module = new ModuleAssembly(logger, factory, Path.GetFullPath(path));
    Check(module.TryLoadAssembly(), $"Load {path}");
    Check(module.Loaded && module.TrackingModule != null, "Successful load has a usable module");
    _ = module.TrackingModule!.Supported;
    var context = AssemblyLoadContext.GetLoadContext(module.Assembly)!;
    foreach (var reference in module.Assembly.GetReferencedAssemblies()
                 .Where(r => r.Name is "VRCFaceTracking.SDK" or "VRCFaceTracking.Core"))
    {
        var expected = reference.Name == "VRCFaceTracking.SDK"
            ? typeof(ExtTrackingModule).Assembly : typeof(UnifiedTracking).Assembly;
        Check(ReferenceEquals(context.LoadFromAssemblyName(reference), expected),
            $"{reference} shares the host assembly and static state");
    }

    using var stream = File.OpenRead(path);
    using var pe = new PEReader(stream);
    var metadata = pe.GetMetadataReader();
    var checkedMembers = 0;
    foreach (var handle in metadata.MemberReferences)
    {
        var member = metadata.GetMemberReference(handle);
        if (member.Parent.Kind != HandleKind.TypeReference) continue;
        var parent = metadata.GetTypeReference((TypeReferenceHandle)member.Parent);
        if (parent.ResolutionScope.Kind != HandleKind.AssemblyReference) continue;
        var reference = metadata.GetAssemblyReference((AssemblyReferenceHandle)parent.ResolutionScope);
        if (metadata.GetString(reference.Name) is not ("VRCFaceTracking.SDK" or "VRCFaceTracking.Core")) continue;
        Check(module.Assembly.ManifestModule.ResolveMember(MetadataTokens.GetToken(handle)) != null,
            $"Resolve {metadata.GetString(parent.Name)}.{metadata.GetString(member.Name)}");
        checkedMembers++;
    }
    await CheckSandboxHandshake(args[0], path, module.TrackingModule.Supported);
    Console.WriteLine($"PASS: {Path.GetFileName(path)} loaded; {checkedMembers} host API references resolved; sandbox capabilities received.");
}

// A managed DLL that isn't a tracking module used to proceed to a handshake and
// time out after 60 seconds. It must now exit promptly with the load-failure code.
var invalidModule = typeof(ModuleAssembly).Assembly.Location;
var invalid = new ModuleAssembly(logger, factory, invalidModule);
Check(!invalid.TryLoadAssembly() && !invalid.Loaded && invalid.TrackingModule == null,
    "A non-module DLL cannot report success");
using var failed = StartSandbox(args[0], "--port", "1", "--module-path", invalidModule);
var outputTask = failed.StandardOutput.ReadToEndAsync();
var errorTask = failed.StandardError.ReadToEndAsync();
try
{
    Check(failed.WaitForExit(10_000), "Invalid module exits within 10 seconds");
    Check(failed.ExitCode == ModuleProcessExitCodes.MODULE_LOAD_FAILED, "Load-failure exit code survives command dispatch");
    var failedOutput = await outputTask;
    Check(failedOutput.Contains("Module load failed"), "Load failure is explained in process output");
    Check(!(await errorTask).Contains("NullReferenceException"), "Early shutdown does not dereference uninitialized state");

    // The host puts the sandbox's own explanation into Output, since the sandbox exits
    // before its IPC log lines can be sent.
    var detail = (string)typeof(UnifiedLibManager)
        .GetMethod("DescribeLoadFailure", BindingFlags.Static | BindingFlags.NonPublic)!
        .Invoke(null, [failedOutput])!;
    Check(detail.Contains("does not export an ExtTrackingModule implementation") && !detail.Contains("Module load failed;"),
        $"Output gets the specific load error, not the generic line (got: {detail})");

    // The constructor starts only a background supervisor. With no module data or
    // activation service, it cannot discover/start modules or change user settings.
    var sandboxCopy = Path.Combine(AppContext.BaseDirectory, "VRCFaceTracking.ModuleProcess.exe");
    if (!File.Exists(sandboxCopy)) File.Copy(args[0], sandboxCopy);
    var manager = new UnifiedLibManager(factory, new ImmediateDispatcher(), null!, null!, null!, null!);
    typeof(UnifiedLibManager).GetMethod("HandleDeadModule", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(manager, [new ModuleRuntimeInfo
        {
            Process = failed,
            SandboxModulePath = invalidModule,
            ModuleClassName = "Invalid smoke module",
            ModuleInformation = new(),
        }]);
    Check(manager.GetRunningModulePaths().Contains(invalidModule), "Hot-plug excludes a module that failed to load");
    Check(manager.LoadedModulesMetadata.Any(m => m.IsDormant && m.StatusNote.Contains("could not load")),
        "Load failure stays visible with its reason");
    manager.TeardownAllAndResetAsync();
    Check(!manager.GetRunningModulePaths().Contains(invalidModule), "Manual reinitialization permits retrying a repaired module");
}
finally
{
    if (!failed.HasExited) failed.Kill(entireProcessTree: true);
}

using var noArgs = StartSandbox(args[0]);
var noArgsOutput = noArgs.StandardOutput.ReadToEndAsync();
var noArgsError = noArgs.StandardError.ReadToEndAsync();
try
{
    Check(noArgs.WaitForExit(10_000), "Missing arguments exit promptly");
    Check(noArgs.ExitCode == ModuleProcessExitCodes.INVALID_ARGS, "Missing arguments retain the argument-error exit code");
    Check(!(await noArgsError).Contains("NullReferenceException"), "Missing arguments do not crash the exit handler");
    await noArgsOutput;
}
finally
{
    if (!noArgs.HasExited) noArgs.Kill(entireProcessTree: true);
}

Console.WriteLine("PASS: module binding, API references, prompt failure, restart suppression, manual retry and early shutdown.");
return 0;

static async Task CheckSandboxHandshake(string exe, string modulePath, (bool Eye, bool Face) supported)
{
    using var host = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    var port = ((IPEndPoint)host.Client.LocalEndPoint!).Port;
    using var child = StartSandbox(exe, "--port", port.ToString(), "--module-path", Path.GetFullPath(modulePath),
        "--parent-pid", Environment.ProcessId.ToString());
    var output = child.StandardOutput.ReadToEndAsync();
    var error = child.StandardError.ReadToEndAsync();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    try
    {
        while (true)
        {
            var packet = await host.ReceiveAsync(timeout.Token);
            if (packet.Buffer.Length < 8) continue;
            switch ((IpcPacket.PacketType)BitConverter.ToUInt32(packet.Buffer, 4))
            {
                case IpcPacket.PacketType.Handshake:
                    host.Send(packet.Buffer, packet.RemoteEndPoint);
                    host.Send(new EventInitGetSupported().GetBytes(), packet.RemoteEndPoint);
                    break;
                case IpcPacket.PacketType.ReplyGetSupported:
                    var reply = new ReplySupportedPacket();
                    reply.Decode(packet.Buffer);
                    Check((reply.eyeAvailable, reply.expressionAvailable) == supported,
                        "Real sandbox replies with the module's capabilities");
                    return;
            }
        }
    }
    finally
    {
        // No Initialize packet is sent, so shutdown must not call module Teardown.
        // Terminate only the sandbox this test started, then drain its private pipes.
        if (!child.HasExited) child.Kill(entireProcessTree: true);
        await child.WaitForExitAsync();
        await output;
        await error;
    }
}

static Process StartSandbox(string exe, params string[] arguments)
{
    var info = new ProcessStartInfo(Path.GetFullPath(exe))
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        WindowStyle = ProcessWindowStyle.Hidden,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
    foreach (var argument in arguments) info.ArgumentList.Add(argument);
    return Process.Start(info)!;
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class ImmediateDispatcher : IDispatcherService
{
    public void Run(Action action) => action();
}
