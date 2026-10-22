namespace VRCFaceTracking.Core.Contracts;

public interface IAvatarInfo
{
    public string Name { get; }
    public string Id { get; }

    /// <summary>
    /// The avatar was uploaded with "Use All Tracking Keys" in its Face Tracking settings, so
    /// VRChat drives and syncs its face itself from /tracking/face/v1 (see FaceTrackingBlob)
    /// and our v2 parameters are switched off for it.
    /// </summary>
    public bool FullFaceTracking { get; }

    public IParameterDefinition[] Parameters { get; }
}