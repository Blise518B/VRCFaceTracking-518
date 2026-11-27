using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using VRCFaceTracking.Contracts.Services;

namespace VRCFaceTracking.Services;

/// <summary>
/// Always-on watch for UI-thread freezes ("I clicked a page and the app hung"). A
/// background thread posts a no-op to the UI dispatcher ten times a second and times the
/// answer. When the answer takes longer than <see cref="ReportMs"/>, one log line says how
/// long, which page was showing (or being opened), and why, from three numbers taken over
/// the same span:
///
///  - the UI thread's own CPU time: close to the freeze length = it was busy working
///    (building a page, or work queued on it);
///  - the process-wide GC pause: a collection suspended it;
///  - the process priority class: BelowNormal while the freeze ran means it waited for CPU
///    behind other programs (Process Lasso's ProBalance lowers us like that).
///
/// A Debug summary per minute gives the slowest answer and how busy the UI thread was, so a
/// UI thread that is never idle shows up before it turns into a freeze.
/// </summary>
public static class UiWatch
{
    private const int PingIntervalMs = 100;
    private const double ReportMs = 400;   // a click answered this late already feels stuck
    private const double WarnMs = 2000;
    private const double StillFrozenMs = 5000;
    private const long SummaryEveryMs = 60_000;

    private const uint BelowNormalPriorityClass = 0x4000;
    private const uint IdlePriorityClass = 0x40;
    private const uint ThreadQueryLimitedInformation = 0x0800;

    private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

    private static ILogger? _logger;
    private static DispatcherQueue? _queue;
    private static IntPtr _uiThread;
    private static volatile string _page = "(starting)";
    private static long _pageSinceTs;
    private static long _answeredTs;
    private static readonly AutoResetEvent Answered = new(false);

