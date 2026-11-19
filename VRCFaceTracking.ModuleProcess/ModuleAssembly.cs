using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace VRCFaceTracking.ModuleProcess;
public class ModuleAssembly
{
    public Assembly Assembly;
    public string ModulePath;
    public bool Loaded;
    private ILogger<ModuleProcessMain> _logger;
    private ILoggerFactory? _loggerFactory;
    public ExtTrackingModule TrackingModule;
    public CancellationTokenSource _updateCts;

    public ModuleAssembly(ILogger<ModuleProcessMain> logger, ILoggerFactory loggerFactory, string dllPath)
    {
        if ( !File.Exists(dllPath) )
        {
            throw new ArgumentException($"Invalid file path: \"{dllPath}\" does not exist!");
        }
        if ( Path.GetExtension(dllPath.ToLowerInvariant()) != ".dll" && Path.GetExtension(dllPath.ToLowerInvariant()) != "dll" )
        {
            throw new ArgumentException($"{dllPath} is not a DLL file and cannot be loaded.");
        }

        _logger         = logger;
        _loggerFactory  = loggerFactory;
        ModulePath      = dllPath;
        Loaded          = false;
    }

    public bool TryLoadAssembly()
    {
        if ( Loaded )
        {
            return true;
        }

        try
        {
            var alc = new ModuleLoadContext(ModulePath);
            Assembly = alc.LoadFromAssemblyPath(ModulePath);

            var references = Assembly.GetReferencedAssemblies();
            var oldRefs = false;
            var validateSharedContract = false;
            foreach ( var reference in references )
            {
                // Modules built against a newer upstream (QFTPlus .NET 10 builds asked
                // for 6.0 while this app was still 5.4.5) share the host types when the
                // API they reference is here; that keeps one UnifiedTracking static state.
                // A newer minor release is checked member by member below. A newer major
                // one is refused outright: that is where upstream breaks its module API.
                var hostAssembly = reference.Name switch
                {
                    "VRCFaceTracking.SDK" => typeof(ExtTrackingModule).Assembly.GetName(),
                    "VRCFaceTracking.Core" => typeof(UnifiedTracking).Assembly.GetName(),
                    _ => null,
                };
                if ( hostAssembly != null && reference.Version > hostAssembly.Version &&
                     reference.Version!.Major > hostAssembly.Version!.Major )
                {
                    _logger.LogError(
                        "Module {dll} requires {assembly} {required}, but this app provides {provided}. " +
                        "Install a compatible module build, or rebuild it against this app's Core and SDK DLLs.",
                        Path.GetFileName(ModulePath), reference.Name, reference.Version, hostAssembly.Version);
                    return false;
                }

                validateSharedContract |= hostAssembly != null && reference.Version > hostAssembly.Version;

                if ( reference.Name == "VRCFaceTracking" || reference.Name == "VRCFaceTracking.Core" )
                {
                    if ( reference.Version < new Version(5, 0, 0, 0) )
                    {
                        _logger.LogWarning("Module {dll} references an older version of VRCFaceTracking. Skipping.", Path.GetFileName(ModulePath));
                        oldRefs = true;
                    }
                }
            }
            if ( oldRefs )
            {
                return false;
            }

            if ( validateSharedContract )
            {
                ModuleLoadContext.ValidateTrackingContract(Assembly, ModulePath);
            }

            foreach ( var type in Assembly.GetExportedTypes() )
            {
                if ( type.BaseType != typeof(ExtTrackingModule) )
                {
                    continue;
                }

                _logger.LogDebug("{module} properly implements ExtTrackingModule.", type.Name);
                TrackingModule  = LoadExternalModule();
                Loaded          = TrackingModule != null;
                return Loaded;
            }

            _logger.LogError("Module {dll} does not export an ExtTrackingModule implementation.", Path.GetFileName(ModulePath));
        } catch ( Exception e )
        {
            _logger.LogError(e, "Module {dll} could not be loaded.", Path.GetFileName(ModulePath));
        }

        return false;
    }

    private ExtTrackingModule LoadExternalModule()
    {
        if ( Assembly == null )
        {
            throw new Exception("Assembly failed to load but tried setting up module!");
        }

        _logger.LogInformation("Loading External Module " + Assembly.FullName);

        try
        {
            // Get the first class that implements ExtTrackingModule
            var module = Assembly.GetTypes().FirstOrDefault(t => t.IsSubclassOf(typeof(ExtTrackingModule)));
            if ( module == null )
            {
                throw new Exception("Failed to get module's ExtTrackingModule impl");
            }
            var moduleObj = (ExtTrackingModule)Activator.CreateInstance(module);
            var logger = _loggerFactory.CreateLogger(moduleObj.GetType().Name);
            moduleObj.Logger = logger;

            return moduleObj;
        } catch ( Exception e )
        {
            _logger.LogError("Exception loading {dll}. Skipping. {e}", Assembly.FullName, e);
        }

        return null;
    }
}
