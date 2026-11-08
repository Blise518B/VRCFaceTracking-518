using VRCFaceTracking.Core.Contracts.Services;
using VRCFaceTracking.Core.Services;
using VRCFaceTracking.Core.Services.Updates;
using Windows.System;

namespace VRCFaceTracking.Helpers;

/// <summary>
/// The one button behind the update banner and the Settings card: download if needed,
/// then hand over to the swap helper and close. Shared so both perform the identical
/// ordered shutdown.
/// </summary>
internal static class UpdateInstaller
{
    /// <summary>
    /// An install that can't replace itself (a build from source, Program Files) gets the
    /// release page instead. Otherwise: download and verify, start the helper, then the same
    /// shutdown as the lite hand-off - watchdog first (or it would treat our exit as a crash
    /// and relaunch the old version under the installer), a clean teardown so mutation and
    /// settings saves finish, clear the crash marker, exit 0.
    /// </summary>
    public static async Task UpdateNowAsync()
    {
        var updates = App.GetService<UpdateService>();

        if (!updates.CanSelfUpdate)
        {
            await Launcher.LaunchUriAsync(new Uri(updates.Latest?.PageUrl ?? updates.ReleasesPage));
            return;
        }

        if (updates.State == UpdateState.Available && !await updates.DownloadAsync())
        {
            return;
        }

        if (!await updates.BeginInstallAsync())
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
            // The helper is already waiting for us; a teardown hiccup must not keep the old
            // version running. Anything left of us is stopped by the helper.
        }

        Environment.Exit(0);
    }
}
