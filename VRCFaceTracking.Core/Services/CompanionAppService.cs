using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using VRCFaceTracking.Core.Contracts.Services;

namespace VRCFaceTracking.Core.Services;

/// <summary>
/// Launches the companion apps that feed tracking sources (BrokenEye, EyeTrackVR,
/// Baballonia, ...) so a session doesn't require hunting down each program by hand.
/// One launch command is stored per tracking-source name; the Sources page shows a
/// Launch button for any configured source that isn't detected as running.
///
/// Launch-only by design: nothing is monitored, restarted or killed. Quitting an app
/// mid-session (say, swapping EyeTrackVR for Baballonia to compare them) must never
/// be fought by a keep-alive.
/// </summary>
public class CompanionAppService
{
    private const string CommandsSettingKey = "CompanionAppCommands";
    private const string AutoLaunchSettingKey = "CompanionAppAutoLaunch";

    private readonly ILocalSettingsService _localSettingsService;
    private readonly TrackingSourceDetector? _detector;
    private readonly ILogger<CompanionAppService>? _logger;

    private Dictionary<string, string> _commands = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, bool> _autoLaunch = new(StringComparer.OrdinalIgnoreCase);
    private bool _initialized;

    /// <summary>
    /// Suggestions shown until the user sets their own command. Steam apps launch
    /// through their store URI so Steam handles the working directory and updates.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> Defaults =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Project Babble", "steam://rungameid/4091970" },
        };

    public CompanionAppService(
        ILocalSettingsService localSettingsService,
        TrackingSourceDetector? detector = null,
        ILogger<CompanionAppService>? logger = null)
    {
        _localSettingsService = localSettingsService;
        _detector = detector;
        _logger = logger;
    }

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        try
        {
            var raw = await _localSettingsService.ReadSettingAsync<string>(CommandsSettingKey);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                var parsed = JsonConvert.DeserializeObject<Dictionary<string, string>>(raw);
                if (parsed != null)
                {
                    _commands = new Dictionary<string, string>(parsed, StringComparer.OrdinalIgnoreCase);
                }
            }

            var rawAuto = await _localSettingsService.ReadSettingAsync<string>(AutoLaunchSettingKey);
            if (!string.IsNullOrWhiteSpace(rawAuto))
            {
                var parsed = JsonConvert.DeserializeObject<Dictionary<string, bool>>(rawAuto);
                if (parsed != null)
                {
                    _autoLaunch = new Dictionary<string, bool>(parsed, StringComparer.OrdinalIgnoreCase);
                }
            }
        }
        catch (Exception e)
        {
            _logger?.LogWarning("Could not load companion app commands: {message}", e.Message);
        }

        _initialized = true;
    }

    public bool GetAutoLaunch(string sourceName) =>
        _autoLaunch.TryGetValue(sourceName, out var enabled) && enabled;

    public void SetAutoLaunch(string sourceName, bool enabled)
    {
        if (enabled)
        {
            _autoLaunch[sourceName] = true;
        }
        else
        {
            _autoLaunch.Remove(sourceName);
        }

        _ = PersistAsync();
    }

    /// <summary>
    /// Starts every companion app marked "launch with VRCFT" whose source isn't already
    /// live. Called once during app startup; idempotent by construction (already-running
    /// apps are skipped), so watchdog relaunches after a crash never spawn duplicates.
    /// </summary>
    public int LaunchConfiguredAtStartup()
    {
        // One forced scan up front so the skip decisions see current reality.
        var statuses = _detector?.Scan(force: true);
        var launched = 0;

        foreach (var source in TrackingSourceDetector.KnownSources)
        {
            if (!GetAutoLaunch(source.Name) || !IsConfigured(source.Name))
            {
                continue;
            }

            var detected = statuses?.FirstOrDefault(s => s.Name == source.Name)?.Detected ?? false;
            if (detected)
            {
                _logger?.LogInformation("Companion app for {source} already running - not launching", source.Name);
                continue;
            }

            var (started, message) = Launch(source.Name);
            if (started)
            {
                launched++;
            }
            else
            {
                _logger?.LogWarning("Auto-launch for {source} did not start: {message}", source.Name, message);
            }
        }

        if (launched > 0)
        {
            // Exe processes exist the moment Process.Start returns; refresh the detector
            // cache so the module start decisions that follow see them. (Steam URIs can
            // take longer than this - those sources may need one re-initialise.)
            _detector?.Scan(force: true);
            _logger?.LogInformation("Auto-launched {count} companion app(s)", launched);
        }

        return launched;
    }

    public string GetCommand(string sourceName)
    {
        if (_commands.TryGetValue(sourceName, out var command) && !string.IsNullOrWhiteSpace(command))
        {
            return command;
        }

        return Defaults.TryGetValue(sourceName, out var suggestion) ? suggestion : string.Empty;
    }

    public bool IsConfigured(string sourceName) => !string.IsNullOrWhiteSpace(GetCommand(sourceName));

    public void SetCommand(string sourceName, string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            _commands.Remove(sourceName);
        }
        else
        {
            _commands[sourceName] = command.Trim();
        }

        _ = PersistAsync();
    }

    /// <summary>
    /// True when the configured command's own executable already has a process. This
    /// backs up source detection for apps whose process name we don't know upfront -
    /// whatever exe the user pointed at IS the thing to look for. URIs can't be
    /// checked this way and return false.
    /// </summary>
    public bool IsCommandProcessRunning(string sourceName)
    {
        var (file, _) = SplitCommand(GetCommand(sourceName));
        if (string.IsNullOrWhiteSpace(file) || file.Contains("://"))
        {
            return false;
        }

        try
        {
            var baseName = Path.GetFileNameWithoutExtension(file);
            return !string.IsNullOrEmpty(baseName) && Process.GetProcessesByName(baseName).Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public (bool Started, string Message) Launch(string sourceName)
    {
        var command = GetCommand(sourceName);
        if (string.IsNullOrWhiteSpace(command))
        {
            return (false, "no launch command configured");
        }

        if (IsCommandProcessRunning(sourceName))
        {
            return (false, "already running");
        }

        var (file, args) = SplitCommand(command);

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                // Shell execute so URIs (steam://), .bat files and file
                // associations all work exactly like double-clicking them.
                UseShellExecute = true,
            };

            if (!file.Contains("://") && File.Exists(file))
            {
                // Apps like BrokenEye load their config relative to the exe.
                startInfo.WorkingDirectory = Path.GetDirectoryName(file) ?? string.Empty;
            }

            Process.Start(startInfo);
            _logger?.LogInformation("Launched companion app for {source}: {command}", sourceName, command);
            return (true, "launched");
        }
        catch (Exception e)
        {
            _logger?.LogWarning("Could not launch companion app for {source} ({command}): {message}",
                sourceName, command, e.Message);
            return (false, e.Message);
        }
    }

    /// <summary>
    /// Splits "path with args" into (file, args). Quoted paths keep their spaces; an
    /// unquoted string that exists on disk as-is is treated as one path (so plain
    /// paste of "C:\Program Files\App\app.exe" works without quoting).
    /// </summary>
    internal static (string File, string Args) SplitCommand(string command)
    {
        command = command.Trim();
        if (command.Length == 0)
        {
            return (string.Empty, string.Empty);
        }

        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            if (end > 0)
            {
                return (command[1..end], command[(end + 1)..].Trim());
            }
        }

        if (command.Contains("://") || File.Exists(command))
        {
            return (command, string.Empty);
        }

        var space = command.IndexOf(' ');
        return space < 0
            ? (command, string.Empty)
            : (command[..space], command[(space + 1)..].Trim());
    }

    private async Task PersistAsync()
    {
        try
        {
            await _localSettingsService.SaveSettingAsync(CommandsSettingKey, JsonConvert.SerializeObject(_commands));
            await _localSettingsService.SaveSettingAsync(AutoLaunchSettingKey, JsonConvert.SerializeObject(_autoLaunch));
        }
        catch (Exception e)
        {
            _logger?.LogWarning("Could not save companion app commands: {message}", e.Message);
        }
    }
}
