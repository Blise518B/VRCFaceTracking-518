using System.Diagnostics;

namespace VRCFaceTracking.Core.Services;

/// <summary>
/// When a module last delivered tracking data that was actually new, for senders that must
/// stop when tracking stalls (<see cref="NativeFaceTrackingSender"/>).
///
/// A packet arriving is no evidence: a module process answers every update poll with
/// whatever its module last wrote, so a stalled tracker (headset off, Virtual Desktop
/// paused, a camera app closed) keeps delivering its last frame 100 times a second. New
/// data is a frame that changes an eye or expression value the module owns
/// (<see cref="Sandboxing.IPC.ReplyUpdatePacket.TrackingChanged"/>).
///
/// Written by the IPC receive thread and the module supervisor, read by the send loop.
/// </summary>
public static class TrackingFreshness
{
    // Stopwatch timestamp of the last changed frame; 0 = none since the modules (re)started
    private static long _lastFresh;

    /// <summary>A module frame changed a tracking value.</summary>
    public static void MarkFresh() => Volatile.Write(ref _lastFresh, Stopwatch.GetTimestamp());

    /// <summary>
    /// No module is left that could deliver tracking: the last slot owner died or all modules
    /// were torn down. Readers treat the data as stale at once instead of waiting it out.
    /// </summary>
    public static void MarkLost() => Volatile.Write(ref _lastFresh, 0);

    /// <summary>Stopwatch timestamp of the last changed frame, or 0 when there is none.</summary>
    public static long LastFresh => Volatile.Read(ref _lastFresh);
}
