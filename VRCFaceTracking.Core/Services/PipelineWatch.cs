using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace VRCFaceTracking.Core.Services;

/// <summary>
/// Always-on performance watch for the tracking pipeline. Costs a few timestamp reads
/// per 100Hz iteration and answers, with numbers instead of guesses:
///
///  - is the send loop running on cadence, and when it stalls, WHERE did the time go
///    (mutations, parameter math, OSC encode/send, GC, or simply not being scheduled)?
///  - is module data actually arriving, so an input freeze (module/IPC side) can be
///    told apart from a pipeline freeze (our side)?
///
/// Written from the tracking loop only, except the module-data timestamp which the IPC
/// receive thread updates via Interlocked. A stall logs one warning line with the full
/// breakdown; a rolling summary is published for UI/debug consumption.
/// </summary>
public static class PipelineWatch
{
    /// <summary>Wired at startup; without it stalls simply go unlogged.</summary>
    public static ILogger? Logger;

    /// <summary>Iteration-to-iteration gap that counts as a stall (loop target is ~10ms).</summary>
    private const double LoopStallMs = 150;

    /// <summary>Module-data silence worth calling out once it recovers.</summary>
    private const double ModuleSilenceMs = 300;

    private const long SummaryEveryLoops = 6000; // ~1 minute at 100Hz

    /// <summary>Tick-to-tick gap the summary counts as slow - three missed ticks.</summary>
    private const double SlowTickMs = 30;

    private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

    // --- written by the tracking loop only ---
    private static long _lastLoopTs;
    private static double _sumGapMs, _maxGapMs;
    private static double _sumMutateMs, _maxMutateMs;
    private static double _sumParamsMs, _maxParamsMs;
    private static double _sumSendMs, _maxSendMs;
    private static long _loops;
    private static long _windowStartTs;
    private static int _stallsInWindow;

    // GC attribution. A collection suspends every managed thread, the send loop included,
    // wherever it is - so the process-wide pause total read once per tick says exactly how
    // much of that tick's gap was GC, and the rest was the loop not being scheduled.
    private static TimeSpan _lastTickGcPause;
    private static double _sumGcMs, _maxTickGcMs, _maxGapGcMs, _maxParamsGcMs;
    private static int _slowTicks, _slowTicksFromGc;
    private static int _gen0AtWindowStart, _gen1AtWindowStart, _gen2AtWindowStart;

    /// <summary>Set by UnifiedTracking.UpdateData; consumed by RecordLoop on the same thread.</summary>
    public static double LastMutateMs, LastParamsMs;

    /// <summary>GC pause inside the last parameters phase; set by UnifiedTracking.UpdateData.</summary>
    public static double LastParamsGcMs;

    /// <summary>Stalls seen since process start (for the debug UI).</summary>
    public static long TotalStalls { get; private set; }

    /// <summary>Human-readable rolling summary of the last window (for the debug UI).</summary>
    public static string LastSummary { get; private set; } = "collecting...";

    // --- written by the IPC receive thread ---
    private static long _lastModuleDataTs;

    /// <summary>
    /// Called for every tracking-data packet a module delivers. Detects input-side
    /// silences: if the gap since the previous packet was long, the freeze the user just
    /// saw was the module/IPC side, not this process's pipeline - and the log says so.
    /// </summary>
    public static void RecordModuleData()
    {
        var now = Stopwatch.GetTimestamp();
        var last = Interlocked.Exchange(ref _lastModuleDataTs, now);
        if (last == 0)
        {
            return;
        }

        var gapMs = (now - last) * TicksToMs;
        if (gapMs >= ModuleSilenceMs)
        {
            Logger?.LogWarning(
                "[PerfWatch] Module tracking data resumed after {gap} ms of silence - that freeze originated on the module/IPC side, not in the send pipeline.",
                (int)gapMs);
        }
    }

    /// <summary>Milliseconds since a module last delivered data, or -1 before the first packet.</summary>
    public static double ModuleDataAgeMs
    {
        get
        {
            var last = Interlocked.Read(ref _lastModuleDataTs);
            return last == 0 ? -1 : (Stopwatch.GetTimestamp() - last) * TicksToMs;
        }
    }

