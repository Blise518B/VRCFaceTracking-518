using Newtonsoft.Json;
using VRCFaceTracking.Core.Params.Expressions;

namespace VRCFaceTracking.Core.Params.Data.Mutation;

/// <summary>
/// Universal dialect translation: run any tracking hardware, keep avatars
/// tuned for any ecosystem. The unified expressions are already the common
/// language - what differs per ecosystem is which shapes get synthesized,
/// mirrored or coupled (verified from all three modules' source, see
/// ETVRTrackingModule/QUESTPRO-PARITY.md). This mutation reshapes the merged
/// data into the selected target dialect:
///
///  - Quest Pro: squint fires while half-closing, squint eats into openness,
///    CornerSlant/UpperDeepen mirror smile/UpperUp, CheekSquint from smile,
///    optionally the QP-authentic constant pupil.
///  - Vive: brows mirrored from eye-wide/eye-squeeze (that is literally what
///    SRanipal avatars were tuned against), Dimple/CheekSquint/CornerSlant
///    mirrored from the smile. SRanipal's stretch-from-right-sad bug is NOT
///    reproduced.
///  - Off: raw passthrough - the native Dream Air / Babble dialect.
///
/// Transforms that would double-apply are gated on the auto-detected source
/// (e.g. openness coupling is skipped when a Quest Pro module already feeds
/// the eye slot), and every synthesis uses max() so a real signal from a
/// module always beats the approximation.
///
/// Fields are INSTANCE fields on purpose: the settings serializer ignores
/// statics, which is why the old Quest Pro Parity card forgot its sliders.
/// </summary>
public class TrackingCompatibility : TrackingMutation
{
    public const int ProfileOff = 0;
    public const int ProfileQuestPro = 1;
    public const int ProfileVive = 2;

    /// <summary>Set by the app: name of the module currently owning the eye slot.</summary>
    [JsonIgnore] public static Func<string?>? EyeOwnerResolver;

    /// <summary>Target dialect; controlled from the Compatibility page.</summary>
    public int profile = ProfileOff;

    [MutationProperty("Squint begins below this openness", true, 0f, 1f)]
    public float squintStart = 0.55f;

    [MutationProperty("Fully squinted at this openness", true, 0f, 1f)]
    public float squintFull = 0.15f;

    [MutationProperty("Squint closes lids (Quest Pro coupling)", true, 0f, 1f)]
    public float opennessCoupling = 0.35f;

    [MutationProperty("Cheek squint / dimple from smile strength", true, 0f, 1f)]
    public float cheekFromSmile = 0.5f;

    [MutationProperty("Quest Pro: constant pupil like the real thing", true)]
    public bool qpConstantPupil = true;

    [MutationProperty("Eye widen from very open lids", true)]
    public bool wideFromLid = false;

    [MutationProperty("Widen begins above this openness", true, 0f, 1f)]
    public float wideStart = 0.9f;

    public override string Name => "Tracking Compatibility";

    public override string Description =>
        "Translates whatever hardware is running into the dialect your avatar was tuned for " +
        "(Quest Pro, Vive, or raw). Controlled from the Compatibility page.";

    public override MutationPriority Step => MutationPriority.None;

    public override bool IsSaved => true;

    public override bool IsActive { get; set; } = false;

    public static string DialectOf(string? moduleName)
    {
        if (string.IsNullOrWhiteSpace(moduleName))
        {
            return "none";
        }

        var n = moduleName.ToLowerInvariant();
        if (n.Contains("virtual desktop") || n.Contains("questpro") || n.Contains("quest pro"))
        {
            return "questpro";
        }
        if (n.Contains("sranipal") || n.Contains("vive"))
        {
            return "vive";
        }
        if (n.Contains("dreamair") || n.Contains("etvr") || n.Contains("babble"))
        {
            return "dreamair";
        }
        return "other";
    }

    // The eye-owner lookup resolves a service and walks a UI-bound collection, so it is
    // not something to do 100 times a second on the tracking thread. The owning module
    // changes on the timescale of headset swaps; once a second is generous.
    [JsonIgnore] private long _dialectCheckedAt;
    [JsonIgnore] private string _eyeDialect = "none";

    public override void MutateData(ref UnifiedTrackingData data)
    {
        switch (profile)
        {
            case ProfileQuestPro:
                var now = Environment.TickCount64;
                if (now - _dialectCheckedAt >= 1000)
                {
                    _dialectCheckedAt = now;
                    try
                    {
                        _eyeDialect = DialectOf(EyeOwnerResolver?.Invoke());
                    }
                    catch (Exception)
                    {
                        // A UI-thread mutation of the module list mid-walk must not
                        // take down the tracking loop; keep the previous dialect.
                    }
                }
                ApplyQuestPro(ref data, _eyeDialect);
                break;
            case ProfileVive:
                ApplyVive(ref data);
                break;
        }
    }

