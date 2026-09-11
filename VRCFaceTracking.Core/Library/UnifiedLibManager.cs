using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using VRCFaceTracking.Core.Contracts.Services;
using VRCFaceTracking.Core.Sandboxing;
using VRCFaceTracking.Core.Sandboxing.IPC;
using VRCFaceTracking.Core.Services;

namespace VRCFaceTracking.Core.Library;

public class UnifiedLibManager : ILibManager
{
    #region Logger
    private readonly ILogger<UnifiedLibManager> _logger;
    private readonly ILogger _moduleLogger;
    private readonly ILoggerFactory _loggerFactory;
    #endregion

    #region Observables
    public ObservableCollection<ModuleMetadataInternal> LoadedModulesMetadata { get; set; }
    private bool _hasInitializedAtLeastOneModule = false;
    private readonly IDispatcherService _dispatcherService;
    #endregion

    #region Statuses
    public static ModuleState EyeStatus { get; private set; }
    public static ModuleState ExpressionStatus { get; private set; }
    #endregion

    #region Modules

    private List<Assembly> AvailableModules { get; set; }
    private readonly List<ModuleRuntimeInfo> _moduleThreads = new();
    private readonly object _moduleThreadsLock = new();
    private readonly IModuleDataService _moduleDataService;

    private string _sandboxProcessPath { get; set; }
    private List<ModuleRuntimeInfo> AvailableSandboxModules = new ();
    #endregion

    #region Thread
    private Thread _initializeWorker;
    private static VrcftSandboxServer _sandboxServer;
    #endregion

    #region Module crash supervision
    private Thread _supervisorThread;
    private volatile bool _suppressAutoRestart;
    private readonly Dictionary<string, List<DateTime>> _restartHistory = new();
    private const int MaxRestartsInWindow = 5;
    private static readonly TimeSpan RestartWindow = TimeSpan.FromMinutes(5);
    private const int SupervisorPollIntervalMs = 2000;
    private int _hotPlugTicks;
    private const int HotPlugCheckEveryTicks = 5;   // ~10s between source re-checks
    #endregion

    private readonly AutoRestartService _autoRestartService;
    private readonly CrashReportService _crashReportService;
    private readonly ModuleActivationService _moduleActivationService;

    public UnifiedLibManager(ILoggerFactory factory, IDispatcherService dispatcherService, IModuleDataService moduleDataService, AutoRestartService autoRestartService, CrashReportService crashReportService, ModuleActivationService moduleActivationService)
    {
        _crashReportService = crashReportService;
        _moduleActivationService = moduleActivationService;
        _loggerFactory = factory;
        _logger = factory.CreateLogger<UnifiedLibManager>();
        _moduleLogger = factory.CreateLogger("\0VRCFT\0");
        _dispatcherService = dispatcherService;
        _moduleDataService = moduleDataService;
        _autoRestartService = autoRestartService;

        LoadedModulesMetadata = new ObservableCollection<ModuleMetadataInternal>();
        // Resolve next to our own binaries, NOT the working directory: SteamVR
        // (and anything else) can launch us with an arbitrary cwd, which made
        // the Lite exe die at startup with "failed to find sandbox process".
        _sandboxProcessPath = Path.Combine(AppContext.BaseDirectory,
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "VRCFaceTracking.ModuleProcess.exe" : "VRCFaceTracking.ModuleProcess");
        if ( !File.Exists(_sandboxProcessPath) )
        {
            // @TODO: Better error handling
            throw new FileNotFoundException($"Failed to find sandbox process at \"{_sandboxProcessPath}\"!");
        }

        // @TODO: Kill any lingering sub-modules to eliminate any conflicts

        StartModuleSupervisor();
    }

    /// <summary>
    /// Starts a background thread that watches for sandbox module processes dying unexpectedly
    /// and respawns them, so a single crashing module doesn't silently kill tracking.
    /// </summary>
    private void StartModuleSupervisor()
    {
        _supervisorThread = new Thread(() =>
        {
            while ( true )
            {
                Thread.Sleep(SupervisorPollIntervalMs);
                try
                {
                    SuperviseModules();
                }
                catch ( Exception e )
                {
                    _logger.LogError("Module supervisor error: {message}", e.Message);
                }

                try
                {
                    // Sources appear mid-session: someone starts iFacialMocap on their phone,
                    // or plugs a tracker in, long after VRCFT launched. Without this the module
                    // stays dormant until a manual re-init, which looks like it doesn't work.
                    if ( ++_hotPlugTicks >= HotPlugCheckEveryTicks )
                    {
                        _hotPlugTicks = 0;
                        StartModulesWhoseSourceAppeared();
                    }
                }
                catch ( Exception e )
                {
                    _logger.LogDebug("Source hot-plug check failed: {message}", e.Message);
                }
            }
        });
        _supervisorThread.IsBackground = true;
        _supervisorThread.Name = "ModuleCrashSupervisor";
        _supervisorThread.Start();
    }

