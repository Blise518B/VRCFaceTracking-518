using VRCFaceTracking.Core.Params.Expressions;

namespace VRCFaceTracking.Core.Params.Data.Mutation;

/// <summary>
/// Optional translation layer for avatars tuned against Quest Pro face
/// tracking (the Virtual Desktop module). The Dream Air / Babble pipeline
/// fills the same unified expressions with different semantics; with this
/// mutation OFF everything passes through untouched, with it ON the signals
/// are reshaped to what QP-tuned avatars expect. Derived from the
/// verified-from-source diff in ETVRTrackingModule/QUESTPRO-PARITY.md:
///
///  - QP's EyeSquint (LidTightener) fires while half-closing the eyes - the
///    classic QP "half-closed look". Our squint signal does not. Synthesized
///    here from low eyelid openness.
///  - QP folded squint into openness (squinting closes the lids a bit).
///  - QP wrote MouthCornerSlant/MouthUpperDeepen as mirrors of the smile and
///    upper-lip weights; Babble never sends them.
///  - QP drove CheekSquint from CheekRaiser (fires when smiling); Babble has
///    no cheek-raise signal, so it is approximated from the smile.
///  - QP wrote per-eye EyeWide; ETVR as eye source never does. Off by
///    default: camera eyelids often rest near 1.0, which would pin widen on.
/// </summary>
public class QuestProParity : TrackingMutation
{
    [MutationProperty("Squint from eyelid (the QP half-closed look)", true)]
    public static bool squintFromLid = true;

    [MutationProperty("Squint begins below this openness", true, 0f, 1f)]
    public static float squintStart = 0.55f;

    [MutationProperty("Fully squinted at this openness", true, 0f, 1f)]
    public static float squintFull = 0.15f;

    [MutationProperty("Squint closes lids (QP openness coupling)", true, 0f, 1f)]
    public static float opennessCoupling = 0.35f;

    [MutationProperty("Mirror CornerSlant + UpperDeepen like QP did", true)]
    public static bool mirrorMouthShapes = true;

    [MutationProperty("Cheek squint from smile strength", true, 0f, 1f)]
    public static float cheekFromSmile = 0.5f;

    [MutationProperty("Eye widen from very open lids (tune before enabling)", true)]
    public static bool wideFromLid = false;

    [MutationProperty("Widen begins above this openness", true, 0f, 1f)]
    public static float wideStart = 0.9f;

    public override string Name => "Quest Pro Parity";

    public override string Description =>
        "Translates Dream Air / Babble tracking into the shapes Quest Pro avatars were tuned for. " +
        "Off = data passes through exactly as the modules produced it.";

    public override MutationPriority Step => MutationPriority.None;

    public override bool IsSaved => true;

    public override bool IsActive { get; set; } = false;

    public override void MutateData(ref UnifiedTrackingData data)
    {
        MutateEye(ref data, isRight: false);
        MutateEye(ref data, isRight: true);

        if (mirrorMouthShapes)
        {
            // On QP these were literally the same weight as the smile /
            // upper-lip raise, so mirroring is a faithful reconstruction.
            Raise(data, UnifiedExpressions.MouthCornerSlantLeft, data.Shapes[(int)UnifiedExpressions.MouthCornerPullLeft].Weight);
            Raise(data, UnifiedExpressions.MouthCornerSlantRight, data.Shapes[(int)UnifiedExpressions.MouthCornerPullRight].Weight);
            Raise(data, UnifiedExpressions.MouthUpperDeepenLeft, data.Shapes[(int)UnifiedExpressions.MouthUpperUpLeft].Weight);
            Raise(data, UnifiedExpressions.MouthUpperDeepenRight, data.Shapes[(int)UnifiedExpressions.MouthUpperUpRight].Weight);
        }

        if (cheekFromSmile > 0f)
        {
            // Approximation: a real smile raises the cheeks. QP measured that
            // directly (CheekRaiser); Babble cannot, so borrow the smile.
            Raise(data, UnifiedExpressions.CheekSquintLeft, cheekFromSmile * data.Shapes[(int)UnifiedExpressions.MouthCornerPullLeft].Weight);
            Raise(data, UnifiedExpressions.CheekSquintRight, cheekFromSmile * data.Shapes[(int)UnifiedExpressions.MouthCornerPullRight].Weight);
        }
    }

    private static void MutateEye(ref UnifiedTrackingData data, bool isRight)
    {
        ref var eye = ref (isRight ? ref data.Eye.Right : ref data.Eye.Left);
        var squintIndex = (int)(isRight ? UnifiedExpressions.EyeSquintRight : UnifiedExpressions.EyeSquintLeft);
        var wideIndex = (int)(isRight ? UnifiedExpressions.EyeWideRight : UnifiedExpressions.EyeWideLeft);

        var openness = eye.Openness;

        // Below ~0.03 the eye is a blink (the Tobii gate slams it to exactly
        // 0) - synthesizing squint there would spike the shape on every
        // blink, which QP avatars never saw either: their closed lids covered
        // everything. Leave the source squint alone during blinks.
        if (squintFromLid && openness > 0.03f && openness < squintStart && squintStart > squintFull)
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

        if (opennessCoupling > 0f)
        {
            // QP: openness = 1 - clamp01(EyesClosed + CheekRaiser * LidTightener),
            // i.e. squint eats into the lid. Multiplicative form keeps a blink
            // at exactly 0 and full-open unaffected while squint is 0.
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

    private static void Raise(UnifiedTrackingData data, UnifiedExpressions shape, float value)
    {
        if (value > data.Shapes[(int)shape].Weight)
        {
            data.Shapes[(int)shape].Weight = value;
        }
    }
}
