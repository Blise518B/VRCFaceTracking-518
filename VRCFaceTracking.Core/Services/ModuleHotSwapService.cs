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
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(45);

    private readonly ILibManager _libManager;
    private readonly ModuleActivationService _activation;
    private readonly IModuleDataService _moduleDataService;
    private readonly IDispatcherService _dispatcherService;
    private readonly ILogger<ModuleHotSwapService>? _logger;

    private Timer? _timer;
    private string? _appliedSet;
    private string? _pendingSet;
    private volatile bool _reinitInFlight;

    public ModuleHotSwapService(
        ILibManager libManager,
        ModuleActivationService activation,
        IModuleDataService moduleDataService,
        IDispatcherService dispatcherService,
        ILogger<ModuleHotSwapService>? logger = null)
    {
        _libManager = libManager;
        _activation = activation;
        _moduleDataService = moduleDataService;
        _dispatcherService = dispatcherService;
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
            if (!_activation.GatingEnabled || _reinitInFlight)
            {
                return;
            }

            var installed = _moduleDataService.GetInstalledModules()
                .Concat(_moduleDataService.GetLegacyModules());
            var current = string.Join("|",
                _activation.FilterStartablePaths(installed, logDecisions: false).OrderBy(p => p));

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

    public void Dispose() => _timer?.Dispose();
}
