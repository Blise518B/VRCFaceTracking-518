using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using VRCFaceTracking.Core.Services;

namespace VRCFaceTracking.ViewModels;

/// <summary>One helper program on the Utilities page.</summary>
public partial class UtilityRow : ObservableObject
{
    private readonly UtilitiesViewModel _owner;

    public UtilityRow(UtilitiesViewModel owner) => _owner = owner;

    public required string Name { get; init; }
    public string? Notes { get; init; }

    [ObservableProperty] private string _launchCommand = string.Empty;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _autoLaunch;
    [ObservableProperty] private string _statusText = "not running";

    /// <summary>Launch is offered whenever the app isn't up and we know how to start it.</summary>
    public bool CanLaunch => !IsRunning && !string.IsNullOrWhiteSpace(LaunchCommand);
    public bool NeedsSetup => string.IsNullOrWhiteSpace(LaunchCommand);

    partial void OnIsRunningChanged(bool value)
    {
        StatusText = value
            ? "running"
            : NeedsSetup
                ? "not set up - choose the program to start"
                : "not running";
        OnPropertyChanged(nameof(CanLaunch));
    }

    partial void OnLaunchCommandChanged(string value)
    {
        OnPropertyChanged(nameof(CanLaunch));
        OnPropertyChanged(nameof(NeedsSetup));

        // Status line follows setup state, otherwise a row that was just configured
        // keeps saying "not set up" until the next status refresh.
        if (!IsRunning)
        {
            StatusText = NeedsSetup ? "not set up - choose the program to start" : "not running";
        }
    }

    partial void OnAutoLaunchChanged(bool value) => _owner.SetRowAutoLaunch(this, value);

    public void Launch() => _owner.LaunchRow(this);
    public void SaveCommand() => _owner.SaveRowCommand(this);
    public void Browse() => _owner.BrowseRowCommand(this);
}

/// <summary>
/// A dedicated place to start the helper programs the tracking stack needs
/// (BrokenEye, EyeTrackVR, Baballonia, ...) instead of hunting for their
/// windows. Buttons stay visible whether or not an app is running, so the
/// page also doubles as an at-a-glance "is my stack up?" view.
/// </summary>
public partial class UtilitiesViewModel : ObservableRecipient
{
    private readonly CompanionAppService _companions;
    private readonly TrackingSourceDetector _detector;
    private readonly DispatcherQueue? _dispatcher;
    private DispatcherQueueTimer? _timer;

    public ObservableCollection<UtilityRow> Apps { get; } = new();

    [ObservableProperty] private string _summary = string.Empty;

    public UtilitiesViewModel()
    {
        _companions = App.GetService<CompanionAppService>();
        _detector = App.GetService<TrackingSourceDetector>();
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        Rebuild();
    }

    public void Start()
    {
        if (_dispatcher is null || _timer is not null)
        {
            return;
        }

        _timer = _dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) => RefreshStatus();
        _timer.Start();
        RefreshStatus();
    }

    public void Stop()
    {
        _timer?.Stop();
        _timer = null;
    }

    public void Rebuild()
    {
        Apps.Clear();
        foreach (var name in _companions.LaunchableSourceNames())
        {
            var source = TrackingSourceDetector.KnownSources.FirstOrDefault(s => s.Name == name);
            var row = new UtilityRow(this)
            {
                Name = name,
                Notes = source?.Notes,
            };
            row.LaunchCommand = _companions.GetCommand(name);
            row.AutoLaunch = _companions.GetAutoLaunch(name);
            Apps.Add(row);
        }
        RefreshStatus();
    }

    public void RefreshStatus()
    {
        // This runs on a 1s UI timer. ScanCached never blocks the UI thread on a probe
        // sweep (a cold one costs hundreds of ms); it serves the last sweep and refreshes
        // in the background, so the pills update within a few seconds of reality.
        var statuses = _detector.ScanCached();
        foreach (var row in Apps)
        {
            // Detection is data-driven (BrokenEye counts only while frames
            // flow), but for THIS page "running" means the program is open -
            // so fall back to the configured exe's process.
            row.IsRunning = (statuses.FirstOrDefault(s => s.Name == row.Name)?.Detected ?? false)
                            || _companions.IsCommandProcessRunning(row.Name);
        }

        var up = Apps.Count(a => a.IsRunning);
        Summary = Apps.Count == 0
            ? "No helper programs configured yet."
            : $"{up} of {Apps.Count} helper programs running";
    }

    public void LaunchAllMissing()
    {
        foreach (var row in Apps.Where(a => a.CanLaunch).ToList())
        {
            LaunchRow(row);
        }
    }

    public void LaunchRow(UtilityRow row)
    {
        var (started, message) = _companions.Launch(row.Name);
        row.StatusText = started ? "starting..." : message;
        // Plain exes appear at once; Steam URIs can take a while.
        ScheduleRefresh(2500);
        ScheduleRefresh(8000);
    }

    public void SaveRowCommand(UtilityRow row)
    {
        _companions.SetCommand(row.Name, row.LaunchCommand);
        row.StatusText = string.IsNullOrWhiteSpace(row.LaunchCommand)
            ? "launch command cleared"
            : "launch command saved";
        RefreshStatus();
    }

    public void SetRowAutoLaunch(UtilityRow row, bool enabled)
    {
        if (_companions.GetAutoLaunch(row.Name) == enabled)
        {
            return;
        }
        _companions.SetAutoLaunch(row.Name, enabled);
    }

    public async void BrowseRowCommand(UtilityRow row)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder,
            };
            picker.FileTypeFilter.Add(".exe");
            picker.FileTypeFilter.Add(".bat");
            picker.FileTypeFilter.Add(".cmd");
            picker.FileTypeFilter.Add(".lnk");
            WinRT.Interop.InitializeWithWindow.Initialize(picker,
                WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));

            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                row.LaunchCommand = file.Path;
                SaveRowCommand(row);
            }
        }
        catch (Exception)
        {
            row.StatusText = "file picker failed - paste the path instead";
        }
    }

    private void ScheduleRefresh(int delayMs)
    {
        _ = Task.Delay(delayMs).ContinueWith(_ => _dispatcher?.TryEnqueue(RefreshStatus));
    }
}
