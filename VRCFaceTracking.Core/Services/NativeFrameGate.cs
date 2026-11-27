using System.Diagnostics;

namespace VRCFaceTracking.Core.Services;

/// <summary>
/// Decides, once per tracking tick, what <see cref="NativeFaceTrackingSender"/> sends: the
/// live blob while module data is fresh, one neutral blob once it has gone stale, and then
/// nothing until fresh data returns. Without the pause VRChat holds the last frame forever,
/// eyes shut or mouth open as the tracker left them, and never falls back to its own eye look
/// and blinking. VRChat resets the face itself once /tracking/face/v1 stops arriving.
///
/// Pure logic over Stopwatch timestamps, so the app and a test drive it the same way.
/// Send loop only: not thread-safe.
/// </summary>
public sealed class NativeFrameGate
{
    public enum Frame
    {
        /// <summary>Send nothing: tracking is stale and the neutral frame already went out, or nothing was sent yet.</summary>
        None,

        /// <summary>Send the live blob.</summary>
        Live,

        /// <summary>Send the live blob; tracking just came back after a pause.</summary>
        Resumed,

        /// <summary>Send the neutral blob once; tracking just went stale or its module went away.</summary>
        Neutral,
    }

    /// <summary>How long the data may stay unchanged before the face goes back to VRChat.</summary>
    public static readonly TimeSpan DefaultStaleAfter = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Ticks in a row the data has to look stale on before it counts. Our own GC pauses stop
    /// the IPC receive thread together with the send loop (150-810 ms each, several a minute in
    /// the 2026-11-08 log), so the first tick after one always finds old data even though the
    /// frames the module sent meanwhile are already waiting in the socket. Five ticks give the
    /// receive thread about 50 ms to catch up; a real stall pauses about 50 ms after the timeout.
    /// </summary>
    public const int StaleTicksNeeded = 5;

    private enum State
    {
        Idle,     // nothing sent since Reset, so there is no frozen face to clear
        Sending,  // live blobs are going out
        Paused,   // the neutral blob went out; silent until fresh data
    }

    private readonly long _staleAfter;
    private State _state = State.Idle;
    private int _staleTicks;

    public NativeFrameGate() : this(DefaultStaleAfter)
    {
    }

    public NativeFrameGate(TimeSpan staleAfter)
    {
        _staleAfter = (long)(staleAfter.TotalSeconds * Stopwatch.Frequency);
    }

    /// <param name="now">Stopwatch timestamp of this tick.</param>
    /// <param name="lastFresh">
    /// <see cref="TrackingFreshness.LastFresh"/>: when a module last changed a tracking value,
    /// or 0 when none has since the modules started or the last one went away.
    /// </param>
    public Frame Next(long now, long lastFresh)
    {
        var lost = lastFresh == 0;
        var old = lost || now - lastFresh > _staleAfter;
        _staleTicks = old ? Math.Min(_staleTicks + 1, StaleTicksNeeded) : 0;

        switch (_state)
        {
            case State.Sending:
                // A module that went away needs no grace: nothing is left to catch up
                if (!lost && _staleTicks < StaleTicksNeeded)
                {
                    return Frame.Live;
                }

                _state = State.Paused;
                return Frame.Neutral;

            case State.Paused:
                if (old)
                {
                    return Frame.None;
                }

                _state = State.Sending;
                return Frame.Resumed;

            default:
                if (old)
                {
                    return Frame.None;
                }

                _state = State.Sending;
                return Frame.Live;
        }
    }

    /// <summary>Back to idle, for a new avatar: nothing of ours is on its face yet.</summary>
    public void Reset()
    {
        _state = State.Idle;
        _staleTicks = 0;
    }
}