    private void SuperviseModules()
    {
        if ( _suppressAutoRestart || !( _autoRestartService?.Enabled ?? true ) )
        {
            return;
        }

        List<ModuleRuntimeInfo> deadModules = null;
        lock ( AvailableSandboxModules )
        {
            foreach ( var module in AvailableSandboxModules )
            {
                bool hasExited;
                try
                {
                    hasExited = module.Process?.HasExited ?? false;
                }
                catch ( Exception )
                {
                    // Process handle may be inaccessible (e.g. elevated process); leave it alone
                    continue;
                }

                if ( module.IsActive && hasExited )
                {
                    ( deadModules ??= new() ).Add(module);
                }
            }

            if ( deadModules == null )
            {
                return;
            }

            foreach ( var module in deadModules )
            {
                AvailableSandboxModules.Remove(module);
            }
        }

        foreach ( var dead in deadModules )
        {
            if ( _suppressAutoRestart )
            {
                return;
            }
            HandleDeadModule(dead);
        }
    }

    private void HandleDeadModule(ModuleRuntimeInfo module)
    {
        var moduleName = module.ModuleInformation?.Name ?? module.ModuleClassName ?? "Unknown";
        var exitCode = 0;
        try
        {
            exitCode = module.Process?.ExitCode ?? 0;
        }
        catch ( Exception ) { }

        // Stop the update pump that was feeding the dead process
        module.UpdateCancellationToken?.Cancel();
        lock ( _moduleThreadsLock )
        {
            _moduleThreads.Remove(module);
        }

        // Free up the eye/expression slots the dead module was occupying so a
        // respawned instance (or another module) can claim them again
        if ( module.ModuleInformation?.UsingEye ?? false )
        {
            EyeStatus = ModuleState.Uninitialized;
        }
        if ( module.ModuleInformation?.UsingExpression ?? false )
        {
            ExpressionStatus = ModuleState.Uninitialized;
        }

        if ( exitCode == 0 )
        {
            // Graceful self-exit (e.g. failed init) - don't resurrect
            _logger.LogInformation("Module {module} (PID {pid}) exited cleanly. Not restarting it.", moduleName, module.SandboxProcessPID);
            return;
        }

        _logger.LogWarning("Module {module} (PID {pid}) died unexpectedly with exit code {code}.", moduleName, module.SandboxProcessPID, exitCode);
        _crashReportService?.ReportModuleCrash(moduleName, exitCode, module.GetOutputTail());

        // A module whose source went away is not crashing, it's finished: iFacialMocap for
        // instance gives up (non-zero) after waiting two minutes for a phone that isn't
        // streaming. Restarting into a source that isn't there just loops until the cap,
        // so re-check first and go dormant instead - that IS the deactivation trigger.
        if ( !SourceStillPresentFor(module.SandboxModulePath, out var goneReason) )
        {
            _logger.LogInformation(
                "Not restarting {module}: {reason}. It will start again on its own once the source is back.",
                moduleName, goneReason);
            MarkModuleDormant(moduleName, goneReason, module.SandboxModulePath);
            return;
        }

        if ( !CanRestart(module.SandboxModulePath) )
        {
            _logger.LogError("Module {module} crashed {count} times within {window} minutes. Giving up on restarting it.",
                moduleName, MaxRestartsInWindow, RestartWindow.TotalMinutes);
            if ( module.ModuleInformation != null )
            {
                _dispatcherService.Run(() => module.ModuleInformation.Active = false);
            }
            return;
        }

        _logger.LogInformation("Restarting module {module}...", moduleName);
        _autoRestartService?.RecordModuleRestart(moduleName);
        InitialiseSandboxesBaseOnPaths(new[] { module.SandboxModulePath });
    }

