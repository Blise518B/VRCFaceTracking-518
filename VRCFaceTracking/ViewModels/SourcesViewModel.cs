using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using VRCFaceTracking.Core.Contracts.Services;
using VRCFaceTracking.Core.Services;

namespace VRCFaceTracking.ViewModels;

/// <summary>One installed module and what will happen to it at startup.</summary>
public partial class ModuleRow : ObservableObject
{
    public required string Key { get; init; }
    public required string DisplayName { get; init; }
    public required string MappedSources { get; init; }

    [ObservableProperty] private bool _willStart;
    [ObservableProperty] private string _reason = string.Empty;

    /// <summary>Index into the mode combo box: 0 Auto, 1 Always, 2 Never.</summary>
    [ObservableProperty] private int _modeIndex;

    public string StatusText => WillStart ? "WILL START" : "SKIPPED";

    partial void OnWillStartChanged(bool value) => OnPropertyChanged(nameof(StatusText));
}

public partial class SourcesViewModel : ObservableRecipient
{
    private readonly ModuleActivationService _activation;
    private readonly IModuleDataService _moduleDataService;

    public ObservableCollection<TrackingSourceStatus> Sources { get; } = new();
    public ObservableCollection<ModuleRow> Modules { get; } = new();

    [ObservableProperty] private string _summary = string.Empty;

    public SourcesViewModel()
    {
        _activation = App.GetService<ModuleActivationService>();
        _moduleDataService = App.GetService<IModuleDataService>();
        Refresh();
    }

    /// <summary>Master switch: off means every installed module starts, as it did before.</summary>
    public bool GatingEnabled
    {
        get => _activation.GatingEnabled;
        set
        {
            _activation.GatingEnabled = value;
            OnPropertyChanged();
            Refresh();
        }
    }

    public void Refresh()
    {
        Sources.Clear();
        foreach (var status in _activation.Detector.Scan(force: true))
        {
            Sources.Add(status);
        }

        var installed = _moduleDataService.GetInstalledModules()
            .Concat(_moduleDataService.GetLegacyModules())
            .ToList();

        Modules.Clear();
        foreach (var decision in _activation.Evaluate(installed))
        {
            var row = new ModuleRow
            {
                Key = decision.Key,
                DisplayName = decision.DisplayName,
                MappedSources = decision.MappedSources,
                WillStart = decision.WillStart,
                Reason = decision.Reason,
                ModeIndex = decision.Mode switch
                {
                    ModuleStartMode.Always => 1,
                    ModuleStartMode.Never => 2,
                    _ => 0,
                },
            };

            // Persist and re-evaluate whenever the user changes a module's mode
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(ModuleRow.ModeIndex))
                {
                    return;
                }

                _activation.SetMode(row.Key, row.ModeIndex switch
                {
                    1 => ModuleStartMode.Always,
                    2 => ModuleStartMode.Never,
                    _ => ModuleStartMode.Auto,
                });
                Refresh();
            };

            Modules.Add(row);
        }

        var active = Sources.Count(s => s.Detected && s.Strong);
        var starting = Modules.Count(m => m.WillStart);
        Summary = installed.Count == 0
            ? "No modules are installed yet - grab one from the Module Registry."
            : $"{active} active source{(active == 1 ? "" : "s")} detected - " +
              $"{starting} of {Modules.Count} installed module{(Modules.Count == 1 ? "" : "s")} will start";
    }
}
