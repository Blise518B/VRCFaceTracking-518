using Microsoft.Extensions.Logging;
using VRCFaceTracking.Core.Contracts.Services;

namespace VRCFaceTracking.Core.Services;

/// <summary>
/// Makes headset switching hands-free: when the set of startable modules
/// changes because a tracking source appeared or vanished (took off the
/// Dream Air, put on the Quest Pro via Virtual Desktop), the modules are
/// re-initialized automatically instead of waiting for a manual restart.
///
/// Deliberately cautious: the decision set must be stable across two
/// consecutive checks (about 20 s) before acting, so a source that flickers
/// for a moment never tears tracking down. Source gating off = no dynamic
/// decisions = nothing to do.
/// </summary>
public class ModuleHotSwapService : IDisposable
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(10);
    // Short enough that a headset visible shortly after startup gets its
    // companion apps quickly; the module-set baseline is taken on the first
    // tick either way, so later data-driven changes still re-init correctly.
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(15);

    private readonly ILibManager _libManager;
    private readonly ModuleActivationService _activation;
    private readonly IModuleDataService _moduleDataService;
    private readonly IDispatcherService _dispatcherService;
    private readonly CompanionAppService _companions;
    private readonly ILogger<ModuleHotSwapService>? _logger;

    private Timer? _timer;
    private string? _appliedSet;
    private string? _pendingSet;
    private string? _lastHmd;
    private volatile bool _reinitInFlight;

    public ModuleHotSwapService(
        ILibManager libManager,
        ModuleActivationService activation,
        IModuleDataService moduleDataService,
        IDispatcherService dispatcherService,
        CompanionAppService companions,
        ILogger<ModuleHotSwapService>? logger = null)
    {
        _libManager = libManager;
        _activation = activation;
        _moduleDataService = moduleDataService;
        _dispatcherService = dispatcherService;
        _companions = companions;
        _logger = logger;
    }

    public void Start()
    {
        _timer ??= new Timer(_ => Tick(), null, StartupGrace, CheckInterval);
    }

    private void Tick()
    {
        try
        {
            WatchHmdChange();

            if (!_activation.GatingEnabled || _reinitInFlight)
            {
                return;
            }

            var installed = _moduleDataService.GetInstalledModules()
                .Concat(_moduleDataService.GetLegacyModules());
            // forStart:false - this set only says which modules BELONG here, so a
            // running module whose own stream listener hides its evidence still counts.
            var startable = _activation.FilterStartablePaths(installed, logDecisions: false, forStart: false);
            var current = string.Join("|", startable.OrderBy(p => p));

            // First observation after startup: assume it matches what the
            // normal init already started, and just watch for changes.
            if (_appliedSet is null)
            {
                _appliedSet = current;
                return;
            }

            if (current == _appliedSet)
            {
                _pendingSet = null;
                return;
            }

            // Never swap TO nothing: a momentary all-sources blackout (app
            // restarting, stream hiccup) must not tear down modules that have
            // their own reconnect logic. Recovery to any non-empty set still
            // triggers normally.
            if (current.Length == 0)
            {
                _pendingSet = null;
                return;
            }

            // A source that APPEARED is already handled incrementally: the lib manager's
            // hot-plug check starts just that module about ten seconds after its stream
            // shows up. If everything that should be running is running, a full
            // re-initialize has nothing to fix - and doing one anyway killed the module
            // that had just started, after which re-init missed the phone behind a stale
            // probe cache, and tracking thrashed through three starts and two blackouts
            // before settling (measured live 2026-09-11). Adopt the set and move on.
            if (SameSet(startable, _libManager.GetRunningModulePaths()))
            {
                _appliedSet = current;
                _pendingSet = null;
                _logger?.LogInformation(
                    "Tracking sources changed, but the matching modules are already running - nothing to re-initialize.");
                return;
            }

            // Require the same changed set twice in a row before acting.
            if (current != _pendingSet)
            {
                _pendingSet = current;
                _logger?.LogInformation(
                    "Tracking sources changed - waiting one more check before re-initializing modules.");
                return;
            }

            _pendingSet = null;
            _appliedSet = current;
            _reinitInFlight = true;
            _logger?.LogInformation(
                "Tracking sources changed and stayed changed - re-initializing modules for the new hardware.");

            _dispatcherService.Run(() =>
            {
                try
                {
                    _libManager.TeardownAllAndResetAsync();
                    _libManager.Initialize();
                }
                catch (Exception e)
                {
                    _logger?.LogError("Hot-swap re-initialize failed: {message}", e.Message);
                }
                finally
                {
                    _reinitInFlight = false;
                }
            });
        }
        catch (Exception e)
        {
            _logger?.LogWarning("Hot-swap check failed: {message}", e.Message);
            _reinitInFlight = false;
        }
    }

    /// <summary>
    /// When SteamVR's active headset changes (or first appears), re-run the
    /// companion auto-launch pass: swapping to the Pimax mid-session then
    /// starts BrokenEye/ETVR by itself, and the module hot-swap follows once
    /// their data flows. The pass is idempotent and Pimax-gated internally,
    /// so a swap to the Quest Pro launches nothing.
    /// </summary>
    private void WatchHmdChange()
    {
        var hmd = CompanionAppService.HmdResolver?.Invoke();
        if (string.Equals(hmd, _lastHmd, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var previous = _lastHmd;
        _lastHmd = hmd;

        if (hmd is null)
        {
            return; // SteamVR went away; nothing to launch for that.
        }

        _logger?.LogInformation("Active headset changed: {old} -> {new}", previous ?? "none", hmd);
        _companions.LaunchConfiguredAtStartup();
    }

    /// <summary>Order- and case-insensitive equality of two path sets.</summary>
    private static bool SameSet(IEnumerable<string> a, IEnumerable<string> b) =>
        new HashSet<string>(a, StringComparer.OrdinalIgnoreCase)
            .SetEquals(new HashSet<string>(b, StringComparer.OrdinalIgnoreCase));

    public void Dispose() => _timer?.Dispose();
}
