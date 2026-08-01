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
            data.Eye.Left.Gaze.x = 0f;
            data.Eye.Left.Gaze.y = 0f;
            data.Eye.Right.Gaze.x = 0f;
            data.Eye.Right.Gaze.y = 0f;
            data.Eye.Left.Openness = 1f;
            data.Eye.Right.Openness = 1f;

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
}
