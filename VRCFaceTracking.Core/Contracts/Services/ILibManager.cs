using System.Collections.ObjectModel;

namespace VRCFaceTracking.Core.Contracts.Services;

public interface ILibManager
{
    public ObservableCollection<ModuleMetadataInternal> LoadedModulesMetadata { get; set; }
    public void Initialize();
    void TeardownAllAndResetAsync();

    /// <summary>
    /// Dll paths of the sandbox module processes that are alive right now, plus modules on
    /// standby because every slot they can fill is already taken. Lets the hot-swap check
    /// tell "the startable set changed" apart from "something still needs doing about it":
    /// a module the hot-plug check already started for a source that just appeared must
    /// not be torn down only to be started again, and neither must a module that is only
    /// waiting for a free slot - re-initializing for it would hand out the slots in the
    /// same order and park it again.
    /// </summary>
    IReadOnlyList<string> GetRunningModulePaths();
}