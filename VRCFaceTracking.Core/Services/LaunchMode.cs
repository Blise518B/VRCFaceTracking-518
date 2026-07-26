using System.Diagnostics;

namespace VRCFaceTracking.Core.Services;

/// <summary>
/// Which build SteamVR should start when auto-launch fires.
/// </summary>
public enum AppLaunchMode
{
    /// <summary>The full WinUI app, with every settings page.</summary>
    Full,

    /// <summary>The lightweight console monitor.</summary>
    Lite,
}

/// <summary>
/// Shared launch preference, stored as a one-word file rather than inside LocalSettings.json.
///
/// The lite build has to read this before it does anything else, and a plain text file it can
/// read in a millisecond avoids pulling the whole settings stack (and its dependencies) into
/// the startup path just to answer one question. It's also trivial to inspect or fix by hand
/// if something goes wrong with auto-launch.
/// </summary>
public static class LaunchMode
{
    public const string FullExeName = "VRCFaceTracking.exe";
    public const string LiteExeName = "VRCFaceTracking.Lite.exe";

    private static string FilePath => Path.Combine(Utils.PersistentDataDirectory, "launch-mode.txt");

    public static AppLaunchMode Read()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return AppLaunchMode.Full;
            }

            var raw = File.ReadAllText(FilePath).Trim();
            return raw.Equals("lite", StringComparison.OrdinalIgnoreCase)
                ? AppLaunchMode.Lite
                : AppLaunchMode.Full;
        }
        catch (Exception)
        {
            // Unreadable preference must not stop the app launching at all
            return AppLaunchMode.Full;
        }
    }

    public static void Write(AppLaunchMode mode)
    {
        try
        {
            Directory.CreateDirectory(Utils.PersistentDataDirectory);
            File.WriteAllText(FilePath, mode == AppLaunchMode.Lite ? "lite" : "full");
        }
        catch (Exception)
        {
            // Not worth failing a settings toggle over
        }
    }

    /// <summary>
    /// Starts the other build and returns true if it was launched. Used by whichever binary
    /// SteamVR invoked to hand over to the one the user actually wants.
    /// </summary>
    public static bool TryHandOff(string exeName)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, exeName);
            if (!File.Exists(path))
            {
                return false;
            }

            Process.Start(new ProcessStartInfo(path)
            {
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = true,
            });
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
