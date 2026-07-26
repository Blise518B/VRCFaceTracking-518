using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using VRCFaceTracking.Core.Params.Data.Mutation.Curves;

namespace VRCFaceTracking.Core.Params.Data.Mutation;

/// <summary>
/// Applies a user-drawn response curve to any tracking channel - every unified expression plus
/// the values that live outside the shape array (left/right eye openness, gaze, head pose).
/// Curves are edited on the Curve Editor page. Every curve starts as a straight 1:1 line and
/// is individually enabled, so this mutation is a no-op until the user actually draws something.
/// </summary>
/// <remarks>
/// Thread safety: <see cref="MutateData"/> runs on the tracking thread while the UI adds curves
/// and edits points. The hot path therefore only touches fixed-size arrays (atomic reference and
/// float slots) and never the <see cref="Curves"/> dictionary, which belongs to the UI thread.
/// </remarks>
public class CurveCalibration : TrackingMutation
{
    /// <summary>
    /// Curves keyed by <see cref="CurveChannel.Key"/>. Only channels the user has touched are
    /// stored. Owned by the UI thread; the tracking thread reads <see cref="_curveByIndex"/>.
    /// </summary>
    public Dictionary<string, ResponseCurve> Curves = new();

    [JsonIgnore] private readonly ResponseCurve?[] _curveByIndex = new ResponseCurve?[CurveChannels.All.Count];
    [JsonIgnore] private readonly float[] _liveByIndex = new float[CurveChannels.All.Count];
    [JsonIgnore] private long _lastRecordMs;

    public override string Name => "Curve Editor";

    public override string Description =>
        "Applies per-channel response curves drawn on the Curve Editor page (including left/right " +
        "eye openness, which normal expression mutations can't reach). Has no effect until you " +
        "bend a curve there.";

    public override MutationPriority Step => MutationPriority.None;

    public override bool IsSaved => true;

    public override bool IsActive { get; set; } = false;

    public override void Initialize(UnifiedTrackingData data) => EnsureValid();

    /// <summary>
    /// Validates deserialized curves, drops ones for channels that no longer exist, and rebuilds
    /// the index lookup the tracking thread uses.
    /// </summary>
    private void EnsureValid()
    {
        Curves ??= new Dictionary<string, ResponseCurve>();

        var stale = new List<string>();
        foreach (var pair in Curves)
        {
            var channel = CurveChannels.ByKey(pair.Key);
            if (channel == null || pair.Value == null)
            {
                stale.Add(pair.Key);
                continue;
            }

            pair.Value.EnsureValid();
            _curveByIndex[channel.Index] = pair.Value;
        }

        foreach (var key in stale)
        {
            Curves.Remove(key);
        }
    }

    /// <summary>
    /// Returns the stored curve for a channel, creating a default 1:1 curve on first access.
    /// Call from the UI thread.
    /// </summary>
    public ResponseCurve GetOrCreateCurve(string channelKey)
    {
        var channel = CurveChannels.ByKey(channelKey)
                      ?? throw new ArgumentException($"Unknown curve channel '{channelKey}'", nameof(channelKey));

        if (Curves.TryGetValue(channelKey, out var curve) && curve != null)
        {
            _curveByIndex[channel.Index] = curve;
            return curve;
        }

        curve = ResponseCurve.Identity();
        Curves[channelKey] = curve;
        _curveByIndex[channel.Index] = curve; // reference write - safe for the tracking thread
        return curve;
    }

    public override void MutateData(ref UnifiedTrackingData data)
    {
        var channels = CurveChannels.All;

        for (var i = 0; i < channels.Count; i++)
        {
            var channel = channels[i];
            var raw = channel.Get(data);

            if (float.IsNaN(raw) || float.IsInfinity(raw))
            {
                continue;
            }

            // Record the untouched input so the editor can show a live position indicator
            _liveByIndex[i] = raw;

            var curve = _curveByIndex[i];
            if (curve is not { Enabled: true })
            {
                continue;
            }

            // Bipolar channels are folded into 0..1 for the curve, then expanded back
            if (channel.Bipolar)
            {
                var normalized = Math.Clamp((raw + 1f) / 2f, 0f, 1f);
                channel.Set(data, curve.Evaluate(normalized) * 2f - 1f);
            }
            else
            {
                channel.Set(data, curve.Evaluate(Math.Clamp(raw, 0f, 1f)));
            }
        }

        _lastRecordMs = Environment.TickCount64;
    }

    /// <summary>
    /// Current raw (pre-curve) value for a channel, for the editor's live indicator. Falls back
    /// to the app's live tracking data when this mutation isn't running, so the editor still
    /// shows movement before the user enables it.
    /// </summary>
    public float GetLiveInput(string channelKey)
    {
        var channel = CurveChannels.ByKey(channelKey);
        if (channel == null)
        {
            return 0f;
        }

        if (Environment.TickCount64 - _lastRecordMs < 500)
        {
            return _liveByIndex[channel.Index];
        }

        try
        {
            var value = channel.Get(UnifiedTracking.Data);
            return float.IsNaN(value) || float.IsInfinity(value) ? 0f : value;
        }
        catch (Exception)
        {
            return 0f;
        }
    }

    [MutationButton("Reset ALL Curves")]
    public void ResetAllCurves()
    {
        for (var i = 0; i < _curveByIndex.Length; i++)
        {
            _curveByIndex[i] = null;
        }
        Curves.Clear();
        Logger?.LogInformation("Curve Editor: all curves reset to 1:1.");
    }
}
