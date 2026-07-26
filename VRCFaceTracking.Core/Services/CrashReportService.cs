using System.Diagnostics;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;

namespace VRCFaceTracking.Core.Services;

/// <summary>
/// Writes human-readable crash reports so a crash can actually be diagnosed after the fact.
/// Reports land in Documents\VRCFaceTracking\crashes\ next to latest.log.
///
/// Three kinds of crash are covered:
///   * managed exceptions - full exception chain and stack, written as it happens
///   * hard crashes (native access violations, stack overflow, the process being
///     killed) - these never reach a managed handler, so they are detected on the
///     *next* start via a run marker left behind by the dead process
///   * module process crashes - reported by the sandbox supervisor with the module's
///     own stderr tail, which is usually where a native tracking SDK explains itself
///
/// Nothing here may ever throw: a crash logger that crashes is worse than none.
/// </summary>
public class CrashReportService
{
    private const int MaxReportsKept = 20;
    private const int LogTailLines = 150;

    /// <summary>
    /// Root the reports hang off. Defaults to the folder holding latest.log; settable so
    /// tests (and any future portable mode) can redirect away from the user's real data.
    /// Must be set before <see cref="EarlyStartup"/>.
    /// </summary>
    public static string BaseDirectory { get; set; } = Utils.UserAccessibleDataDirectory;

    public static string CrashDirectory => Path.Combine(BaseDirectory, "crashes");

    private static string MarkerPath => Path.Combine(CrashDirectory, "running.marker");
    private static string ArchivedLogPath => Path.Combine(CrashDirectory, "previous-run.log");
    private static string HistoryPath => Path.Combine(CrashDirectory, "history.log");
    private static string LiveLogPath => Path.Combine(BaseDirectory, "latest.log");

    /// <summary>Details recovered from a previous run that never exited cleanly.</summary>
    public sealed class PreviousRun
    {
        public string RawMarker = string.Empty;
        public DateTime StartedAt;
        public int ProcessId;
        public string Version = "unknown";
    }

    private static PreviousRun? _staleRun;
    private static bool _earlyDone;

    private readonly ILogger<CrashReportService>? _logger;
    private readonly DateTime _startedAt = DateTime.Now;
    private bool _reportWritten;

