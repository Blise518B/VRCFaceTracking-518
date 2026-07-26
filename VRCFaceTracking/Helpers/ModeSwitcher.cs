using VRCFaceTracking.Core.Contracts.Services;
using VRCFaceTracking.Core.Services;

namespace VRCFaceTracking.Helpers;

/// <summary>
/// Live hand-off from the full app to the lite console. Shared by the navigation pane entry
/// and the Settings card so both perform the identical ordered shutdown.
/// </summary>
internal static class ModeSwitcher
{
    public static bool LiteAvailable => LaunchMode.CanHandOff(LaunchMode.LiteExeName);

    /// <summary>
    /// Order matters: the watchdog dies first (otherwise it treats our exit as a crash and
    /// relaunches the full app underneath the console), then a clean teardown so mutation and
    /// settings saves finish, then the console starts - it waits for us to vanish before
    /// claiming the OSC ports.
    /// </summary>
    public static async Task SwitchToLiteAsync()
    {
        if (!LiteAvailable)
        {
            return;
        }

        try
        {
            App.GetService<AutoRestartService>().StopWatchdog();
            await App.GetService<IMainService>().Teardown();
            App.GetService<CrashReportService>().MarkCleanExit();
        }
        catch (Exception)
        {
            // A teardown hiccup must not strand the user with no app at all - the console's
            // startup sweep cleans up whatever is left of us.
        }

        LaunchMode.TryHandOff(LaunchMode.LiteExeName, "--force-lite");
        Environment.Exit(0);
    }
}
