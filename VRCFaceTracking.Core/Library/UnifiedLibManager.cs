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
            MarkModuleDormant(moduleName, goneReason);
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

        List<string> runningPaths;
        lock ( AvailableSandboxModules )
        {
            runningPaths = AvailableSandboxModules
                .Where(m => m.Process is { HasExited: false })
                .Select(m => m.SandboxModulePath ?? string.Empty)
                .ToList();
        }

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

    /// <summary>Letters and digits only, lowercased - for comparing module display names.</summary>
    private static string Simplify(string? name) =>
        string.IsNullOrEmpty(name)
            ? string.Empty
            : new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    /// <summary>
    /// Shows a module as present-but-idle with the reason. A module that just disappears from
    /// the list reads as uninstalled, which is exactly the confusion this avoids.
    /// </summary>
    private void MarkModuleDormant(string moduleName, string reason)
    {
        _dispatcherService.Run(() =>
        {
            try
            {
                // Match loosely on purpose: the startup pass names the card from the module
                // manifest ("iFacialMocap Tracking Module") while a running module reports
                // itself more briefly ("iFacialMocap"), and an exact compare produced two
                // cards for the same module.
                var key = Simplify(moduleName);
                var existing = LoadedModulesMetadata.FirstOrDefault(m =>
                {
                    var name = Simplify(m.Name);
                    return name.Length > 0 && key.Length > 0 &&
                           (name.Contains(key, StringComparison.Ordinal) ||
                            key.Contains(name, StringComparison.Ordinal));
                });

                if ( existing != null )
                {
                    existing.Active = false;
                    existing.IsDormant = true;
                    existing.StatusNote = reason;
                    return;
                }

                LoadedModulesMetadata.Add(new ModuleMetadataInternal
                {
                    Name = moduleName,
                    Active = false,
                    IsDormant = true,
                    StatusNote = reason,
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
            _sandboxServer.OnPacketReceived += (in IpcPacket packet, in int port) =>
            {
                // Get sandbox module internal index
                int moduleIndex = -1;
                for ( int i = 0; i < AvailableSandboxModules.Count; i++ )
                {
                    if ( AvailableSandboxModules[i].SandboxProcessPort == port )
                    {
                        moduleIndex = i;
                        break;
                    }
                }

                switch ( packet.GetPacketType() )
                {
                    // @TODO: Move these all into methods to make the code easier to maintain
                    case IpcPacket.PacketType.Handshake:
                        {
                            // Look for the PID in the added modules list
                            var pkt = (HandshakePacket) packet;
                            lock ( AvailableSandboxModules )
                            {
                                bool pidRegistered = false;
                                
                                for ( int i = 0; i < AvailableSandboxModules.Count; i++ )
                                {
                                    if ( AvailableSandboxModules[i].SandboxProcessPID == pkt.PID )
                                    {
                                        var structCopy = AvailableSandboxModules[i];
                                        structCopy.SandboxProcessPort = port;
                                        AvailableSandboxModules[i] = structCopy;

                                        _logger.LogInformation("Initializing {module}...", AvailableSandboxModules[i].ModuleClassName.ToString());
                                        AttemptSandboxedModuleInitialize(AvailableSandboxModules[i]);
                                        pidRegistered = true;

                                        break;
                                    }
                                }

                                if ( pidRegistered == false )
                                {
                                    Process sandboxProcess = Process.GetProcessById(pkt.PID);

                                    ModuleRuntimeInfo runtimeInfo = new ModuleRuntimeInfo()
                                    {
                                        SandboxProcessPID   = pkt.PID,
                                        SandboxProcessPort  = port,
                                        SandboxModulePath   = pkt.ModulePath,
                                        IsActive            = true,
                                        Process             = sandboxProcess,
                                        ModuleClassName     = Path.GetFileNameWithoutExtension(pkt.ModulePath),
                                        ModuleInformation   = new (),
                                        EventBus            = new (),
                                    };
                                    AvailableSandboxModules.Add(runtimeInfo);

                                    _logger.LogInformation("Initializing {module}...", runtimeInfo.ModuleClassName);
                                    AttemptSandboxedModuleInitialize(runtimeInfo);
                                    pidRegistered = true;
                                }
                            }
                            break;
                        }

                    case IpcPacket.PacketType.EventLog:
                        {
                            EventLogPacket eventLogPacket = (EventLogPacket) packet;
                            _moduleLogger.Log(eventLogPacket.LogLevel, eventLogPacket.Message);
                            break;
                        }

                    case IpcPacket.PacketType.ReplyGetSupported:
                        {
                            // We now know whether or not the module supports face or eye tracking
                            ReplySupportedPacket replySupportedPacket = (ReplySupportedPacket) packet;

                            AvailableSandboxModules[moduleIndex].SupportsEyeTracking        = AvailableSandboxModules[moduleIndex].SupportsEyeTracking && replySupportedPacket.eyeAvailable;
                            AvailableSandboxModules[moduleIndex].SupportsExpressionTracking = AvailableSandboxModules[moduleIndex].SupportsExpressionTracking && replySupportedPacket.expressionAvailable;

                            // Now tell it to initialise
                            EventInitPacket eventInitPacket = new EventInitPacket()
                            {
                                expressionAvailable     = ExpressionStatus == ModuleState.Uninitialized,
                                eyeAvailable            = EyeStatus == ModuleState.Uninitialized,
                            };
                            _logger.LogInformation("Got supported for module {module}. Expr: {} Eye: {}...",
                                AvailableSandboxModules[moduleIndex].ModuleClassName,
                                eventInitPacket.expressionAvailable,
                                eventInitPacket.eyeAvailable);
                            _sandboxServer.SendData(eventInitPacket, port);
                            break;
                        }

                    case IpcPacket.PacketType.ReplyInit:
                        {
                            ReplyInitPacket replyInitPacket = (ReplyInitPacket) packet;
                            AvailableSandboxModules[moduleIndex].ModuleInformation.Name = replyInitPacket.ModuleInformationName;

                            // Update support variables
                            AvailableSandboxModules[moduleIndex].SupportsEyeTracking        = AvailableSandboxModules[moduleIndex].SupportsEyeTracking && replyInitPacket.eyeSuccess;
                            AvailableSandboxModules[moduleIndex].SupportsExpressionTracking = AvailableSandboxModules[moduleIndex].SupportsExpressionTracking && replyInitPacket.expressionSuccess;

                            _logger.LogInformation("Got init for module {module}. Eye: {eye} Expr: {expr}...",
                                AvailableSandboxModules[moduleIndex].ModuleClassName,
                                replyInitPacket.eyeSuccess,
                                replyInitPacket.eyeSuccess);

                            // Skip any modules that don't succeed, otherwise set UnifiedLib to have these states active and add module to module list.
                            if ( !replyInitPacket.eyeSuccess && !replyInitPacket.expressionSuccess )
                            {
                                break;
                            }

                            int portCopy = port; // So that we can use it in the lambda method
                            AvailableSandboxModules[moduleIndex].ModuleInformation.OnActiveChange = (state) =>
                            {
                                AvailableSandboxModules[moduleIndex].Status = state ? ModuleState.Active : ModuleState.Idle;

                                EventStatusUpdatePacket statusUpdatePkt = new EventStatusUpdatePacket();
                                statusUpdatePkt.ModuleState = AvailableSandboxModules[moduleIndex].Status;
                                _sandboxServer.SendData(statusUpdatePkt, portCopy);
                            };

                            EyeStatus           = replyInitPacket.eyeSuccess        ? ModuleState.Active : ModuleState.Uninitialized;
                            ExpressionStatus    = replyInitPacket.expressionSuccess ? ModuleState.Active : ModuleState.Uninitialized;

                            AvailableSandboxModules[moduleIndex].ModuleInformation.Active           = true;
                            AvailableSandboxModules[moduleIndex].ModuleInformation.UsingEye         = !AvailableSandboxModules.Any(m => m.ModuleInformation.UsingEye) && replyInitPacket.eyeSuccess;
                            AvailableSandboxModules[moduleIndex].ModuleInformation.UsingExpression  = !AvailableSandboxModules.Any(m => m.ModuleInformation.UsingExpression) && replyInitPacket.expressionSuccess;
                            AvailableSandboxModules[moduleIndex].ModuleInformation.StaticImages     = replyInitPacket.IconDataStreams;
                            EnsureModuleThreadStartedSandboxed(AvailableSandboxModules[moduleIndex]);

                            _dispatcherService.Run(() => {

                                // Check if the module is already loaded on the user-facing side. If so, overwrite with the new module if it's unloaded
                                var isModuleLoaded = false;
                                for ( var i = 0; i < LoadedModulesMetadata.Count; i++ )
                                {
                                    // Look for modules with the same name
                                    if ( LoadedModulesMetadata[i].Name == AvailableSandboxModules[moduleIndex].ModuleInformation.Name )
                                    {
                                        // Update module info
                                        LoadedModulesMetadata[i] = AvailableSandboxModules[moduleIndex].ModuleInformation;
                                        isModuleLoaded = true;
                                        break;
                                    }
                                }

                                // Add it to list if it was never loaded
                                if ( isModuleLoaded == false )
                                {
                                    LoadedModulesMetadata.Add(AvailableSandboxModules[moduleIndex].ModuleInformation);
                                }

                                if ( AvailableSandboxModules.Count == 0 )
                                {
                                    _logger.LogWarning("No modules loaded.");
                                    LoadedModulesMetadata.Clear();
                                    LoadedModulesMetadata.Add(new ModuleMetadataInternal
                                    {
                                        Active = false,
                                        Name = "No Modules Loaded"
                                    });
                                }
                                else
                                {
                                    // Remove our dummy module
                                    if ( LoadedModulesMetadata.Count > 0 &&
                                        LoadedModulesMetadata[0].Active == false &&
                                           (LoadedModulesMetadata[0].Name == "No Modules Loaded" ||
                                            LoadedModulesMetadata[0].Name == "Initializing Modules..." ))
                                    {
                                        LoadedModulesMetadata.RemoveAt(0);
                                    }

                                    // foreach ( var pair in _moduleThreads )
                                    {
                                        if ( AvailableSandboxModules[moduleIndex].ModuleInformation.Active )
                                        {
                                            _logger.LogInformation("Tracking initialized via {module}", AvailableSandboxModules[moduleIndex].ModuleClassName.ToString());
                                        }
                                    }
                                }
                            });

                            break;
                        }
                    case IpcPacket.PacketType.ReplyUpdate:
                        {
                            ReplyUpdatePacket replyUpdatePacket = (ReplyUpdatePacket) packet;

                            if ( AvailableSandboxModules[moduleIndex].Status == ModuleState.Active && AvailableSandboxModules[moduleIndex].ModuleInformation.Active )
                            {
                                if ( AvailableSandboxModules[moduleIndex].ModuleInformation.UsingEye )
                                {
                                    replyUpdatePacket.UpdateGlobalEyeState();
                                }
                                if ( AvailableSandboxModules[moduleIndex].ModuleInformation.UsingExpression )
                                {
                                    replyUpdatePacket.UpdateGlobalExpressionState();
                                }
                                replyUpdatePacket.UpdateHeadState();
                            }

                            break;
                        }
                }
            };
        }

        // Start Initialization
        _initializeWorker = new Thread(() =>
        {
            // Kill lingering threads
            TeardownAllAndResetAsync();

            // Find all modules, then start only the ones whose tracking source is actually
            // live. Without this every installed module spawns a process and races to claim
            // the eye/expression slots, which wastes startup time and invites conflicts.
            var modules = _moduleDataService.GetInstalledModules().Concat(_moduleDataService.GetLegacyModules()).ToList();
            var modulePaths = _moduleActivationService.FilterStartablePaths(modules);

            if (modulePaths.Count < modules.Count)
            {
                _logger.LogInformation(
                    "Source detection: starting {started} of {total} installed modules. See the Sources page for why.",
                    modulePaths.Count, modules.Count);

                // List the skipped ones greyed out instead of dropping them: a module that
                // silently disappears looks uninstalled rather than idle.
                var skipped = _moduleActivationService.Evaluate(modules).Where(d => !d.WillStart).ToList();
                _dispatcherService.Run(() =>
                {
                    foreach (var decision in skipped)
                    {
                        LoadedModulesMetadata.Add(new ModuleMetadataInternal
                        {
                            Name = decision.DisplayName,
                            Active = false,
                            IsDormant = true,
                            StatusNote = decision.Reason,
                            StaticImages = new List<Stream>(),
                        });
                    }
                });
            }

            // Load all modules
            AvailableSandboxModules.Clear();
            InitialiseSandboxesBaseOnPaths(modulePaths.ToArray());

            if ( AvailableSandboxModules != null && AvailableSandboxModules.Count > 0 )
            {
                _logger.LogDebug("Initializing requested runtimes...");
            }
            else
            {
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

        });
        _logger.LogInformation("Starting initialization tracking");
        _initializeWorker.Start();
    }

    private void InitialiseSandboxesBaseOnPaths(IEnumerable<string> paths)
    {
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

        foreach ( var module in moduleThreadsSnapshot )
        {
            var success = false;
            if (module == null || (module.Process?.HasExited ?? true))
                continue;
            try
            {
                success = TeardownModuleSandboxed(module);
            } finally
            {
                if ( !success )
                {
                    _logger.LogWarning($"Module: {module.Module.ModuleInformation.Name} failed to shut down. Killing its thread.");
                    module.UpdateThread.Interrupt();
                }
            }
        }

        lock ( _moduleThreadsLock )
        {
            _moduleThreads.Clear();
        }

        foreach ( var module in AvailableSandboxModules )
        {
            var success = false;
            if (module == null || (module.Process?.HasExited ?? true)) // c# objects may be null, use null coalesce to detect if a module has been destroyed but we have a lingering ref to it
                continue;
            try
            {
                success = TeardownModuleSandboxed(module);
            } finally
            {
                if ( !success )
                {
                    var moduleName = module.ModuleInformation?.Name ?? module.ModuleClassName ?? "Unknown";
                    _logger.LogWarning($"Module: {moduleName} failed to shut down. Killing its thread.");
                    module.UpdateThread?.Interrupt();
                }
            }
        }

        AvailableSandboxModules.Clear();

        EyeStatus = ModuleState.Uninitialized;
        ExpressionStatus = ModuleState.Uninitialized;
    }
}