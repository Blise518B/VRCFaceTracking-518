using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;

namespace VRCFaceTracking.ModuleProcess;

/// <summary>
/// Shares the host's tracking contract across the module boundary. Default binding
/// rejects a module that references a newer Core/SDK version than this app's identity
/// even when the APIs match (a 6.0 module against the 5.4.5 identity, before the bump
/// to 6.0.0.0). Loading private SDK/Core copies would also break casts and send
/// tracking updates to a different UnifiedTracking static state.
/// </summary>
internal sealed class ModuleLoadContext(string modulePath) : AssemblyLoadContext(modulePath, isCollectible: true)
{
    // Validate direct contract type/member references before constructing a module
    // that requests a newer identity. A genuinely missing API must be a load failure,
    // not a MissingMethodException much later in Initialize() or Update().
    internal static void ValidateTrackingContract(Assembly assembly, string modulePath)
    {
        using var stream = File.OpenRead(modulePath);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var contractTypes = new HashSet<TypeReferenceHandle>();
        foreach ( var handle in metadata.TypeReferences )
        {
            var type = metadata.GetTypeReference(handle);
            if ( type.ResolutionScope.Kind != HandleKind.AssemblyReference ) continue;
            var reference = metadata.GetAssemblyReference((AssemblyReferenceHandle)type.ResolutionScope);
            if ( metadata.GetString(reference.Name) is not ("VRCFaceTracking.SDK" or "VRCFaceTracking.Core") ) continue;
            assembly.ManifestModule.ResolveType(MetadataTokens.GetToken(handle));
            contractTypes.Add(handle);
        }

        foreach ( var handle in metadata.MemberReferences )
        {
            var member = metadata.GetMemberReference(handle);
            if ( member.Parent.Kind == HandleKind.TypeReference &&
                 contractTypes.Contains((TypeReferenceHandle)member.Parent) )
            {
                assembly.ManifestModule.ResolveMember(MetadataTokens.GetToken(handle));
            }
        }
    }

    protected override Assembly? Load(AssemblyName assemblyName) => assemblyName.Name switch
    {
        "VRCFaceTracking.SDK" => typeof(ExtTrackingModule).Assembly,
        "VRCFaceTracking.Core" => typeof(UnifiedTracking).Assembly,
        _ => null,
    };
}
