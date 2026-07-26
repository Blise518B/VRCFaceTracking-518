using System;
using System.Collections.Generic;
using System.Linq;
using VRCFaceTracking.Core.Params.Expressions;

namespace VRCFaceTracking.Core.Params.Data.Mutation.Curves;

/// <summary>
/// One tunable tracking value that can have a response curve attached. Covers more than the
/// unified expression shapes - notably eye openness, which lives in its own structure and is
/// therefore invisible to shape-only mutations.
/// </summary>
public sealed class CurveChannel
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required string Category { get; init; }

    /// <summary>
    /// Position in <see cref="CurveChannels.All"/>. Used for lock-free array lookups on the
    /// tracking hot path instead of hashing the key every frame.
    /// </summary>
    public int Index { get; internal set; }

    /// <summary>
    /// True for values that range -1..1 (gaze, head pose) instead of 0..1. These are mapped
    /// into 0..1 for the curve and back afterwards, so the editor always shows 0..1.
    /// </summary>
    public bool Bipolar { get; init; }

    public required Func<UnifiedTrackingData, float> Get { get; init; }
    public required Action<UnifiedTrackingData, float> Set { get; init; }
}

public static class CurveChannels
{
    public const string CategoryEye = "Eyes";
    public const string CategoryGaze = "Eye Gaze";
    public const string CategoryHead = "Head";
    public const string CategoryExpression = "Expressions";

    public static IReadOnlyList<CurveChannel> All { get; }

    private static readonly Dictionary<string, CurveChannel> ByKeyLookup;

    static CurveChannels()
    {
        var channels = new List<CurveChannel>
        {
            // Eye openness comes first: it's the most commonly uneven pair between left and right,
            // and it is NOT part of the unified expression shapes.
            new()
            {
                Key = "Eye.Openness.Left",
                Name = "Eye Openness Left",
                Category = CategoryEye,
                Get = d => d.Eye.Left.Openness,
                Set = (d, v) => d.Eye.Left.Openness = v,
            },
            new()
            {
                Key = "Eye.Openness.Right",
                Name = "Eye Openness Right",
                Category = CategoryEye,
                Get = d => d.Eye.Right.Openness,
                Set = (d, v) => d.Eye.Right.Openness = v,
            },

            new()
            {
                Key = "Eye.Gaze.Left.X",
                Name = "Gaze Left - Horizontal",
                Category = CategoryGaze,
                Bipolar = true,
                Get = d => d.Eye.Left.Gaze.x,
                Set = (d, v) => d.Eye.Left.Gaze.x = v,
            },
            new()
            {
                Key = "Eye.Gaze.Left.Y",
                Name = "Gaze Left - Vertical",
                Category = CategoryGaze,
                Bipolar = true,
                Get = d => d.Eye.Left.Gaze.y,
                Set = (d, v) => d.Eye.Left.Gaze.y = v,
            },
            new()
            {
                Key = "Eye.Gaze.Right.X",
                Name = "Gaze Right - Horizontal",
                Category = CategoryGaze,
                Bipolar = true,
                Get = d => d.Eye.Right.Gaze.x,
                Set = (d, v) => d.Eye.Right.Gaze.x = v,
            },
            new()
            {
                Key = "Eye.Gaze.Right.Y",
                Name = "Gaze Right - Vertical",
                Category = CategoryGaze,
                Bipolar = true,
                Get = d => d.Eye.Right.Gaze.y,
                Set = (d, v) => d.Eye.Right.Gaze.y = v,
            },

            new()
            {
                Key = "Head.Yaw",
                Name = "Head Rotation - Side to Side",
                Category = CategoryHead,
                Bipolar = true,
                Get = d => d.Head.HeadYaw,
                Set = (d, v) => d.Head.HeadYaw = v,
            },
            new()
            {
                Key = "Head.Pitch",
                Name = "Head Rotation - Up/Down",
                Category = CategoryHead,
                Bipolar = true,
                Get = d => d.Head.HeadPitch,
                Set = (d, v) => d.Head.HeadPitch = v,
            },
            new()
            {
                Key = "Head.Roll",
                Name = "Head Rotation - Tilt",
                Category = CategoryHead,
                Bipolar = true,
                Get = d => d.Head.HeadRoll,
                Set = (d, v) => d.Head.HeadRoll = v,
            },
            new()
            {
                Key = "Head.PosX",
                Name = "Head Position - Side to Side",
                Category = CategoryHead,
                Bipolar = true,
                Get = d => d.Head.HeadPosX,
                Set = (d, v) => d.Head.HeadPosX = v,
            },
            new()
            {
                Key = "Head.PosY",
                Name = "Head Position - Up/Down",
                Category = CategoryHead,
                Bipolar = true,
                Get = d => d.Head.HeadPosY,
                Set = (d, v) => d.Head.HeadPosY = v,
            },
            new()
            {
                Key = "Head.PosZ",
                Name = "Head Position - Forward/Back",
                Category = CategoryHead,
                Bipolar = true,
                Get = d => d.Head.HeadPosZ,
                Set = (d, v) => d.Head.HeadPosZ = v,
            },
        };

        // Every unified expression shape
        for (var i = 0; i < (int)UnifiedExpressions.Max; i++)
        {
            var index = i; // capture per iteration
            var name = ((UnifiedExpressions)i).ToString();
            channels.Add(new CurveChannel
            {
                Key = $"Shape.{name}",
                Name = name,
                Category = CategoryExpression,
                Get = d => d.Shapes[index].Weight,
                Set = (d, v) => d.Shapes[index].Weight = v,
            });
        }

        for (var i = 0; i < channels.Count; i++)
        {
            channels[i].Index = i;
        }

        All = channels;
        ByKeyLookup = channels.ToDictionary(c => c.Key, c => c);
    }

    public static CurveChannel? ByKey(string key) =>
        key != null && ByKeyLookup.TryGetValue(key, out var channel) ? channel : null;
}
