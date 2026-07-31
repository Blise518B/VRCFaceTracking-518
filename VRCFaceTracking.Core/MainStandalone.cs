using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using VRCFaceTracking.Core.Contracts.Services;
using VRCFaceTracking.Core.Library;
using VRCFaceTracking.Core.Params.Data;

[assembly: TypeForwardedTo(typeof(VRCFaceTracking.ExtTrackingModule))]
[assembly: TypeForwardedTo(typeof(VRCFaceTracking.ModuleMetadata))]
[assembly: TypeForwardedTo(typeof(ModuleState))]

namespace VRCFaceTracking.Core;

public class MainStandalone : IMainService
{
    private readonly ILogger<MainStandalone> _logger;
    private readonly ILibManager _libManager;
    private readonly UnifiedTrackingMutator _mutator;

    public Action<string, float> ParameterUpdate { get; set; } = (_, _) => { };

    public MainStandalone(
        ILogger<MainStandalone> logger,
        ILibManager libManager,
        UnifiedTrackingMutator mutator
        )
    {
        _logger = logger;
        _libManager = libManager;
        _mutator = mutator;
    }

    public async Task Teardown()
    {
        _logger.LogInformation("VRCFT Standalone Exiting!");
        await _mutator.Save();

        _libManager.TeardownAllAndResetAsync();

        if (OperatingSystem.IsWindows())
        {
            _logger.LogDebug("Resetting our time end period...");
            var timeEndRes = Utils.TimeEndPeriod(1);
            if (timeEndRes != 0)
            {
                _logger.LogWarning($"TimeEndPeriod failed with HRESULT {timeEndRes}");
            }
        }

        _logger.LogDebug("Teardown complete. Awaiting exit...");
    }

    public Task InitializeAsync()
    {
        VRChat.EnsureVRCOSCDirectory();

        // Ensure OSC is enabled
        var isWindows = OperatingSystem.IsWindows();

        if (isWindows && VRChat.ForceEnableOsc()) // If osc was previously not enabled
        {
            _logger.LogWarning("VRCFT detected OSC was disabled and automatically enabled it.");
            // If we were launched after VRChat
            if (VRChat.IsVrChatRunning())
                _logger.LogError(
                    "However, VRChat was running while this change was made.\n" +
                    "If parameters do not update, please restart VRChat or manually enable OSC yourself in your avatar's expressions menu.");
        }

        _mutator.Load();

        // Begin main OSC update loop
        _logger.LogDebug("Starting OSC update loop...");

        if (isWindows)
        {
            Utils.TimeBeginPeriod(1);

            // Without this, Windows 11 ignores the 1ms request whenever the window is
            // minimized/occluded (i.e. the whole VR session) and the 10ms send loop
            // ticks at 15.6ms instead - measured live by PipelineWatch.
            var honored = Utils.HonorTimerResolutionWhileMinimized();
            _logger.LogDebug("Timer-resolution coalescing opt-out: {result}", honored ? "applied" : "not available");
        }

        return Task.CompletedTask;
    }
}
