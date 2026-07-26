using System;
using System.Collections.Generic;
using System.Linq;
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
    /// Named curve sets, so each tracking source can have its own shaping. Keyed by profile name.
    /// </summary>
    public Dictionary<string, CurveProfile> Profiles = new();

    /// <summary>Profile in use when <see cref="AutoSwitchBySource"/> is off, or as the fallback.</summary>
    public string ActiveProfileName = CurveProfile.DefaultName;

    /// <summary>
    /// Follow the detected tracking source, so unplugging one rig and using another swaps
    /// curve sets without touching anything.
    /// </summary>
    public bool AutoSwitchBySource = true;

    /// <summary>
    /// Supplies the name of the source currently streaming, for auto-switching. Set once at
    /// startup: mutations are constructed by reflection with no constructor args, so they
    /// can't be given the detector through DI.
    /// </summary>
    [JsonIgnore] public static Func<string?>? SourceNameResolver;

    [JsonIgnore] private readonly ResponseCurve?[] _curveByIndex = new ResponseCurve?[CurveChannels.All.Count];
    [JsonIgnore] private readonly float[] _liveByIndex = new float[CurveChannels.All.Count];
    [JsonIgnore] private long _lastRecordMs;
    [JsonIgnore] private long _lastProfileCheckMs;
    [JsonIgnore] private string _resolvedProfileName = CurveProfile.DefaultName;
    [JsonIgnore] private const int ProfileRecheckIntervalMs = 2000;

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
    /// Validates every profile's curves, drops ones for channels that no longer exist, and
    /// rebuilds the lookup the tracking thread uses.
    /// </summary>
    private void EnsureValid()
    {
        Profiles ??= new Dictionary<string, CurveProfile>();

        if (Profiles.Count == 0)
        {
            GetOrCreateProfile(CurveProfile.DefaultName);
        }

        foreach (var profile in Profiles.Values)
        {
            if (profile == null)
            {
                continue;
            }

            profile.Curves ??= new Dictionary<string, ResponseCurve>();
            var stale = new List<string>();
            foreach (var pair in profile.Curves)
            {
                if (CurveChannels.ByKey(pair.Key) == null || pair.Value == null)
                {
                    stale.Add(pair.Key);
                    continue;
                }
                pair.Value.EnsureValid();
            }
            foreach (var key in stale)
            {
                profile.Curves.Remove(key);
            }
        }

        if (string.IsNullOrWhiteSpace(ActiveProfileName) || !Profiles.ContainsKey(ActiveProfileName))
        {
            ActiveProfileName = Profiles.Keys.First();
        }

        _resolvedProfileName = ActiveProfileName;
        RebuildIndex();
    }

    /// <summary>All profile names, for the UI's picker.</summary>
    public IEnumerable<string> ProfileNames => Profiles.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase);

    /// <summary>The profile whose curves are currently being applied.</summary>
    public string EffectiveProfileName => _resolvedProfileName;

    public CurveProfile GetOrCreateProfile(string name)
    {
        Profiles ??= new Dictionary<string, CurveProfile>();
        if (!Profiles.TryGetValue(name, out var profile) || profile == null)
        {
            profile = new CurveProfile(name);
            Profiles[name] = profile;
        }
        return profile;
    }

    /// <summary>Switches the profile being edited/applied and re-points the tracking lookup.</summary>
    public void SetActiveProfile(string name)
    {
        GetOrCreateProfile(name);
        ActiveProfileName = name;
        _resolvedProfileName = name;
        RebuildIndex();
    }

    public bool DeleteProfile(string name)
    {
        // Always keep one profile, otherwise there is nothing to edit
        if (Profiles.Count <= 1 || !Profiles.ContainsKey(name))
        {
            return false;
        }

        Profiles.Remove(name);
        if (ActiveProfileName == name)
        {
            SetActiveProfile(Profiles.Keys.First());
        }
        return true;
    }

    public CurveProfile DuplicateProfile(string source, string newName)
    {
        var from = GetOrCreateProfile(source);
        var copy = from.CloneAs(newName);
        Profiles[newName] = copy;
        return copy;
    }

    /// <summary>
    /// Points the index lookup at the effective profile's curves. Array element writes are
    /// atomic, so the tracking thread can keep reading while this runs.
    /// </summary>
    private void RebuildIndex()
    {
        var profile = GetOrCreateProfile(_resolvedProfileName);
        for (var i = 0; i < _curveByIndex.Length; i++)
        {
            var key = CurveChannels.All[i].Key;
            _curveByIndex[i] = profile.Curves.TryGetValue(key, out var curve) ? curve : null;
        }
    }

    /// <summary>
    /// Returns the stored curve for a channel in the effective profile, creating a default 1:1
    /// curve on first access. Call from the UI thread.
    /// </summary>
    public ResponseCurve GetOrCreateCurve(string channelKey)
    {
        var channel = CurveChannels.ByKey(channelKey)
                      ?? throw new ArgumentException($"Unknown curve channel '{channelKey}'", nameof(channelKey));

        var profile = GetOrCreateProfile(_resolvedProfileName);
        if (profile.Curves.TryGetValue(channelKey, out var curve) && curve != null)
        {
            _curveByIndex[channel.Index] = curve;
            return curve;
        }

        curve = ResponseCurve.Identity();
        profile.Curves[channelKey] = curve;
        _curveByIndex[channel.Index] = curve; // reference write - safe for the tracking thread
        return curve;
    }

    /// <summary>
    /// Follows the detected source when auto-switching is on. Rate-limited rather than checked
    /// per frame, since this runs on the 100Hz tracking path.
    /// </summary>
    private void UpdateProfileForSource()
    {
        if (!AutoSwitchBySource || SourceNameResolver == null)
        {
            return;
        }

        var now = Environment.TickCount64;
        if (now - _lastProfileCheckMs < ProfileRecheckIntervalMs)
        {
            return;
        }
        _lastProfileCheckMs = now;

        string? sourceName;
        try
        {
            sourceName = SourceNameResolver();
        }
        catch (Exception)
        {
            return;
        }

        // No source detected: stay on whatever is loaded rather than yanking curves away
        if (string.IsNullOrWhiteSpace(sourceName) || sourceName == _resolvedProfileName)
        {
            return;
        }

        _resolvedProfileName = sourceName;
        GetOrCreateProfile(sourceName);
        RebuildIndex();
        Logger?.LogInformation("Curve Editor: switched to profile \"{profile}\" for the active source.", sourceName);
    }

    public override void MutateData(ref UnifiedTrackingData data)
    {
        UpdateProfileForSource();

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

    [MutationButton("Reset Curves In This Profile")]
    public void ResetAllCurves()
    {
        GetOrCreateProfile(_resolvedProfileName).Curves.Clear();
        for (var i = 0; i < _curveByIndex.Length; i++)
        {
            _curveByIndex[i] = null;
        }
        Logger?.LogInformation("Curve Editor: curves in \"{profile}\" reset to 1:1.", _resolvedProfileName);
    }
}
