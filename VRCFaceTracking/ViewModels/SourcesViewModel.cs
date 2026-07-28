using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
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

/// <summary>
/// One tracking source's detection state, plus its companion-app launch command.
/// The Launch button shows for any source that has a command but isn't detected.
/// </summary>
public partial class SourceRow : ObservableObject
{
    private readonly SourcesViewModel _owner;

    public SourceRow(SourcesViewModel owner) => _owner = owner;

    public required string Name { get; init; }
    public required string Evidence { get; init; }
    public string? Notes { get; init; }
    public bool Detected { get; init; }
    public bool Strong { get; init; }
    public bool WeakOnly { get; init; }

    /// <summary>Exe path (optionally quoted, with args) or a URI like steam://...</summary>
    [ObservableProperty] private string _launchCommand = string.Empty;

    /// <summary>One-line feedback under the evidence text ("launching...", errors).</summary>
    [ObservableProperty] private string _launchStatus = string.Empty;

    /// <summary>Start this app automatically when VRCFaceTracking starts.</summary>
    [ObservableProperty] private bool _autoLaunch;

    public bool ShowLaunch => !Detected && !string.IsNullOrWhiteSpace(LaunchCommand);
    public bool HasLaunchStatus => !string.IsNullOrWhiteSpace(LaunchStatus);

    partial void OnLaunchCommandChanged(string value) => OnPropertyChanged(nameof(ShowLaunch));
    partial void OnLaunchStatusChanged(string value) => OnPropertyChanged(nameof(HasLaunchStatus));
    partial void OnAutoLaunchChanged(bool value) => _owner.SetRowAutoLaunch(this, value);

    public void Launch() => _owner.LaunchRow(this);

    public void SaveCommand() => _owner.SaveRowCommand(this);
}

public partial class SourcesViewModel : ObservableRecipient
{
    private readonly ModuleActivationService _activation;
    private readonly IModuleDataService _moduleDataService;
    private readonly CompanionAppService _companions;
    private readonly DispatcherQueue? _dispatcher;

    public ObservableCollection<SourceRow> Sources { get; } = new();
    public ObservableCollection<ModuleRow> Modules { get; } = new();

    [ObservableProperty] private string _summary = string.Empty;

    public SourcesViewModel()
    {
        _activation = App.GetService<ModuleActivationService>();
        _moduleDataService = App.GetService<IModuleDataService>();
        _companions = App.GetService<CompanionAppService>();
        _dispatcher = DispatcherQueue.GetForCurrentThread();
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

    public void LaunchRow(SourceRow row)
    {
        var (started, message) = _companions.Launch(row.Name);
        row.LaunchStatus = started ? "launching..." : message;

        if (started)
        {
            // The process shows up within a couple of seconds for plain exes;
            // Steam URIs can take longer, hence the second pass.
            ScheduleRefresh(3000);
            ScheduleRefresh(9000);
        }
    }

    public void SaveRowCommand(SourceRow row)
    {
        _companions.SetCommand(row.Name, row.LaunchCommand);
        row.LaunchStatus = string.IsNullOrWhiteSpace(row.LaunchCommand)
            ? "launch command cleared"
            : "launch command saved";
    }

    public void SetRowAutoLaunch(SourceRow row, bool enabled)
    {
        // Refresh() re-seeds AutoLaunch from the service; only user changes persist.
        if (_companions.GetAutoLaunch(row.Name) == enabled)
        {
            return;
        }

        _companions.SetAutoLaunch(row.Name, enabled);
    }

    private void ScheduleRefresh(int delayMs)
    {
        _ = Task.Delay(delayMs).ContinueWith(_ => _dispatcher?.TryEnqueue(Refresh));
    }

    public void Refresh()
    {
        // Keep launch feedback alive across the rebuild for rows that still
        // aren't detected, so "launching..." doesn't vanish mid-startup.
        var previousStatus = Sources.ToDictionary(r => r.Name, r => r.LaunchStatus, StringComparer.OrdinalIgnoreCase);

        Sources.Clear();
        foreach (var status in _activation.Detector.Scan(force: true))
        {
            var row = new SourceRow(this)
            {
                Name = status.Name,
                Evidence = status.Evidence,
                Notes = status.Notes,
                Detected = status.Detected,
                Strong = status.Strong,
                WeakOnly = status.WeakOnly,
            };
            row.LaunchCommand = _companions.GetCommand(status.Name);
            row.AutoLaunch = _companions.GetAutoLaunch(status.Name);
            if (!status.Detected && previousStatus.TryGetValue(status.Name, out var old))
            {
                row.LaunchStatus = old;
            }
            Sources.Add(row);
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
