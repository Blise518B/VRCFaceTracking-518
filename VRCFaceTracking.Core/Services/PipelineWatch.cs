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

    private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

    // --- written by the tracking loop only ---
    private static long _lastLoopTs;
    private static double _sumGapMs, _maxGapMs;
    private static double _sumMutateMs, _maxMutateMs;
    private static double _sumParamsMs, _maxParamsMs;
    private static double _sumSendMs, _maxSendMs;
    private static long _loops;
    private static long _windowStartTs;
    private static TimeSpan _lastGcPause;
    private static int _stallsInWindow;

    /// <summary>Set by UnifiedTracking.UpdateData; consumed by RecordLoop on the same thread.</summary>
    public static double LastMutateMs, LastParamsMs;

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

        if (_lastLoopTs == 0)
        {
            _lastLoopTs = now;
            _windowStartTs = now;
            _lastGcPause = GC.GetTotalPauseDuration();
            return;
        }

        var gapMs = (now - _lastLoopTs) * TicksToMs;
        _lastLoopTs = now;

        var mutateMs = LastMutateMs;
        var paramsMs = LastParamsMs;

        _loops++;
        _sumGapMs += gapMs;
        _sumMutateMs += mutateMs;
        _sumParamsMs += paramsMs;
        _sumSendMs += sendMs;
        if (gapMs > _maxGapMs) _maxGapMs = gapMs;
        if (mutateMs > _maxMutateMs) _maxMutateMs = mutateMs;
        if (paramsMs > _maxParamsMs) _maxParamsMs = paramsMs;
        if (sendMs > _maxSendMs) _maxSendMs = sendMs;

        if (gapMs >= LoopStallMs)
        {
            TotalStalls++;
            _stallsInWindow++;

            var gcPause = GC.GetTotalPauseDuration();
            var gcDeltaMs = (gcPause - _lastGcPause).TotalMilliseconds;
            _lastGcPause = gcPause;

            var moduleAge = ModuleDataAgeMs;
            Logger?.LogWarning(
                "[PerfWatch] Send loop stalled for {gap} ms (work last iteration: mutations {mutate:F2} ms, parameters {params:F2} ms, OSC send {send:F2} ms; GC paused {gc:F1} ms of it; threadpool backlog {backlog}; module data age {age} ms). "
                + "Time not accounted for by those numbers was spent blocked or unscheduled.",
                (int)gapMs, mutateMs, paramsMs, sendMs, gcDeltaMs,
                ThreadPool.PendingWorkItemCount,
                moduleAge < 0 ? -1 : (int)moduleAge);
        }

        if (_loops >= SummaryEveryLoops)
        {
            var windowSec = (now - _windowStartTs) * TicksToMs / 1000.0;
            var hz = _loops / Math.Max(windowSec, 0.001);
            LastSummary =
                $"loop {hz:F0}Hz (avg {_sumGapMs / _loops:F1}ms, max {_maxGapMs:F0}ms) | " +
                $"mutations avg {_sumMutateMs / _loops * 1000:F0}us max {_maxMutateMs:F2}ms | " +
                $"parameters avg {_sumParamsMs / _loops * 1000:F0}us max {_maxParamsMs:F2}ms | " +
                $"osc send avg {_sumSendMs / _loops * 1000:F0}us max {_maxSendMs:F2}ms | " +
                $"stalls {_stallsInWindow} (total {TotalStalls})";
            Logger?.LogDebug("[PerfWatch] {summary}", LastSummary);

            _loops = 0;
            _sumGapMs = _sumMutateMs = _sumParamsMs = _sumSendMs = 0;
            _maxGapMs = _maxMutateMs = _maxParamsMs = _maxSendMs = 0;
            _stallsInWindow = 0;
            _windowStartTs = now;
            _lastGcPause = GC.GetTotalPauseDuration();
        }
    }
}
