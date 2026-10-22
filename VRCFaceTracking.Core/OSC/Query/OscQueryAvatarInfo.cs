using VRCFaceTracking.Core.Contracts;

namespace VRCFaceTracking.Core.OSC.Query;

public class OscQueryAvatarInfo : IAvatarInfo
{
    public string Name { get; internal set; }

    public string Id { get; }

    public bool FullFaceTracking { get; }

    public IParameterDefinition[] Parameters { get; }

    public OscQueryAvatarInfo(OscQueryNode rootNode)
    {
        if (!rootNode.Contents.ContainsKey("parameters"))
        {
            return;
        }

        // /avatar/name is new in VRChat 2026.4.1 (open beta). Empty while VRChat doesn't serve
        // it; OscQueryConfigParser then takes the name from the legacy OSC config JSON.
        Name = FirstValue(rootNode, "name") as string ?? "";
        Id = rootNode.Contents.TryGetValue("change", out var change) ? change.Value[0] as string : "Unknown";
        // Set for avatars uploaded with "Use All Tracking Keys" (VRChat's native face tracking,
        // open beta). Not in VRChat's public docs yet; upstream reads the same node.
        FullFaceTracking = FirstValue(rootNode, "avatarfullfacetracking") is true;

        //TODO: Figure out a way to reconstruct the traditional address pattern instead of the whole thing.
        IEnumerable<IParameterDefinition> ConstructParameterArray(Dictionary<string, OscQueryNode> entries)
        {
            return entries
                .SelectMany(entry =>
                    entry.Value.Contents != null ? ConstructParameterArray(entry.Value.Contents) : !string.IsNullOrEmpty(entry.Value.OscType) ? new[] { new OscQueryParameterDef(entry.Value.FullPath, entry.Value) } : Array.Empty<OscQueryParameterDef>()
                );
        }
        
        Parameters = rootNode.Contents["parameters"].Contents
            .SelectMany(entry => 
                entry.Value.Contents != null ? ConstructParameterArray(entry.Value.Contents) : !string.IsNullOrEmpty(entry.Value.OscType) ? new[] { new OscQueryParameterDef(entry.Value.FullPath, entry.Value) } : Array.Empty<OscQueryParameterDef>()
            )
            .ToArray();
    }

    // First VALUE of a direct child of /avatar, or null when VRChat doesn't serve that node
    private static object? FirstValue(OscQueryNode rootNode, string child) =>
        rootNode.Contents.TryGetValue(child, out var node) && node.Value is { Length: > 0 } values
            ? values[0]
            : null;
}