    public CrashReportService(ILogger<CrashReportService>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>How many consecutive times the watchdog has relaunched us.</summary>
    public int RelaunchCount { get; set; }

    /// <summary>One-line description of the most recent crash, for the settings page.</summary>
    public string? LastCrashSummary { get; private set; }

    /// <summary>
    /// Must run before logging is configured, because LogFileProvider opens latest.log
    /// with FileMode.Create and destroys the previous run's log - which, now that the
    /// watchdog relaunches automatically, would erase the evidence within seconds.
    /// </summary>
    public static void EarlyStartup()
    {
        if (_earlyDone)
        {
            return;
        }
        _earlyDone = true;

        try
        {
            Directory.CreateDirectory(CrashDirectory);

            // Preserve the outgoing log before it gets truncated
            if (File.Exists(LiveLogPath))
            {
                try
                {
                    File.Copy(LiveLogPath, ArchivedLogPath, overwrite: true);
                }
                catch (Exception)
                {
                    // Locked by a dying process - not worth failing startup over
                }
            }

            // A marker still on disk means the previous process never got to clean up
            if (File.Exists(MarkerPath))
            {
                var raw = SafeReadAllText(MarkerPath);
                _staleRun = ParseMarker(raw);
            }

            File.WriteAllText(MarkerPath,
                $"pid={Environment.ProcessId}\n" +
                $"started={DateTime.Now:o}\n" +
                $"version={AppVersion()}\n");
        }
        catch (Exception)
        {
            // Never let crash bookkeeping stop the app from starting
        }
    }

    private static PreviousRun? ParseMarker(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var info = new PreviousRun { RawMarker = raw.Trim() };
        foreach (var line in raw.Split('\n'))
        {
            var parts = line.Split('=', 2);
            if (parts.Length != 2)
            {
                continue;
            }

            switch (parts[0].Trim())
            {
                case "pid":
                    int.TryParse(parts[1].Trim(), out var pid);
                    info.ProcessId = pid;
                    break;
                case "started":
                    DateTime.TryParse(parts[1].Trim(), out var started);
                    info.StartedAt = started;
                    break;
                case "version":
                    info.Version = parts[1].Trim();
                    break;
            }
        }
        return info;
    }

    /// <summary>
    /// Writes a report for a previous run that died without a managed exception.
    /// Call once at startup, after logging is up.
    /// </summary>
    public void ReportPreviousRunIfCrashed()
    {
        var stale = _staleRun;
        if (stale == null)
        {
            return;
        }
        _staleRun = null;

        try
        {
            var body = new StringBuilder();
            body.AppendLine("KIND: hard crash (no managed exception was raised)");
            body.AppendLine();
            body.AppendLine(
                "The previous session ended without running its shutdown path and without a\n" +
                ".NET exception being reported. That points at something the runtime cannot\n" +
                "catch - typically a native access violation inside a tracking SDK, a stack\n" +
                "overflow, the process being killed, or a power/driver event.");
            body.AppendLine();
            body.AppendLine($"Previous PID:      {stale.ProcessId}");
            body.AppendLine($"Previous version:  {stale.Version}");
            if (stale.StartedAt != default)
            {
                body.AppendLine($"Previous start:    {stale.StartedAt:yyyy-MM-dd HH:mm:ss}");
                body.AppendLine($"Ran for:           {DescribeSpan(DateTime.Now - stale.StartedAt)} (until the crash)");
            }
            body.AppendLine();
            AppendEnvironment(body);
            body.AppendLine();
            body.AppendLine($"--- last {LogTailLines} lines of the previous run's log ---");
            body.AppendLine(ReadTail(ArchivedLogPath, LogTailLines));

            var summary = stale.StartedAt != default
                ? $"hard crash after {DescribeSpan(DateTime.Now - stale.StartedAt)} (no .NET exception)"
                : "hard crash (no .NET exception)";

            Write("hardcrash", summary, body.ToString());
        }
        catch (Exception e)
        {
            _logger?.LogWarning("Could not write previous-run crash report: {message}", e.Message);
        }
    }

    /// <summary>Writes a full report for an unhandled managed exception.</summary>
    public void ReportException(Exception? exception, string source)
    {
        try
        {
            var body = new StringBuilder();
            body.AppendLine($"KIND: unhandled managed exception ({source})");
            body.AppendLine();

            if (exception == null)
            {
                body.AppendLine("No exception object was supplied by the runtime.");
            }
            else
            {
                var depth = 0;
                for (var ex = exception; ex != null; ex = ex.InnerException, depth++)
                {
                    var label = depth == 0 ? "EXCEPTION" : $"INNER EXCEPTION #{depth}";
                    body.AppendLine($"--- {label} ---");
                    body.AppendLine($"Type:    {ex.GetType().FullName}");
                    body.AppendLine($"Message: {ex.Message}");
                    if (ex.TargetSite != null)
                    {
                        body.AppendLine($"Thrown in: {ex.TargetSite.DeclaringType?.FullName}.{ex.TargetSite.Name}");
                    }
                    if (ex is System.ComponentModel.Win32Exception win32)
                    {
                        body.AppendLine($"Win32 error: {win32.NativeErrorCode}");
                    }
                    body.AppendLine("Stack trace:");
                    body.AppendLine(ex.StackTrace ?? "  (none)");
                    body.AppendLine();

                    if (depth > 8)
                    {
                        body.AppendLine("(inner exception chain truncated)");
                        break;
                    }
                }
            }

            AppendEnvironment(body);
            body.AppendLine();
            body.AppendLine($"--- last {LogTailLines} lines of this run's log ---");
            body.AppendLine(ReadTail(LiveLogPath, LogTailLines));

            var summary = exception != null
                ? $"{exception.GetType().Name}: {FirstLine(exception.Message)}"
                : $"unhandled exception ({source})";

            Write("exception", summary, body.ToString());
            _reportWritten = true;
        }
        catch (Exception e)
        {
            _logger?.LogWarning("Could not write crash report: {message}", e.Message);
        }
    }

    /// <summary>Records a sandboxed tracking module dying unexpectedly.</summary>
    public void ReportModuleCrash(string moduleName, int exitCode, string? outputTail)
    {
        try
        {
            var body = new StringBuilder();
            body.AppendLine("KIND: tracking module process crashed");
            body.AppendLine();
            body.AppendLine($"Module:     {moduleName}");
            body.AppendLine($"Exit code:  {exitCode}{DescribeExitCode(exitCode)}");
            body.AppendLine();
            body.AppendLine(
                "The module ran in its own process, so this did not take VRCFaceTracking\n" +
                "down with it. If a module crashes repeatedly, its own output below is the\n" +
                "most useful part of this report - native tracking SDKs usually explain\n" +
                "themselves there rather than through .NET.");
            body.AppendLine();
            AppendEnvironment(body);
            body.AppendLine();
            body.AppendLine("--- module process output (stdout + stderr) ---");
            body.AppendLine(string.IsNullOrWhiteSpace(outputTail) ? "(nothing captured)" : outputTail);
            body.AppendLine();
            body.AppendLine($"--- last {LogTailLines} lines of the app log ---");
            body.AppendLine(ReadTail(LiveLogPath, LogTailLines));

            Write("module", $"module \"{moduleName}\" crashed (exit {exitCode})", body.ToString());
        }
        catch (Exception e)
        {
            _logger?.LogWarning("Could not write module crash report: {message}", e.Message);
        }
    }

    /// <summary>
    /// Clears the run marker so the next start knows this session ended properly.
    /// </summary>
    public void MarkCleanExit()
    {
        try
        {
            if (File.Exists(MarkerPath))
            {
                File.Delete(MarkerPath);
            }
        }
        catch (Exception)
        {
            // Worst case the next start reports a crash that didn't happen
        }
    }

    /// <summary>True if this session already wrote an exception report.</summary>
    public bool HasReportedThisSession => _reportWritten;

    public IEnumerable<FileInfo> RecentReports()
    {
        try
        {
            return new DirectoryInfo(CrashDirectory)
                .GetFiles("crash-*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc);
        }
        catch (Exception)
        {
            return Array.Empty<FileInfo>();
        }
    }

    // ------------------------------------------------------------------ helpers

    private void Write(string kind, string summary, string body)
    {
        Directory.CreateDirectory(CrashDirectory);

        // Millisecond precision plus a collision guard: several modules can die inside the
        // same second, and a second-resolution name would silently overwrite the evidence.
        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff");
        var path = Path.Combine(CrashDirectory, $"crash-{stamp}-{kind}.log");
        for (var attempt = 2; File.Exists(path) && attempt < 100; attempt++)
        {
            path = Path.Combine(CrashDirectory, $"crash-{stamp}-{kind}-{attempt}.log");
        }

        var header = new StringBuilder();
        header.AppendLine("VRCFaceTracking - Blise518B Edition :: crash report");
        header.AppendLine(new string('=', 64));
        header.AppendLine($"When:        {DateTime.Now:yyyy-MM-dd HH:mm:ss} (local)");
        header.AppendLine($"Summary:     {summary}");
        header.AppendLine($"App version: {AppVersion()}");
        header.AppendLine($"This session ran for: {DescribeSpan(DateTime.Now - _startedAt)}");
        if (RelaunchCount > 0)
        {
            header.AppendLine($"NOTE: this session was itself an automatic relaunch (#{RelaunchCount}) after an earlier crash.");
        }
        header.AppendLine(new string('=', 64));
        header.AppendLine();

        File.WriteAllText(path, header + body);

        try
        {
            File.AppendAllText(HistoryPath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  [{kind}] {summary}{Environment.NewLine}");
        }
        catch (Exception)
        {
        }

        LastCrashSummary = $"{DateTime.Now:yyyy-MM-dd HH:mm}  {summary}";
        _logger?.LogError("Crash report written: {path}", path);

        Prune();
    }

    private static void Prune()
    {
        try
        {
            var stale = new DirectoryInfo(CrashDirectory)
                .GetFiles("crash-*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(MaxReportsKept);
            foreach (var file in stale)
            {
                file.Delete();
            }
        }
        catch (Exception)
        {
        }
    }

    private static void AppendEnvironment(StringBuilder body)
    {
        body.AppendLine("--- environment ---");
        try
        {
            body.AppendLine($"OS:              {Environment.OSVersion.VersionString}");
            body.AppendLine($".NET:            {Environment.Version}");
            body.AppendLine($"Machine cores:   {Environment.ProcessorCount}");
            body.AppendLine($"Working set:     {Environment.WorkingSet / 1024 / 1024} MB");
            body.AppendLine($"64-bit process:  {Environment.Is64BitProcess}");

            // Which of the usual suspects were running matters: a lot of tracking
            // crashes only happen with SteamVR up, or when VRChat restarts OSC.
            foreach (var name in new[] { "VRChat", "vrserver", "vrmonitor", "SRanipalRuntime", "sr_runtime", "VRCFaceTracking.ModuleProcess" })
            {
                var count = SafeProcessCount(name);
                body.AppendLine($"  {name,-28} {(count > 0 ? $"running ({count})" : "not running")}");
            }
        }
        catch (Exception e)
        {
            body.AppendLine($"(could not collect environment: {e.Message})");
        }
    }

    private static int SafeProcessCount(string name)
    {
        try
        {
            return Process.GetProcessesByName(name).Length;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static string DescribeExitCode(int exitCode) => exitCode switch
    {
        -1 => " (invalid arguments)",
        -2 => " (network connection timed out)",
        -3 => " (unhandled exception inside the module)",
        -1073741819 => " (0xC0000005 ACCESS_VIOLATION - a native crash inside the module)",
        -1073741571 => " (0xC00000FD STACK_OVERFLOW)",
        -1073741795 => " (0xC000001D ILLEGAL_INSTRUCTION)",
        _ => string.Empty,
    };

    private static string DescribeSpan(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            return "unknown";
        }
        if (span.TotalMinutes < 1)
        {
            return $"{span.TotalSeconds:F0}s";
        }
        if (span.TotalHours < 1)
        {
            return $"{span.Minutes}m {span.Seconds}s";
        }
        return $"{(int)span.TotalHours}h {span.Minutes}m";
    }

    private static string FirstLine(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "(no message)";
        }
        var idx = text.IndexOfAny(new[] { '\r', '\n' });
        var line = idx < 0 ? text : text[..idx];
        return line.Length > 160 ? line[..160] + "..." : line;
    }

    private static string SafeReadAllText(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string ReadTail(string path, int lines)
    {
        try
        {
            if (!File.Exists(path))
            {
                return "(log not available)";
            }

            // Share-read: the live log is held open by the logging provider
            var all = SafeReadAllText(path).Split('\n');
            var tail = all.Length <= lines ? all : all[^lines..];
            return string.Join("\n", tail).TrimEnd();
        }
        catch (Exception e)
        {
            return $"(could not read log: {e.Message})";
        }
    }

    private static string AppVersion()
    {
        try
        {
            var version = Assembly.GetEntryAssembly()?.GetName().Version;
            return version?.ToString() ?? "unknown";
        }
        catch (Exception)
        {
            return "unknown";
        }
    }
}
