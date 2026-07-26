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
    /// Finds the other build's exe: normally right next to ours (the packaged layout, and the
    /// full build's output folder which also carries the lite exe), with a fallback hop across
    /// the dev tree's sibling project output for running straight out of the repo.
    /// </summary>
    private static string? Resolve(string exeName)
    {
        try
        {
            var local = Path.Combine(AppContext.BaseDirectory, exeName);
            if (File.Exists(local))
            {
                return local;
            }

            // Dev layout: <repo>\<project>\bin\x64\Release\<tfm>\win-x64\
            var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\.."));
            var project = exeName == FullExeName ? "VRCFaceTracking" : "VRCFaceTracking.Lite";
            var sibling = Path.Combine(repoRoot, project,
                @"bin\x64\Release\net10.0-windows10.0.22621.0\win-x64", exeName);
            return File.Exists(sibling) ? sibling : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>True when the other build exists somewhere we can start it from.</summary>
    public static bool CanHandOff(string exeName) => Resolve(exeName) != null;

    /// <summary>
    /// Starts the other build and returns true if it was launched. Used by whichever binary
    /// SteamVR invoked to hand over to the one the user actually wants, and by the live
    /// full-app/console switch.
    /// </summary>
    public static bool TryHandOff(string exeName, string? arguments = null)
    {
        try
        {
            var path = Resolve(exeName);
            if (path == null)
            {
                return false;
            }

            Process.Start(new ProcessStartInfo(path)
            {
                Arguments = arguments ?? string.Empty,
                WorkingDirectory = Path.GetDirectoryName(path)!,
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
