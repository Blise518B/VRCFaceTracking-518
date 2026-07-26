using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using VRCFaceTracking.Core.Contracts.Services;
using VRCFaceTracking.Core.Models;

namespace VRCFaceTracking.Core.Services;

/// <summary>
/// Decides which installed modules are allowed to start, based on which tracking sources are
/// actually live plus any per-module override the user has set. Keeps VRCFaceTracking from
/// launching a process for every installed module when only one of them has hardware behind it.
/// </summary>
public partial class ModuleActivationService : ObservableObject
{
    private const string ModesSettingKey = "ModuleStartModes";

    /// <summary>
    /// Master switch. Off restores stock behaviour (start everything and let each module
    /// fail its own initialisation).
    /// </summary>
    [ObservableProperty]
    [property: SavedSetting("SourceGatingEnabled", true)]
    private bool _gatingEnabled = true;

    private readonly ILocalSettingsService _localSettingsService;
    private readonly TrackingSourceDetector _detector;
    private readonly ILogger<ModuleActivationService>? _logger;

    private Dictionary<string, ModuleStartMode> _modes = new(StringComparer.OrdinalIgnoreCase);
    private bool _initialized;

    public ModuleActivationService(
        ILocalSettingsService localSettingsService,
        TrackingSourceDetector detector,
        ILogger<ModuleActivationService>? logger = null)
    {
        _localSettingsService = localSettingsService;
        _detector = detector;
        _logger = logger;

        PropertyChanged += (_, _) =>
        {
            if (_initialized)
            {
                _localSettingsService.Save(this);
            }
        };
    }

    public TrackingSourceDetector Detector => _detector;

    /// <summary>Describes what will happen to one module at startup.</summary>
    public sealed class ModuleDecision
    {
        public required string Key { get; init; }
        public required string DisplayName { get; init; }
        public required string DllName { get; init; }
        public required ModuleStartMode Mode { get; init; }
        public required bool WillStart { get; init; }
        public required string Reason { get; init; }
        public string MappedSources { get; init; } = string.Empty;
    }

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        await _localSettingsService.Load(this);

        try
        {
            var raw = await _localSettingsService.ReadSettingAsync<string>(ModesSettingKey);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                var parsed = JsonConvert.DeserializeObject<Dictionary<string, ModuleStartMode>>(raw);
                if (parsed != null)
                {
                    _modes = new Dictionary<string, ModuleStartMode>(parsed, StringComparer.OrdinalIgnoreCase);
                }
            }
        }
        catch (Exception e)
        {
            _logger?.LogWarning("Could not load module start modes: {message}", e.Message);
        }

        _initialized = true;
    }

    /// <summary>
    /// Stable identity for a module. Uses the dll filename rather than the display name,
    /// because the display name only becomes known after the module has been initialised -
    /// which is exactly what we're deciding whether to do.
    /// </summary>
    public static string KeyFor(string? dllPath, string? moduleName) =>
        !string.IsNullOrWhiteSpace(dllPath)
            ? Path.GetFileNameWithoutExtension(dllPath)!
            : moduleName ?? "unknown";

    public ModuleStartMode GetMode(string key) =>
        _modes.TryGetValue(key, out var mode) ? mode : ModuleStartMode.Auto;

    public void SetMode(string key, ModuleStartMode mode)
    {
        if (mode == ModuleStartMode.Auto)
        {
            _modes.Remove(key);
        }
        else
        {
            _modes[key] = mode;
        }

        _ = PersistModesAsync();
    }

    private async Task PersistModesAsync()
    {
        try
        {
            await _localSettingsService.SaveSettingAsync(ModesSettingKey, JsonConvert.SerializeObject(_modes));
        }
        catch (Exception e)
        {
            _logger?.LogWarning("Could not save module start modes: {message}", e.Message);
        }
    }

    /// <summary>
    /// Filters module dll paths down to the ones that should actually be launched, logging
    /// the reason for every decision.
    /// </summary>
    public List<string> FilterStartablePaths(IEnumerable<InstallableTrackingModule> modules)
    {
        var allowed = new List<string>();

        foreach (var module in modules)
        {
            var path = module.AssemblyLoadPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var key = KeyFor(path, module.ModuleName);
            var (load, reason) = _detector.Decide(module.ModuleName, module.DllFileName, path,
                                                  GetMode(key), GatingEnabled);

            if (load)
            {
                allowed.Add(path);
                _logger?.LogInformation("Starting module {module}: {reason}", key, reason);
            }
            else
            {
                _logger?.LogInformation("Skipping module {module}: {reason}", key, reason);
            }
        }

        return allowed;
    }

    /// <summary>Decisions for every installed module, for display in the UI.</summary>
    public List<ModuleDecision> Evaluate(IEnumerable<InstallableTrackingModule> modules)
    {
        var decisions = new List<ModuleDecision>();

        foreach (var module in modules)
        {
            var path = module.AssemblyLoadPath;
            var key = KeyFor(path, module.ModuleName);
            var mode = GetMode(key);
            var (load, reason) = _detector.Decide(module.ModuleName, module.DllFileName, path,
                                                  mode, GatingEnabled);
            var mapped = _detector.SourcesForModule(module.ModuleName, module.DllFileName, path);

            decisions.Add(new ModuleDecision
            {
                Key = key,
                DisplayName = string.IsNullOrWhiteSpace(module.ModuleName) ? key : module.ModuleName,
                DllName = module.DllFileName ?? string.Empty,
                Mode = mode,
                WillStart = load,
                Reason = reason,
                MappedSources = mapped.Count > 0
                    ? string.Join(", ", mapped.Select(m => m.Name))
                    : "unrecognised",
            });
        }

        return decisions;
    }
}
