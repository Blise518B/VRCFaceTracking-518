using VRCFaceTracking.Core.Params.Expressions;

namespace VRCFaceTracking.Core.Params.Data.Mutation;

/// <summary>
/// The panic switch for testing days: force the eye output, the face output, or both
/// to a calm neutral pose while the real data keeps flowing underneath.
///
/// Runs as the LAST mutation and only rewrites the outgoing buffer, never the raw
/// module data - so the Tracking Debug page still shows what the hardware is doing
/// (raw column moves, mut column sits at neutral), and switching back to live
/// tracking is instant. "Neutral" deliberately means a RESTING face, not a zeroed
/// struct: eyes open and centered reads as normal; openness 0 would leave the
/// avatar standing around with its eyes shut, which is the look this exists to avoid.
/// </summary>
public class NeutralOverride : TrackingMutation
{
    [MutationProperty("Hold EYES at neutral (open, centered)", true)]
    public bool neutralEyes = false;

    [MutationProperty("While eyes are held: glance around + blink like an idle avatar", true)]
    public bool idleEyes = true;

    [MutationProperty("Hold FACE at neutral (resting)", true)]
    public bool neutralFace = false;

    public override string Name => "Neutral Override";

    public override string Description =>
        "Testing safety net: hold the eye output and/or the face output at a calm neutral "
        + "pose while modules keep running. Toggle here or on the Tracking Debug page; "
        + "switching back to live tracking is instant.";

    public override MutationPriority Step => MutationPriority.Postprocessor;

    public override bool IsSaved => true;

    // Always active: MutateData is two branch checks when both toggles are off, and an
    // emergency switch that first needs enabling somewhere else is not an emergency switch.
    public override bool IsActive { get; set; } = true;

    // The eye stack owns shapes EyeSquintRight..BrowOuterUpLeft (squint/wide/brows come
    // from the eye cameras on this rig); everything after is the face camera's territory.
    // Matches the eye/expression slot split exactly.
    private const int FirstEyeShape = (int)UnifiedExpressions.EyeSquintRight;
    private const int LastEyeShape = (int)UnifiedExpressions.BrowOuterUpLeft;

    public override void MutateData(ref UnifiedTrackingData data)
    {
        if (neutralEyes)
        {
            float gazeX = 0f, gazeY = 0f, openness = 1f;
            if (idleEyes)
            {
                (gazeX, gazeY, openness) = IdleEyePose();
            }

            data.Eye.Left.Gaze.x = gazeX;
            data.Eye.Left.Gaze.y = gazeY;
            data.Eye.Right.Gaze.x = gazeX;
            data.Eye.Right.Gaze.y = gazeY;
            data.Eye.Left.Openness = openness;
            data.Eye.Right.Openness = openness;

            for (var i = FirstEyeShape; i <= LastEyeShape; i++)
            {
                data.Shapes[i].Weight = 0f;
            }
        }

        if (neutralFace)
        {
            for (var i = LastEyeShape + 1; i < data.Shapes.Length; i++)
            {
                data.Shapes[i].Weight = 0f;
            }
        }
    }

    // ------------------------------------------------------------- idle animation
    //
    // A small model of what resting eyes actually do, so a held avatar reads as
    // "person standing there" instead of "mannequin": fixate somewhere near center
    // for a moment, dart to a new spot in ~100-180ms (a saccade), drift very
    // slightly while fixating, and blink every few seconds (sometimes twice).
    // Runs on the 100Hz tracking thread: a handful of arithmetic ops, no allocations.

    private readonly Random _idleRandom = new();
    private long _lastIdleTick;
    private long _saccadeStart, _saccadeEnd;
    private long _fixationUntil;
    private float _fromX, _fromY, _toX, _toY;
    private long _blinkStart = -1;
    private long _nextBlinkAt;

    // Where idle gaze is allowed to wander (fraction of full range). Mostly
    // horizontal, like real glances; vertical stays subtle.
    private const float GazeRangeX = 0.30f;
    private const float GazeRangeY = 0.12f;

    private (float X, float Y, float Openness) IdleEyePose()
    {
        var now = Environment.TickCount64;

        // Freshly toggled on (or first run): restart the schedule so the first
        // saccade/blink land at natural times instead of firing all at once.
        if (now - _lastIdleTick > 1000)
        {
            _fromX = _toX = 0f;
            _fromY = _toY = 0f;
            _saccadeStart = _saccadeEnd = now;
            _fixationUntil = now + 600 + _idleRandom.Next(1200);
            _blinkStart = -1;
            _nextBlinkAt = now + 800 + _idleRandom.Next(2500);
        }
        _lastIdleTick = now;

        // Time for a new glance: dart from wherever we are to a fresh spot
        if (now >= _fixationUntil && now >= _saccadeEnd)
        {
            _fromX = _toX;
            _fromY = _toY;
            // Bias toward center: average two rolls so straight-ahead stays home base
            _toX = GazeRangeX * (float)(_idleRandom.NextDouble() + _idleRandom.NextDouble() - 1.0);
            _toY = GazeRangeY * (float)(_idleRandom.NextDouble() + _idleRandom.NextDouble() - 1.0);
            _saccadeStart = now;
            _saccadeEnd = now + 100 + _idleRandom.Next(80);
            _fixationUntil = _saccadeEnd + 500 + _idleRandom.Next(2500);
        }

        float x, y;
        if (now < _saccadeEnd)
        {
            // Mid-saccade: smoothstep between fixation points
            var p = (now - _saccadeStart) / (float)Math.Max(1, _saccadeEnd - _saccadeStart);
            var ease = p * p * (3f - 2f * p);
            x = _fromX + (_toX - _fromX) * ease;
            y = _fromY + (_toY - _fromY) * ease;
        }
        else
        {
            // Fixating: hold the point with a whisper of drift
            x = _toX + 0.012f * (float)Math.Sin(now * 0.00055);
            y = _toY + 0.008f * (float)Math.Sin(now * 0.00041 + 1.7);
        }

        // Blinks: fast close, brief hold, slower reopen (~230ms total)
        var openness = 1f;
        if (_blinkStart < 0 && now >= _nextBlinkAt)
        {
            _blinkStart = now;
        }
        if (_blinkStart >= 0)
        {
            var t = now - _blinkStart;
            if (t < 70)
            {
                openness = 1f - t / 70f;
            }
            else if (t < 110)
            {
                openness = 0f;
            }
            else if (t < 230)
            {
                openness = (t - 110) / 120f;
            }
            else
            {
                _blinkStart = -1;
                // Occasionally a quick double blink, otherwise a few seconds of calm
                _nextBlinkAt = now + (_idleRandom.Next(10) == 0
                    ? 250 + _idleRandom.Next(150)
                    : 1800 + _idleRandom.Next(4200));
            }
        }

        return (x, y, openness);
    }
}
