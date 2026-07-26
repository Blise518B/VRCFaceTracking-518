using System.Diagnostics;

namespace VRCFaceTracking.Watchdog;

/// <summary>
/// Tiny companion process spawned by VRCFaceTracking on startup. It waits for the main
/// process to exit and relaunches it if the exit was a crash (non-zero exit code).
/// Clean shutdowns (exit code 0) and shutdowns caused by a newer instance taking over
/// never trigger a relaunch. Rapid crash loops are capped.
/// </summary>
public static class Program
{
    // If the app survived at least this long, a crash resets the restart counter
    private static readonly TimeSpan StableUptime = TimeSpan.FromMinutes(2);
    private const int MaxConsecutiveRestarts = 5;
    private static readonly TimeSpan RelaunchDelay = TimeSpan.FromSeconds(2);

    private static string _logPath = string.Empty;

    public static int Main(string[] args)
    {
        var pid = -1;
        var exePath = string.Empty;
        var restartCount = 0;
        var instanceName = "VRCFaceTracking";

        for ( var i = 0; i < args.Length - 1; i++ )
        {
            switch ( args[i] )
            {
                case "--watch-pid":
                    int.TryParse(args[i + 1], out pid);
                    break;
                case "--exe":
                    exePath = args[i + 1];
                    break;
                case "--restart-count":
                    int.TryParse(args[i + 1], out restartCount);
                    break;
                case "--instance-name":
                    instanceName = args[i + 1];
                    break;
            }
        }

        var persistentDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VRCFaceTracking");
        Directory.CreateDirectory(persistentDir);
        _logPath = Path.Combine(persistentDir, "watchdog.log");

        if ( pid <= 0 || string.IsNullOrEmpty(exePath) || !File.Exists(exePath) )
        {
            Log($"Invalid arguments (pid={pid}, exe=\"{exePath}\"). Exiting.");
            return 1;
        }

        Process parent;
        DateTime parentStartTime;
        try
        {
            parent = Process.GetProcessById(pid);
            // Makes .NET hold a real process handle so the exit code stays readable
            // after the process dies, even though we didn't start it ourselves
            parent.EnableRaisingEvents = true;
            parentStartTime = parent.StartTime;
        }
        catch ( Exception e )
        {
            Log($"Could not open process {pid}: {e.Message}. Exiting.");
            return 1;
        }

        Log($"Watching VRCFaceTracking (PID {pid}), restart count so far: {restartCount}.");
        parent.WaitForExit();

        int exitCode;
        try
        {
            exitCode = parent.ExitCode;
        }
        catch ( Exception e )
        {
            Log($"Could not read exit code: {e.Message}. Assuming crash.");
            exitCode = -1;
        }

        if ( exitCode == 0 )
        {
            Log("VRCFaceTracking exited cleanly. Goodbye.");
            return 0;
        }

        // If another instance is already running, the old one was likely killed by the
        // new instance's startup routine - do not fight it by spawning yet another copy.
        if ( Process.GetProcessesByName(instanceName).Length > 0 )
        {
            Log($"{instanceName} exited with code {exitCode} but another instance is already running. Not restarting.");
            return 0;
        }

        var uptime = DateTime.Now - parentStartTime;
        var newCount = uptime >= StableUptime ? 1 : restartCount + 1;

        if ( newCount > MaxConsecutiveRestarts )
        {
            Log($"VRCFaceTracking crashed (code {exitCode}) after {uptime.TotalSeconds:F0}s, " +
                $"but it has already been restarted {MaxConsecutiveRestarts} times in a row. Giving up.");
            return 2;
        }

        Log($"VRCFaceTracking crashed with exit code {exitCode} after {uptime.TotalSeconds:F0}s. " +
            $"Relaunching in {RelaunchDelay.TotalSeconds:F0}s (attempt {newCount}/{MaxConsecutiveRestarts})...");
        Thread.Sleep(RelaunchDelay);

        try
        {
            Process.Start(new ProcessStartInfo(exePath)
            {
                Arguments = $"--restarted-count {newCount}",
                WorkingDirectory = Path.GetDirectoryName(exePath)!,
                UseShellExecute = true,
            });
            Log("Relaunched VRCFaceTracking.");
        }
        catch ( Exception e )
        {
            Log($"Failed to relaunch VRCFaceTracking: {e.Message}");
            return 3;
        }

        return 0;
    }

    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(_logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch ( Exception )
        {
            // Logging must never take the watchdog down
        }
    }
}