    private void ApplyQuestPro(ref UnifiedTrackingData data, string eyeDialect)
    {
        QuestProEye(ref data, eyeDialect, isRight: false);
        QuestProEye(ref data, eyeDialect, isRight: true);

        // On the real QP these were literally the smile / upper-lip weights.
        Raise(data, UnifiedExpressions.MouthCornerSlantLeft, data.Shapes[(int)UnifiedExpressions.MouthCornerPullLeft].Weight);
        Raise(data, UnifiedExpressions.MouthCornerSlantRight, data.Shapes[(int)UnifiedExpressions.MouthCornerPullRight].Weight);
        Raise(data, UnifiedExpressions.MouthUpperDeepenLeft, data.Shapes[(int)UnifiedExpressions.MouthUpperUpLeft].Weight);
        Raise(data, UnifiedExpressions.MouthUpperDeepenRight, data.Shapes[(int)UnifiedExpressions.MouthUpperUpRight].Weight);

        if (cheekFromSmile > 0f)
        {
            Raise(data, UnifiedExpressions.CheekSquintLeft, cheekFromSmile * data.Shapes[(int)UnifiedExpressions.MouthCornerPullLeft].Weight);
            Raise(data, UnifiedExpressions.CheekSquintRight, cheekFromSmile * data.Shapes[(int)UnifiedExpressions.MouthCornerPullRight].Weight);
        }

        if (qpConstantPupil)
        {
            // The real QP module hardcoded 5 mm in a forced 0..10 range, so
            // QP-tuned avatars saw dilation frozen at 0.5. Reproduce exactly.
            data.Eye.Left.PupilDiameter_MM = 5f;
            data.Eye.Right.PupilDiameter_MM = 5f;
            data.Eye._minDilation = 0f;
            data.Eye._maxDilation = 10f;
        }
    }

    private void QuestProEye(ref UnifiedTrackingData data, string eyeDialect, bool isRight)
    {
        ref var eye = ref (isRight ? ref data.Eye.Right : ref data.Eye.Left);
        var squintIndex = (int)(isRight ? UnifiedExpressions.EyeSquintRight : UnifiedExpressions.EyeSquintLeft);
        var wideIndex = (int)(isRight ? UnifiedExpressions.EyeWideRight : UnifiedExpressions.EyeWideLeft);

        var openness = eye.Openness;

        // Below ~0.03 the eye is a blink (the Tobii gate slams it to exactly
        // 0) - synthesizing squint there would spike the shape every blink.
        if (openness > 0.03f && openness < squintStart && squintStart > squintFull)
        {
            var t = (squintStart - openness) / (squintStart - squintFull);
            if (t > 1f)
            {
                t = 1f;
            }
            if (data.Shapes[squintIndex].Weight < t)
            {
                data.Shapes[squintIndex].Weight = t;
            }
        }

        // A Quest Pro source already folded squint into openness - coupling
        // again would over-close the lids.
        if (opennessCoupling > 0f && eyeDialect != "questpro")
        {
            eye.Openness = openness * (1f - opennessCoupling * data.Shapes[squintIndex].Weight);
        }

        if (wideFromLid && openness > wideStart && wideStart < 1f)
        {
            var w = (openness - wideStart) / (1f - wideStart);
            if (w > 1f)
            {
                w = 1f;
            }
            if (data.Shapes[wideIndex].Weight < w)
            {
                data.Shapes[wideIndex].Weight = w;
            }
        }
    }

    private void ApplyVive(ref UnifiedTrackingData data)
    {
        // SRanipal avatars were tuned against brows that ARE eye-wide and
        // eye-squeeze - the Vive module has no brow tracking at all.
        Raise(data, UnifiedExpressions.BrowInnerUpLeft, data.Shapes[(int)UnifiedExpressions.EyeWideLeft].Weight);
        Raise(data, UnifiedExpressions.BrowOuterUpLeft, data.Shapes[(int)UnifiedExpressions.EyeWideLeft].Weight);
        Raise(data, UnifiedExpressions.BrowInnerUpRight, data.Shapes[(int)UnifiedExpressions.EyeWideRight].Weight);
        Raise(data, UnifiedExpressions.BrowOuterUpRight, data.Shapes[(int)UnifiedExpressions.EyeWideRight].Weight);
        Raise(data, UnifiedExpressions.BrowPinchLeft, data.Shapes[(int)UnifiedExpressions.EyeSquintLeft].Weight);
        Raise(data, UnifiedExpressions.BrowLowererLeft, data.Shapes[(int)UnifiedExpressions.EyeSquintLeft].Weight);
        Raise(data, UnifiedExpressions.BrowPinchRight, data.Shapes[(int)UnifiedExpressions.EyeSquintRight].Weight);
        Raise(data, UnifiedExpressions.BrowLowererRight, data.Shapes[(int)UnifiedExpressions.EyeSquintRight].Weight);

        // SRanipal mirrored these from the smile.
        Raise(data, UnifiedExpressions.MouthCornerSlantLeft, data.Shapes[(int)UnifiedExpressions.MouthCornerPullLeft].Weight);
        Raise(data, UnifiedExpressions.MouthCornerSlantRight, data.Shapes[(int)UnifiedExpressions.MouthCornerPullRight].Weight);
        Raise(data, UnifiedExpressions.MouthDimpleLeft, data.Shapes[(int)UnifiedExpressions.MouthCornerPullLeft].Weight);
        Raise(data, UnifiedExpressions.MouthDimpleRight, data.Shapes[(int)UnifiedExpressions.MouthCornerPullRight].Weight);

        if (cheekFromSmile > 0f)
        {
            Raise(data, UnifiedExpressions.CheekSquintLeft, cheekFromSmile * data.Shapes[(int)UnifiedExpressions.MouthCornerPullLeft].Weight);
            Raise(data, UnifiedExpressions.CheekSquintRight, cheekFromSmile * data.Shapes[(int)UnifiedExpressions.MouthCornerPullRight].Weight);
        }
    }

    private static void Raise(UnifiedTrackingData data, UnifiedExpressions shape, float value)
    {
        if (value > data.Shapes[(int)shape].Weight)
        {
            data.Shapes[(int)shape].Weight = value;
        }
    }
}