    /// <summary>
    /// Starts any installed module that isn't running but whose tracking source has since
    /// appeared - the counterpart to going dormant when a source disappears. Together they
    /// mean the user just starts and stops the source itself, and nothing here needs touching.
    /// </summary>
    private void StartModulesWhoseSourceAppeared()
    {
        if ( _suppressAutoRestart || _moduleActivationService == null || !_moduleActivationService.GatingEnabled )
        {
            return;
        }

        var modules = _moduleDataService.GetInstalledModules()
            .Concat(_moduleDataService.GetLegacyModules())
            .ToList();
        if ( modules.Count == 0 )
        {
            return;
        }

        var runningPaths = GetRunningModulePaths();

        foreach ( var decision in _moduleActivationService.Evaluate(modules).Where(d => d.WillStart) )
        {
            if ( string.IsNullOrEmpty(decision.DllName) )
            {
                continue;
            }

            var alreadyRunning = runningPaths.Any(p =>
                p.EndsWith(decision.DllName, StringComparison.OrdinalIgnoreCase));
            if ( alreadyRunning )
            {
                continue;
            }

            // CanRestart both checks the cap AND records the attempt, so a module that keeps
            // dying can't be revived here every ten seconds forever - that would defeat the
            // giving-up logic entirely.
            var path = modules
                .Select(m => m.AssemblyLoadPath)
                .FirstOrDefault(p => !string.IsNullOrEmpty(p) &&
                                     p.EndsWith(decision.DllName, StringComparison.OrdinalIgnoreCase));
            if ( string.IsNullOrEmpty(path) || !CanRestart(path) )
            {
                continue;
            }

            _logger.LogInformation("{module}: {reason}. Starting it now.", decision.DisplayName, decision.Reason);
            InitialiseSandboxesBaseOnPaths(new[] { path });
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> GetRunningModulePaths()
    {
        lock ( AvailableSandboxModules )
        {
            return AvailableSandboxModules
                .Where(m => m.Process is { HasExited: false } && !string.IsNullOrEmpty(m.SandboxModulePath))
                .Select(m => m.SandboxModulePath)
                .ToList();
        }
    }

    /// <summary>
    /// Re-runs the activation decision for a module that just died, so a vanished source is
    /// told apart from a real crash. Unknown modules always pass: never withhold a restart
    /// from a module we have no opinion about.
    /// </summary>
    private bool SourceStillPresentFor(string? modulePath, out string reason)
    {
        reason = string.Empty;
        if ( _moduleActivationService == null || string.IsNullOrEmpty(modulePath) )
        {
            return true;
        }

        try
        {
            var modules = _moduleDataService.GetInstalledModules()
                .Concat(_moduleDataService.GetLegacyModules())
                .ToList();

            // Force a fresh probe. The cached sweep is up to 5s old, and for a stream-only
            // source it was taken while THIS module still held the port - i.e. it reports
            // "in use, so it's live", which would wave every restart through. The port only
            // becomes probe-able the moment the module dies, which is exactly now.
            _moduleActivationService.Detector.Scan(force: true);

            var decision = _moduleActivationService.Evaluate(modules).FirstOrDefault(d =>
                !string.IsNullOrEmpty(d.DllName) &&
                modulePath.EndsWith(d.DllName, StringComparison.OrdinalIgnoreCase));

            if ( decision == null || decision.WillStart )
            {
                return true;
            }

            reason = decision.Reason;
            return false;
        }
        catch ( Exception e )
        {
            _logger.LogDebug("Could not re-check the source for {path}: {message}", modulePath, e.Message);
            return true;
        }
    }

    /// <summary>
    /// Drops the dormant placeholder for a module that has just started. Matched on the dll,
    /// the one identity both sides genuinely share.
    /// </summary>
    private void RemoveDormantCardFor(string? modulePath)
    {
        if ( string.IsNullOrEmpty(modulePath) )
        {
            return;
        }

        try
        {
            var dll = Path.GetFileName(modulePath);
            for ( var i = LoadedModulesMetadata.Count - 1; i >= 0; i-- )
            {
                var card = LoadedModulesMetadata[i];
                if ( card.IsDormant && !string.IsNullOrEmpty(card.SourceDll) &&
                     string.Equals(card.SourceDll, dll, StringComparison.OrdinalIgnoreCase) )
                {
                    LoadedModulesMetadata.RemoveAt(i);
                }
            }
        }
        catch ( Exception )
        {
            // Cosmetic bookkeeping; never let it affect module startup
        }
    }

    /// <summary>Letters and digits only, lowercased - for comparing module display names.</summary>
    private static string Simplify(string? name) =>
        string.IsNullOrEmpty(name)
            ? string.Empty
            : new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    /// <summary>
    /// Shows a module as present-but-idle with the reason. A module that just disappears from
    /// the list reads as uninstalled, which is exactly the confusion this avoids.
    /// </summary>
    private void MarkModuleDormant(string moduleName, string reason, string? modulePath = null)
    {
        _dispatcherService.Run(() =>
        {
            try
            {
                var dll = string.IsNullOrEmpty(modulePath) ? null : Path.GetFileName(modulePath);

                // Prefer the dll - the only identity a dormant card and a live module reliably
                // share. Names differ between the two ("ETVR Tracking Module" from the manifest
                // vs "ETVR Eye Tracking module" at runtime), so the name compare is a fallback
                // for callers that have no path to give.
                var existing = dll == null
                    ? null
                    : LoadedModulesMetadata.FirstOrDefault(m =>
                        string.Equals(m.SourceDll, dll, StringComparison.OrdinalIgnoreCase));

                if ( existing == null )
                {
                    var key = Simplify(moduleName);
                    existing = LoadedModulesMetadata.FirstOrDefault(m =>
                    {
                        var name = Simplify(m.Name);
                        return name.Length > 0 && key.Length > 0 &&
                               (name.Contains(key, StringComparison.Ordinal) ||
                                key.Contains(name, StringComparison.Ordinal));
                    });
                }

                if ( existing != null )
                {
                    existing.Active = false;
                    existing.IsDormant = true;
                    existing.StatusNote = reason;
                    existing.SourceDll ??= dll;
                    return;
                }

                LoadedModulesMetadata.Add(new ModuleMetadataInternal
                {
                    Name = moduleName,
                    Active = false,
                    IsDormant = true,
                    StatusNote = reason,
                    SourceDll = dll,
                    StaticImages = new List<Stream>(),
                });
            }
            catch ( Exception )
            {
                // Cosmetic bookkeeping; never let it affect the supervisor
            }
        });
    }

    private bool CanRestart(string modulePath)
    {
        if ( string.IsNullOrEmpty(modulePath) || !File.Exists(modulePath) )
        {
            return false;
        }

        lock ( _restartHistory )
        {
            if ( !_restartHistory.TryGetValue(modulePath, out var history) )
            {
                history = new List<DateTime>();
                _restartHistory[modulePath] = history;
            }

            var cutoff = DateTime.UtcNow - RestartWindow;
            history.RemoveAll(t => t < cutoff);

            if ( history.Count >= MaxRestartsInWindow )
            {
                return false;
            }

            history.Add(DateTime.UtcNow);
            return true;
        }
    }

    public void Initialize()
    {
        LoadedModulesMetadata.Clear();
        LoadedModulesMetadata.Add(new ModuleMetadataInternal
        { 
            Active = false,
            Name = "Initializing Modules..."
        });

        // Spawn sandbox server if it's null
        if (_sandboxServer == null )
        {
            // @TODO: Figure out an elegant way to ask the GUI for the ports the user assigned to the OSCTarget.
            int[] reservedPorts = new int[2] { 9000, 9001 };
            _sandboxServer = new VrcftSandboxServer(_loggerFactory, reservedPorts);
            _sandboxServer.OnPacketReceived += (in IpcPacket packet, in int port) => HandleModulePacket(packet, port);
        }

        // Start Initialization.
        //
        // The body is wrapped because an unhandled exception on a plain Thread kills the
        // whole PROCESS - a failed teardown or a module list that changed mid-iteration
        // used to take VRCFaceTracking down rather than just failing to start a module.
        _initializeWorker = new Thread(() =>
        {
            try
            {
                InitializeModulesOnWorker();
            }
            catch ( Exception e )
            {
                _logger.LogError("Module initialization failed: {message}", e.Message);
            }
        });
        _initializeWorker.IsBackground = true;
        _logger.LogInformation("Starting initialization tracking");
        _initializeWorker.Start();
    }

    /// <summary>
    /// Tears down whatever is running, works out which modules should start for the
    /// hardware that is actually live, and spawns them. Runs on the initialize worker.
    /// </summary>
    private void InitializeModulesOnWorker()
    {
        // Kill lingering threads
        TeardownAllAndResetAsync();

        // That teardown just freed every port our module processes held, but the detector's
        // sweep cache (up to 5 s old) still says "UDP 49983 in use". With no module of ours
        // owning the port any more, that stale verdict reads as "not detected" - so a phone
        // streaming right now was skipped here and only picked up by the next hot-plug tick,
        // ten seconds of no tracking later (measured live 2026-09-11). Look again, fresh.
        _moduleActivationService.Detector.Scan(force: true);

        // Find all modules, then start only the ones whose tracking source is actually
        // live. Without this every installed module spawns a process and races to claim
        // the eye/expression slots, which wastes startup time and invites conflicts.
        var modules = _moduleDataService.GetInstalledModules().Concat(_moduleDataService.GetLegacyModules()).ToList();
        var modulePaths = _moduleActivationService.FilterStartablePaths(modules);

        if ( modulePaths.Count < modules.Count )
        {
            _logger.LogInformation(
                "Source detection: starting {started} of {total} installed modules. See the Sources page for why.",
                modulePaths.Count, modules.Count);

            // List the skipped ones greyed out instead of dropping them: a module that
            // silently disappears looks uninstalled rather than idle.
            var skipped = _moduleActivationService.Evaluate(modules).Where(d => !d.WillStart).ToList();
            _dispatcherService.Run(() =>
            {
                foreach ( var decision in skipped )
                {
                    LoadedModulesMetadata.Add(new ModuleMetadataInternal
                    {
                        Name = decision.DisplayName,
                        Active = false,
                        IsDormant = true,
                        StatusNote = decision.Reason,
                        SourceDll = decision.DllName,
                        StaticImages = new List<Stream>(),
                    });
                }
            });
        }

        // Load all modules
        lock ( AvailableSandboxModules )
        {
            AvailableSandboxModules.Clear();
        }
        InitialiseSandboxesBaseOnPaths(modulePaths.ToArray());

        int started;
        lock ( AvailableSandboxModules )
        {
            started = AvailableSandboxModules.Count;
        }

        if ( started > 0 )
        {
            _logger.LogDebug("Initializing requested runtimes...");
            return;
        }

        _dispatcherService.Run(() =>
        {
            LoadedModulesMetadata.Clear();
            LoadedModulesMetadata.Add(new ModuleMetadataInternal
            {
                Active = false,
                Name = "No Modules Loaded"
            });
        });
        _logger.LogWarning("No modules loaded.");
    }

    #region Module IPC packet handling

    /// <summary>
    /// The module registered on a sandbox port, or null when none is.
    /// </summary>
    /// <remarks>
    /// A null result is routine, not exceptional: a module that was just torn down keeps
    /// sending for a moment after its entry is gone, and a hot-swap clears the whole list
    /// while the outgoing module is still streaming at 100Hz. The code this replaced
    /// searched for an index, left it at the -1 "not found" sentinel, and then indexed
    /// the list with it - see <see cref="HandleModulePacket"/> for what that cost.
    /// </remarks>
    private ModuleRuntimeInfo? ModuleOnPort(int port)
    {
        lock ( AvailableSandboxModules )
        {
            foreach ( var module in AvailableSandboxModules )
            {
                if ( module.SandboxProcessPort == port )
                {
                    return module;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Entry point for every packet a module sandbox sends us.
    /// </summary>
    /// <remarks>
    /// Must never throw. This runs on the UDP receive loop, and an exception escaping it
    /// faults that loop's task, which stops IPC reception for EVERY module permanently
    /// and silently - tracking simply freezes until each module hits its 60s no-packet
    /// timeout and gets killed. The loop now has a catch-all of its own as well; both
    /// layers are deliberate, because this is the failure that has no symptom to debug.
    /// </remarks>
    private void HandleModulePacket(in IpcPacket packet, int port)
    {
        try
        {
            switch ( packet.GetPacketType() )
            {
                case IpcPacket.PacketType.Handshake:
                    HandleHandshake((HandshakePacket) packet, port);
                    break;

                case IpcPacket.PacketType.EventLog:
                    var logPacket = (EventLogPacket) packet;
                    _moduleLogger.Log(logPacket.LogLevel, logPacket.Message);
                    break;

                case IpcPacket.PacketType.ReplyGetSupported:
                    HandleReplySupported((ReplySupportedPacket) packet, port);
                    break;

                case IpcPacket.PacketType.ReplyInit:
                    HandleReplyInit((ReplyInitPacket) packet, port);
                    break;

                case IpcPacket.PacketType.ReplyUpdate:
                    HandleReplyUpdate((ReplyUpdatePacket) packet, port);
                    break;
            }
        }
        catch ( Exception e )
        {
            _logger.LogWarning("Failed to handle a {type} packet from module port {port}: {message}",
                packet.GetPacketType(), port, e.Message);
        }
    }

    /// <summary>
    /// A sandbox process announcing itself. Either it is one we spawned (bind the port it
    /// picked to our record of it) or one we have no record of, which is registered as-is.
    /// </summary>
    private void HandleHandshake(HandshakePacket packet, int port)
    {
        lock ( AvailableSandboxModules )
        {
            foreach ( var known in AvailableSandboxModules )
            {
                if ( known.SandboxProcessPID != packet.PID )
                {
                    continue;
                }

                known.SandboxProcessPort = port;
                _logger.LogInformation("Initializing {module}...", known.ModuleClassName);
                AttemptSandboxedModuleInitialize(known);
                return;
            }

            var runtimeInfo = new ModuleRuntimeInfo
            {
                SandboxProcessPID   = packet.PID,
                SandboxProcessPort  = port,
                SandboxModulePath   = packet.ModulePath,
                IsActive            = true,
                Process             = Process.GetProcessById(packet.PID),
                ModuleClassName     = Path.GetFileNameWithoutExtension(packet.ModulePath),
                ModuleInformation   = new (),
                EventBus            = new (),
            };
            AvailableSandboxModules.Add(runtimeInfo);

            _logger.LogInformation("Initializing {module}...", runtimeInfo.ModuleClassName);
            AttemptSandboxedModuleInitialize(runtimeInfo);
        }
    }

    /// <summary>
    /// The module has told us which tracking types it can do. Narrow our expectation to
    /// the intersection, then ask it to initialize whichever slots are still unclaimed.
    /// </summary>
    private void HandleReplySupported(ReplySupportedPacket packet, int port)
    {
        var module = ModuleOnPort(port);
        if ( module == null )
        {
            return;
        }

        module.SupportsEyeTracking        = module.SupportsEyeTracking && packet.eyeAvailable;
        module.SupportsExpressionTracking = module.SupportsExpressionTracking && packet.expressionAvailable;

        var initPacket = new EventInitPacket
        {
            expressionAvailable = ExpressionStatus == ModuleState.Uninitialized,
            eyeAvailable        = EyeStatus == ModuleState.Uninitialized,
        };

        _logger.LogInformation("Got supported for module {module}. Expr: {expr} Eye: {eye}...",
            module.ModuleClassName, initPacket.expressionAvailable, initPacket.eyeAvailable);
        _sandboxServer.SendData(initPacket, port);
    }

    /// <summary>
    /// The module has finished initializing. Claim the eye/expression slots it succeeded
    /// at (first module to claim one keeps it), start its update pump, and list it.
    /// </summary>
    private void HandleReplyInit(ReplyInitPacket packet, int port)
    {
        var module = ModuleOnPort(port);
        if ( module == null )
        {
            return;
        }

        module.ModuleInformation.Name     = packet.ModuleInformationName;
        module.SupportsEyeTracking        = module.SupportsEyeTracking && packet.eyeSuccess;
        module.SupportsExpressionTracking = module.SupportsExpressionTracking && packet.expressionSuccess;

        _logger.LogInformation("Got init for module {module}. Eye: {eye} Expr: {expr}...",
            module.ModuleClassName, packet.eyeSuccess, packet.expressionSuccess);

        // Initialized nothing: no slots, no update pump, and nothing worth listing.
        if ( !packet.eyeSuccess && !packet.expressionSuccess )
        {
            return;
        }

        // Assigned before Active is set below, because setting Active fires this.
        module.ModuleInformation.OnActiveChange = state =>
        {
            module.Status = state ? ModuleState.Active : ModuleState.Idle;
            _sandboxServer.SendData(new EventStatusUpdatePacket { ModuleState = module.Status }, port);
        };

        EyeStatus        = packet.eyeSuccess        ? ModuleState.Active : ModuleState.Uninitialized;
        ExpressionStatus = packet.expressionSuccess ? ModuleState.Active : ModuleState.Uninitialized;

        module.ModuleInformation.Active = true;
        lock ( AvailableSandboxModules )
        {
            // This module is already in the list, and its own flags are still false here,
            // so each Any() asks "does some OTHER module already own this slot?".
            module.ModuleInformation.UsingEye =
                !AvailableSandboxModules.Any(m => m.ModuleInformation.UsingEye) && packet.eyeSuccess;
            module.ModuleInformation.UsingExpression =
                !AvailableSandboxModules.Any(m => m.ModuleInformation.UsingExpression) && packet.expressionSuccess;
        }
        module.ModuleInformation.StaticImages = packet.IconDataStreams;

        EnsureModuleThreadStartedSandboxed(module);

        _dispatcherService.Run(() => PublishModuleCard(module));
    }

    /// <summary>
    /// Puts a freshly initialized module on the modules page, replacing its dormant
    /// placeholder and the startup filler row. UI thread only.
    /// </summary>
    private void PublishModuleCard(ModuleRuntimeInfo module)
    {
        // This module just came up, so any dormant card standing in for it is stale.
        // Cleared by dll rather than by name: the dormant card is named from the manifest
        // ("ETVR Tracking Module") and the live one names itself ("ETVR Eye Tracking
        // module"), so the name compare below never pairs them and the module ends up
        // listed as in-use AND not-in-use at the same time.
        RemoveDormantCardFor(module.SandboxModulePath);

        var replaced = false;
        for ( var i = 0; i < LoadedModulesMetadata.Count; i++ )
        {
            if ( LoadedModulesMetadata[i].Name == module.ModuleInformation.Name )
            {
                LoadedModulesMetadata[i] = module.ModuleInformation;
                replaced = true;
                break;
            }
        }

        if ( !replaced )
        {
            LoadedModulesMetadata.Add(module.ModuleInformation);
        }

        // Drop the placeholder row now that a real module is listed. Count > 1 so this
        // can never empty the list, which would leave the page blank instead of informative.
        if ( LoadedModulesMetadata.Count > 1 &&
             !LoadedModulesMetadata[0].Active &&
             ( LoadedModulesMetadata[0].Name == "No Modules Loaded" ||
               LoadedModulesMetadata[0].Name == "Initializing Modules..." ) )
        {
            LoadedModulesMetadata.RemoveAt(0);
        }

        if ( module.ModuleInformation.Active )
        {
            _logger.LogInformation("Tracking initialized via {module}", module.ModuleClassName);
        }
    }

    /// <summary>
    /// One frame of tracking data. Only the module owning a slot may write to it, so a
    /// second module can run alongside without fighting over the same expressions.
    /// </summary>
    private void HandleReplyUpdate(ReplyUpdatePacket packet, int port)
    {
        // Input-side heartbeat: lets the perf watch tell "the module went silent" apart
        // from "our pipeline stalled" when tracking freezes.
        PipelineWatch.RecordModuleData();

        var module = ModuleOnPort(port);
        if ( module is not { Status: ModuleState.Active } || !module.ModuleInformation.Active )
        {
            return;
        }

        if ( module.ModuleInformation.UsingEye )
        {
            packet.UpdateGlobalEyeState();
        }
        if ( module.ModuleInformation.UsingExpression )
        {
            packet.UpdateGlobalExpressionState();
        }
        packet.UpdateHeadState();
    }

    #endregion

    private void InitialiseSandboxesBaseOnPaths(IEnumerable<string> paths)
    {
        // The crash supervisor starts in the constructor and does its own hot-plug pass
        // every ~10s, so it can reach this before Initialize() has created the sandbox
        // host - on a slow startup that produced "Object reference not set to an instance
        // of an object. Failed to start sandbox process", and the module only came up on
        // the supervisor's next sweep. Nothing to do yet; Initialize() will start these.
        if ( _sandboxServer == null )
        {
            _logger.LogDebug("Sandbox host is not running yet - leaving module startup to Initialize().");
            return;
        }

        foreach ( var dll in paths )
        {
            try
            {
                // Start subprocess. Output is redirected in every configuration, not just
                // DEBUG, so a crashed module's own error text can go into the crash report.
                var sandboxProcess  = Process.Start(new ProcessStartInfo(
                    _sandboxProcessPath, $"--port {_sandboxServer.Port} --module-path \"{dll}\" --parent-pid {Environment.ProcessId}"
                )
                {
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
#if !DEBUG
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
#endif
                });

                var pid             = sandboxProcess.Id;

                // Add the module info into the loaded list
                ModuleRuntimeInfo runtimeInfo = new ModuleRuntimeInfo()
                {
                    SandboxProcessPID   = pid,
                    SandboxProcessPort  = -1,
                    SandboxModulePath   = dll,
                    IsActive            = true,
                    Process             = sandboxProcess,
                    ModuleClassName     = Path.GetFileNameWithoutExtension(dll),
                    ModuleInformation   = new (),
                    EventBus            = new ()
                };
                // Drain both pipes asynchronously. This is not optional: a redirected pipe
                // that nobody reads fills up and blocks the child process.
                sandboxProcess.OutputDataReceived += (_, e) =>
                {
                    if (e.Data != null) runtimeInfo.RecordOutput(e.Data);
                };
                sandboxProcess.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data != null) runtimeInfo.RecordOutput($"[stderr] {e.Data}");
                };
                sandboxProcess.BeginOutputReadLine();
                sandboxProcess.BeginErrorReadLine();

                lock ( AvailableSandboxModules )
                {
                    _logger.LogDebug("Started sandbox process with dll {dllPath}", dll);
                    AvailableSandboxModules.Add(runtimeInfo);
                }
            }
            catch ( Exception e )
            {
                _logger.LogWarning("{error} Failed to start sandbox process for {path}. Skipping...", e.Message, dll);
            }
        }
    }
    
    private void EnsureModuleThreadStartedSandboxed(ModuleRuntimeInfo module)
    {
        lock ( _moduleThreadsLock )
        {
            if (_moduleThreads.Any(pair =>
                ( pair.SandboxProcessPID    == module.SandboxProcessPID ) &&
                ( pair.SandboxProcessPort   == module.SandboxProcessPort )
            ))
            {
                return;
            }
        }

        int port = module.SandboxProcessPort;

        var cts = new CancellationTokenSource();
        var thread = new Thread(() =>
        {
            _logger.LogDebug("Starting thread for {module}", module.GetType().Name);
            var updatePacket = new EventUpdatePacket();
            while (!cts.IsCancellationRequested)
            {
                Thread.Sleep(10); // Wait 10ms => 100Hz
                _sandboxServer.SendData(updatePacket, port);
            }
            _logger.LogDebug("Thread for {module} ended", module.GetType().Name);
        });
        thread.Start();
        module.UpdateCancellationToken = cts;
        module.UpdateThread = thread;

        lock ( _moduleThreadsLock )
        {
            _moduleThreads.Add(module);
        }
    }

    private void AttemptSandboxedModuleInitialize(ModuleRuntimeInfo module)
    {
        // Tell the sandbox to call the initialize function on the module
        var eventGetSupportedPacket = new EventInitGetSupported();

        // If PID is valid and we know which port the sandbox process is running on
        if ( module.SandboxProcessPID != -1 && module.SandboxProcessPort > 0 )
        {
            _sandboxServer.SendData(eventGetSupportedPacket, module.SandboxProcessPort);
        }
        else
        {
            // Queue the packet so that we send it after we know which process the sandbox process is running on
            QueuedPacket queuedPacket = new QueuedPacket()
            {
                packet = eventGetSupportedPacket,
                destinationPort = module.SandboxProcessPort
            };
            module.EventBus.Enqueue(queuedPacket);
        }
    }

    private bool TeardownModuleSandboxed(ModuleRuntimeInfo module)
    {
        _logger.LogInformation("Tearing down {module} ", module.ModuleClassName);

        // Send a message to the module sub-process
        var eventTeardownPacket = new EventTeardownPacket();
        _sandboxServer.SendData(eventTeardownPacket, module.SandboxProcessPort);

        // Kill the update thread
        module.UpdateCancellationToken?.Cancel();
        // Give the module 100ms to kill itself
        Thread.Sleep(100);

        // Only bother tearing down a module if it's actually shutdown
        if ( !(module.Process?.HasExited ?? true) )
        {
            _logger.LogDebug("Module process has not yet exited");
            try  {
                if (!(module.Process?.WaitForExit(200) ?? false))
                {
                    _logger.LogDebug("Module {id} didn't exit gracefully. Forcing kill...", module.Process?.Id ?? -1);
                    module.Process?.Kill(entireProcessTree: true);
                    if (!(module.Process?.WaitForExit(2000) ?? false))
                    {
                        // on windows we can use taskkill /F /T /PID {procId} to force kill a process very aggressively. this has a higher success rate than process.kill!
                        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
                            using var killer = Process.Start(new ProcessStartInfo
                            {
                                FileName = "taskkill",
                                Arguments = $"/F /T /PID {module.Process.Id}",
                                CreateNoWindow = true,
                                UseShellExecute = false
                            });
                            killer?.WaitForExit(2000);
                        } else {
                            _logger.LogCritical("Process {id} is a zombie or stuck in Kernel I/O. Manual intervention required.", module.Process.Id);
                        }
                        return false; 
                    }
                }
            } catch ( System.ComponentModel.Win32Exception ex ) {
                // Can fail to call OpenProcessEx due to some error such as ACCESS_DENIED (process has higher priveleges, eg Sraniple)
                _logger.LogError($"Tried killing process with PID {module.Process.Id}. Got win32 error ({ex.ToString()}");
            } catch ( Exception ex ) {
                // Tell the user why we got an exception so that we can hopefully fix it.
                _logger.LogError($"Tried killing process with PID {module.Process.Id}. Got exception ({ex.HResult}) {ex.Message}");
            }
        }

        if (module.UpdateThread?.IsAlive ?? false)
        {
            // Edge case, we wait for the thread to finish before unloading the assembly
            var moduleName = module.ModuleInformation?.Name ?? module.ModuleClassName ?? "Unknown";
            _logger.LogDebug("Waiting for {module}'s thread to join...", moduleName);
            module.UpdateThread?.Join(500);
        }

        return true;
    }

    // Signal all active modules to gracefully shut down their respective runtimes
    public void TeardownAllAndResetAsync()
    {
        _logger.LogInformation("Tearing down all modules...");

        // Intentional teardown - the crash supervisor must not resurrect these processes
        _suppressAutoRestart = true;
        try
        {
            TeardownAllAndResetCore();
        }
        finally
        {
            lock ( _restartHistory )
            {
                _restartHistory.Clear();
            }
            _suppressAutoRestart = false;
        }
    }

    private void TeardownAllAndResetCore()
    {
        List<ModuleRuntimeInfo> moduleThreadsSnapshot;
        lock ( _moduleThreadsLock )
        {
            moduleThreadsSnapshot = _moduleThreads.ToList();
        }

        TeardownEach(moduleThreadsSnapshot);

        lock ( _moduleThreadsLock )
        {
            _moduleThreads.Clear();
        }

        // Snapshot under the lock rather than iterating the live list: a module handshake
        // can Add to it from the IPC receive thread at any moment, and iterating through
        // that throws "Collection was modified" - which, on the initialize worker thread,
        // is an unhandled exception that takes the whole process down.
        List<ModuleRuntimeInfo> sandboxSnapshot;
        lock ( AvailableSandboxModules )
        {
            sandboxSnapshot = AvailableSandboxModules.ToList();
            AvailableSandboxModules.Clear();
        }

        TeardownEach(sandboxSnapshot);

        EyeStatus = ModuleState.Uninitialized;
        ExpressionStatus = ModuleState.Uninitialized;
    }

    /// <summary>
    /// Tears down each still-running module, and force-interrupts the update thread of any
    /// that would not go quietly. One module refusing to die never stops the others.
    /// </summary>
    private void TeardownEach(IEnumerable<ModuleRuntimeInfo> modules)
    {
        foreach ( var module in modules )
        {
            // Objects may be null or already destroyed while we hold a lingering reference
            if ( module == null || ( module.Process?.HasExited ?? true ) )
            {
                continue;
            }

            var success = false;
            try
            {
                success = TeardownModuleSandboxed(module);
            }
            catch ( Exception e )
            {
                _logger.LogWarning("Error tearing down {module}: {message}", ModuleNameOf(module), e.Message);
            }
            finally
            {
                if ( !success )
                {
                    _logger.LogWarning("Module: {module} failed to shut down. Killing its thread.", ModuleNameOf(module));
                    module.UpdateThread?.Interrupt();
                }
            }
        }
    }

    /// <summary>
    /// Best available display name for a module. Never throws: this is used from failure
    /// paths, where the previous code dereferenced the legacy <c>Module</c> field - always
    /// null under sandboxing - and turned a failed teardown into a NullReferenceException.
    /// </summary>
    private static string ModuleNameOf(ModuleRuntimeInfo? module) =>
        module?.ModuleInformation?.Name ?? module?.ModuleClassName ?? "Unknown";
}