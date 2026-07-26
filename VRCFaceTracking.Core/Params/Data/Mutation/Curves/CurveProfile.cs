using System.Collections.Generic;

namespace VRCFaceTracking.Core.Params.Data.Mutation.Curves;

/// <summary>
/// A named set of response curves. Separate profiles exist because different tracking sources
/// need genuinely different shaping - a Virtual Desktop headset and a DIY ETVR rig do not
/// report the same ranges for the same expression, so one global curve set can't serve both.
/// </summary>
public class CurveProfile
{
    public string Name = DefaultName;

    /// <summary>Curves in this profile, keyed by <see cref="CurveChannel.Key"/>.</summary>
    public Dictionary<string, ResponseCurve> Curves = new();

    public const string DefaultName = "Default";

    public CurveProfile() { }

    public CurveProfile(string name)
    {
        Name = name;
    }

    /// <summary>Deep copy, for "duplicate this profile" so the copy edits independently.</summary>
    public CurveProfile CloneAs(string newName)
    {
        var copy = new CurveProfile(newName);
        foreach (var pair in Curves)
        {
            if (pair.Value != null)
            {
                copy.Curves[pair.Key] = pair.Value.Clone();
            }
        }
        return copy;
    }
}