    /// <summary>Call once, on the UI thread, after the main window exists.</summary>
    public static void Start(ILogger logger, INavigationService navigation)
    {
        if (_queue is not null)
        {
            return;
        }

        _logger = logger;
        _queue = DispatcherQueue.GetForCurrentThread();
        _uiThread = OpenThread(ThreadQueryLimitedInformation, false, GetCurrentThreadId());
        if (_queue is null)
        {
            return;
        }

        navigation.Navigated += (_, e) =>
        {
            _page = e.SourcePageType?.Name ?? "?";
            Volatile.Write(ref _pageSinceTs, Stopwatch.GetTimestamp());
        };

        new Thread(Run) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "UiWatch" }.Start();
    }

    private static void Run()
    {
        var summaryStartTs = Stopwatch.GetTimestamp();
        var summaryUiCpu = UiCpuMs();
        double slowestMs = 0;
        var freezes = 0;

        while (true)
        {
            var postTs = Stopwatch.GetTimestamp();
            var uiCpu0 = UiCpuMs();
            var gc0 = GC.GetTotalPauseDuration();
            var lowPriority = IsLowPriority();

            if (!_queue!.TryEnqueue(DispatcherQueuePriority.Normal, () =>
                {
                    Volatile.Write(ref _answeredTs, Stopwatch.GetTimestamp());
                    Answered.Set();
                }))
            {
                return; // The dispatcher is shutting down
            }

            // Wait for the answer, sampling the priority class while it is late: ProBalance
            // can lower it for a few seconds at any time. A freeze that goes on and on is
            // reported while it lasts, in case it never ends.
            var stillFrozenLogged = false;
            while (!Answered.WaitOne(PingIntervalMs))
            {
                lowPriority |= IsLowPriority();
                var waitedMs = (Stopwatch.GetTimestamp() - postTs) * TicksToMs;
                if (!stillFrozenLogged && waitedMs >= StillFrozenMs)
                {
                    stillFrozenLogged = true;
                    _logger?.LogWarning(
                        "The window has not responded for {ms:F0} ms and counting (on {page}). UI thread CPU so far {cpu:F0} ms, GC pause {gc:F0} ms, priority {priority}.",
                        waitedMs, _page, UiCpuMs() - uiCpu0, (GC.GetTotalPauseDuration() - gc0).TotalMilliseconds, PriorityName());
                }
            }

            var answeredTs = Volatile.Read(ref _answeredTs);
            var responseMs = (answeredTs - postTs) * TicksToMs;
            slowestMs = Math.Max(slowestMs, responseMs);

            if (responseMs >= ReportMs)
            {
                freezes++;
                Report(responseMs, UiCpuMs() - uiCpu0, (GC.GetTotalPauseDuration() - gc0).TotalMilliseconds,
                    lowPriority, postTs);
            }

            var now = Stopwatch.GetTimestamp();
            var windowMs = (now - summaryStartTs) * TicksToMs;
            if (windowMs >= SummaryEveryMs)
            {
                var uiCpuNow = UiCpuMs();
                _logger?.LogDebug(
                    "last {seconds:F0} s: slowest UI answer {slowest:F0} ms, {freezes} freezes >= {report:F0} ms, UI thread busy {busy:F0}% of a core, priority {priority}",
                    windowMs / 1000, slowestMs, freezes, ReportMs, 100 * (uiCpuNow - summaryUiCpu) / windowMs,
                    PriorityName());
                summaryStartTs = now;
                summaryUiCpu = uiCpuNow;
                slowestMs = 0;
                freezes = 0;
            }

            var spentMs = (Stopwatch.GetTimestamp() - postTs) * TicksToMs;
            if (spentMs < PingIntervalMs)
            {
                Thread.Sleep((int)(PingIntervalMs - spentMs));
            }
        }
    }

    private static void Report(double ms, double uiCpuMs, double gcMs, bool lowPriority, long postTs)
    {
        var verdict = gcMs >= 0.5 * ms
            ? "a garbage collection held it"
            : uiCpuMs >= 0.6 * ms
                ? "it was busy working (building or laying out a page, or a backlog of work queued on it)"
                : lowPriority
                    ? "it was mostly waiting for CPU time: the process ran at BelowNormal priority (Process Lasso ProBalance?) while other programs had the cores"
                    : "it was mostly not running: waiting for CPU time, or blocked";

        // Name the page switch when the freeze started within a second of it
        var page = _page;
        var sinceNavMs = (postTs - Volatile.Read(ref _pageSinceTs)) * TicksToMs;
        var where = sinceNavMs is >= -1000 and <= 1000 ? $"opening {page}" : $"on {page}";

        var level = ms >= WarnMs ? LogLevel.Warning : LogLevel.Information;
        _logger?.Log(level,
            "The window did not respond for {ms:F0} ms ({where}): {verdict}. UI thread CPU {cpu:F0} ms, GC pause {gc:F0} ms, priority {priority}{low}.",
            ms, where, verdict, uiCpuMs, gcMs, PriorityName(), lowPriority ? " (was BelowNormal during it)" : "");
    }

    private static double UiCpuMs()
    {
        if (_uiThread == IntPtr.Zero || !GetThreadTimes(_uiThread, out _, out _, out var kernel, out var user))
        {
            return 0;
        }

        return (kernel + user) / 10_000.0;
    }

    private static bool IsLowPriority()
    {
        var p = GetPriorityClass(GetCurrentProcess());
        return p is BelowNormalPriorityClass or IdlePriorityClass;
    }

    private static string PriorityName() => GetPriorityClass(GetCurrentProcess()) switch
    {
        0x40 => "Idle",
        0x4000 => "BelowNormal",
        0x20 => "Normal",
        0x8000 => "AboveNormal",
        0x80 => "High",
        0x100 => "Realtime",
        var other => $"0x{other:X}",
    };

    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern IntPtr OpenThread(uint access, bool inherit, uint threadId);
    [DllImport("kernel32.dll")] private static extern uint GetPriorityClass(IntPtr process);

    [DllImport("kernel32.dll")]
    private static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);
}