    /// <summary>
    /// Called once per send-loop iteration. <paramref name="sendMs"/> covers OSC encode +
    /// socket send; mutate/params times were stored by UpdateData just before.
    /// </summary>
    public static void RecordLoop(double sendMs)
    {
        var now = Stopwatch.GetTimestamp();
        var gcPause = GC.GetTotalPauseDuration();

        if (_lastLoopTs == 0)
        {
            _lastLoopTs = now;
            _windowStartTs = now;
            _lastTickGcPause = gcPause;
            StartGcWindow();
            return;
        }

        var gapMs = (now - _lastLoopTs) * TicksToMs;
        _lastLoopTs = now;

        var tickGcMs = (gcPause - _lastTickGcPause).TotalMilliseconds;
        _lastTickGcPause = gcPause;

        var mutateMs = LastMutateMs;
        var paramsMs = LastParamsMs;

        _loops++;
        _sumGapMs += gapMs;
        _sumMutateMs += mutateMs;
        _sumParamsMs += paramsMs;
        _sumSendMs += sendMs;
        _sumGcMs += tickGcMs;
        if (gapMs > _maxGapMs)
        {
            _maxGapMs = gapMs;
            _maxGapGcMs = tickGcMs;
        }
        if (mutateMs > _maxMutateMs) _maxMutateMs = mutateMs;
        if (paramsMs > _maxParamsMs)
        {
            _maxParamsMs = paramsMs;
            _maxParamsGcMs = LastParamsGcMs;
        }
        if (sendMs > _maxSendMs) _maxSendMs = sendMs;
        if (tickGcMs > _maxTickGcMs) _maxTickGcMs = tickGcMs;

        if (gapMs >= SlowTickMs)
        {
            _slowTicks++;
            // GC is the cause when it covers at least half of the time past the 10 ms target
            if (tickGcMs >= (gapMs - 10) / 2)
            {
                _slowTicksFromGc++;
            }
        }

        if (gapMs >= LoopStallMs)
        {
            TotalStalls++;
            _stallsInWindow++;

            // This tick's GC pause only. It used to be the pause since the last stall or
            // summary, which is how a 152 ms stall came to report "GC paused 214 ms of it".
            var moduleAge = ModuleDataAgeMs;
            Logger?.LogWarning(
                "[PerfWatch] Send loop stalled for {gap} ms (work last iteration: mutations {mutate:F2} ms, parameters {params:F2} ms, OSC send {send:F2} ms; GC paused {gc:F1} ms of it; threadpool backlog {backlog}; module data age {age} ms). "
                + "Time not accounted for by those numbers was spent blocked or unscheduled.",
                (int)gapMs, mutateMs, paramsMs, sendMs, tickGcMs,
                ThreadPool.PendingWorkItemCount,
                moduleAge < 0 ? -1 : (int)moduleAge);
        }

        if (_loops >= SummaryEveryLoops)
        {
            var windowSec = (now - _windowStartTs) * TicksToMs / 1000.0;
            var hz = _loops / Math.Max(windowSec, 0.001);
            var gen0 = GC.CollectionCount(0) - _gen0AtWindowStart; // counts every collection
            var gen1 = GC.CollectionCount(1) - _gen1AtWindowStart; // gen1 and gen2
            var gen2 = GC.CollectionCount(2) - _gen2AtWindowStart;
            LastSummary =
                $"loop {hz:F0}Hz (avg {_sumGapMs / _loops:F1}ms, max {_maxGapMs:F0}ms, GC {_maxGapGcMs:F0}ms of it) | " +
                $"mutations avg {_sumMutateMs / _loops * 1000:F0}us max {_maxMutateMs:F2}ms | " +
                $"parameters avg {_sumParamsMs / _loops * 1000:F0}us max {_maxParamsMs:F2}ms (GC {_maxParamsGcMs:F0}ms of it) | " +
                $"osc send avg {_sumSendMs / _loops * 1000:F0}us max {_maxSendMs:F2}ms | " +
                $"slow ticks (>={SlowTickMs:F0}ms) {_slowTicks}, {_slowTicksFromGc} of them GC | " +
                $"GCs gen0 {gen0 - gen1} gen1 {gen1 - gen2} gen2 {gen2}, paused {_sumGcMs:F0}ms (worst tick {_maxTickGcMs:F0}ms), heap {GC.GetTotalMemory(false) / (1024 * 1024)}MB | " +
                $"stalls {_stallsInWindow} (total {TotalStalls})";
            Logger?.LogDebug("[PerfWatch] {summary}", LastSummary);

            _loops = 0;
            _sumGapMs = _sumMutateMs = _sumParamsMs = _sumSendMs = 0;
            _maxGapMs = _maxMutateMs = _maxParamsMs = _maxSendMs = 0;
            _sumGcMs = _maxTickGcMs = _maxGapGcMs = _maxParamsGcMs = 0;
            _slowTicks = _slowTicksFromGc = 0;
            _stallsInWindow = 0;
            _windowStartTs = now;
            StartGcWindow();
        }
    }

    private static void StartGcWindow()
    {
        _gen0AtWindowStart = GC.CollectionCount(0);
        _gen1AtWindowStart = GC.CollectionCount(1);
        _gen2AtWindowStart = GC.CollectionCount(2);
    }
}
