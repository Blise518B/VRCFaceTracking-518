using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using VRCFaceTracking.Core.Contracts.Services;

namespace VRCFaceTracking.Core.Services;

/// <summary>
/// Central switch and statistics for the fork's crash auto-restart features (the module
/// process supervisor and the app crash watchdog). Persists an on/off toggle, restart
/// counters and a capped log of recent restart events, and owns the watchdog process
/// lifetime so the toggle takes effect immediately.
/// </summary>
public partial class AutoRestartService : ObservableObject
{
    private const int MaxLogEntries = 50;
    private const string EventsSettingKey = "AutoRestartEvents";

    [ObservableProperty]
    [property: SavedSetting("AutoRestartEnabled", true)]
    private bool _enabled = true;

    [ObservableProperty]
    [property: SavedSetting("AutoRestartModuleCount", 0)]
    private int _moduleRestartCount;

    [ObservableProperty]
    [property: SavedSetting("AutoRestartAppCount", 0)]
    private int _appRelaunchCount;

    public ObservableCollection<string> RecentEvents { get; } = new();

    private readonly ILocalSettingsService _localSettingsService;
    private readonly IDispatcherService _dispatcherService;
    private readonly ILogger<AutoRestartService> _logger;
    private Process _watchdogProcess;
    private bool _initialized;

    public AutoRestartService(
        ILocalSettingsService localSettingsService,
        IDispatcherService dispatcherService,
        ILogger<AutoRestartService> logger)
    {
        _localSettingsService = localSettingsService;
        _dispatcherService = dispatcherService;
        _logger = logger;

        PropertyChanged += (_, _) =>
        {
            if (_initialized)
            {
                _localSettingsService.Save(this);
            }
        };
    }

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        await _localSettingsService.Load(this);
        var events = await _localSettingsService.ReadSettingAsync<List<string>>(EventsSettingKey) ?? new List<string>();
        foreach (var entry in events.Take(MaxLogEntries))
        {
            RecentEvents.Add(entry);
        }
        _initialized = true;
    }

    partial void OnEnabledChanged(bool value)
    {
        // Only react to user toggling, not to the initial settings load
        if (!_initialized)
        {
            return;
        }

        if (value)
        {
            _logger.LogInformation("Auto-restart enabled.");
            StartWatchdog(0);
        }
        else
        {
            _logger.LogInformation("Auto-restart disabled.");
            StopWatchdog();
        }
    }

    /// <summary>
    /// Called by the module supervisor whenever it respawns a crashed module process.
    /// Safe to call from any thread.
    /// </summary>
    public void RecordModuleRestart(string moduleName) =>
        Record(isModule: true, $"Module \"{moduleName}\" was restarted after a crash");

    /// <summary>
    /// Called at startup when the app detects it was relaunched by the watchdog.
    /// </summary>
    public void RecordAppRelaunch(int attempt) =>
        Record(isModule: false, $"VRCFaceTracking was relaunched after a crash (attempt {attempt} in a row)");

    private void Record(bool isModule, string message)
    {
        _dispatcherService.Run(() =>
        {
            if (isModule)
            {
                ModuleRestartCount++;
            }
            else
            {
                AppRelaunchCount++;
            }

            RecentEvents.Insert(0, $"{DateTime.Now:yyyy-MM-dd HH:mm}  {message}");
            while (RecentEvents.Count > MaxLogEntries)
            {
                RecentEvents.RemoveAt(RecentEvents.Count - 1);
            }

            _ = _localSettingsService.SaveSettingAsync(EventsSettingKey, RecentEvents.ToList());
        });
    }

    public void ResetStats()
    {
        _dispatcherService.Run(() =>
        {
            ModuleRestartCount = 0;
            AppRelaunchCount = 0;
            RecentEvents.Clear();
            _ = _localSettingsService.SaveSettingAsync(EventsSettingKey, new List<string>());
        });
    }

    /// <summary>
    /// Spawns the watchdog process that relaunches the app after a crash. Idempotent.
    /// </summary>
    public void StartWatchdog(int restartCount)
    {
        try
        {
            if (_watchdogProcess is { HasExited: false })
            {
                return;
            }

            var watchdogPath = Path.Combine(AppContext.BaseDirectory, "VRCFaceTracking.Watchdog.exe");
            if (!File.Exists(watchdogPath))
            {
                _logger.LogWarning("Crash watchdog not found at {path}. Auto-relaunch is disabled.", watchdogPath);
                return;
            }

            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                _logger.LogWarning("Could not determine own executable path. Auto-relaunch is disabled.");
                return;
            }

            _watchdogProcess = Process.Start(new ProcessStartInfo(watchdogPath)
            {
                Arguments = $"--watch-pid {Environment.ProcessId} --exe \"{exePath}\" --restart-count {restartCount}",
                WorkingDirectory = AppContext.BaseDirectory,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            _logger.LogInformation("Crash watchdog started.");
        }
        catch (Exception e)
        {
            _logger.LogWarning("Failed to start crash watchdog: {message}. Auto-relaunch is disabled.", e.Message);
        }
    }

    public void StopWatchdog()
    {
        try
        {
            if (_watchdogProcess is { HasExited: false })
            {
                _watchdogProcess.Kill();
                _logger.LogInformation("Crash watchdog stopped.");
            }
        }
        catch (Exception e)
        {
            _logger.LogWarning("Failed to stop crash watchdog: {message}", e.Message);
        }
        finally
        {
            _watchdogProcess = null;
        }
    }
}
