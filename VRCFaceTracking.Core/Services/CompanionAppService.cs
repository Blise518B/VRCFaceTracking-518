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
    /// Launch commands that work on any machine. Steam apps go through their store URI
    /// so Steam picks the working directory and handles updates.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> Defaults =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { TrackingSourceDetector.BabbleSourceName, "steam://rungameid/4091970" },
        };

    /// <summary>
    /// Executable names to go looking for, per source, when no command is configured.
    /// Matched by prefix so version-stamped names (BrokenEye_v1.4.0.exe) are found too.
    /// </summary>
    /// <remarks>
    /// This replaced hardcoded absolute paths, which were whichever folders the author
    /// happened to use. Those were dead weight on every other machine - a friend saw an
    /// empty Launch Apps page and had to Browse for each app by hand - and they leaked
    /// the author's directory layout into a public binary.
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, string[]> DiscoverableExecutables =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { TrackingSourceDetector.BrokenEyeSourceName, new[] { "BrokenEye" } },
            { TrackingSourceDetector.EtvrSourceName, new[] { "eyetrackapp", "EyeTrackVR", "EyeTrackApp" } },
            { TrackingSourceDetector.BabbleSourceName, new[] { "Baballonia.Desktop", "Baballonia", "BabbleApp" } },
        };

    /// <summary>Roots worth searching, cheapest and most likely first.</summary>
    private static IEnumerable<string> DiscoveryRoots()
    {
        static string? Folder(Environment.SpecialFolder f)
        {
            try { return Environment.GetFolderPath(f); } catch (Exception) { return null; }
        }

        var roots = new List<string?>
        {
            Folder(Environment.SpecialFolder.DesktopDirectory),
            Folder(Environment.SpecialFolder.LocalApplicationData),
            Folder(Environment.SpecialFolder.ApplicationData),
            Folder(Environment.SpecialFolder.ProgramFiles),
            Folder(Environment.SpecialFolder.ProgramFilesX86),
        };

        var profile = Folder(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile))
        {
            roots.Add(Path.Combine(profile, "Downloads"));
            roots.Add(Path.Combine(profile, "Documents"));
        }

        // Where this app itself lives - people often keep the VR stack together
        try { roots.Add(Path.GetDirectoryName(AppContext.BaseDirectory)); } catch (Exception) { }

        return roots.Where(r => !string.IsNullOrEmpty(r) && Directory.Exists(r)).Select(r => r!).Distinct();
    }

    private static readonly Dictionary<string, string?> DiscoveryCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Looks for a source's executable in the usual places. Bounded on purpose: two
    /// directory levels below each root, so this stays a few hundred stat calls rather
    /// than a full-disk crawl. Cached per session including the misses, because a miss
    /// costs the same search as a hit.
    /// </summary>
    private static string? DiscoverExecutable(string sourceName)
    {
        lock (DiscoveryCache)
        {
            if (DiscoveryCache.TryGetValue(sourceName, out var cached))
            {
                return cached;
            }
        }

        string? found = null;
        if (DiscoverableExecutables.TryGetValue(sourceName, out var prefixes))
        {
            // A running instance is the best possible answer: it is the exact path, it
            // costs nothing to ask, and it works no matter how unusual the install
            // folder is - which a directory search never can.
            found = PathOfRunningProcess(prefixes);

            if (found == null)
            {
                foreach (var root in DiscoveryRoots())
                {
                    found = SearchForExecutable(root, prefixes, depth: 2);
                    if (found != null)
                    {
                        break;
                    }
                }
            }
        }

        lock (DiscoveryCache)
        {
            DiscoveryCache[sourceName] = found;
        }
        return found;
    }

    /// <summary>
    /// Full path of a running process whose name matches one of <paramref name="prefixes"/>.
    /// </summary>
    private static string? PathOfRunningProcess(string[] prefixes)
    {
        try
        {
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    if (!prefixes.Any(p => process.ProcessName.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    var path = process.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    {
                        return path;
                    }
                }
                catch (Exception)
                {
                    // Protected or exited process - MainModule throws; just try the next
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch (Exception)
        {
            // Process enumeration unavailable; fall back to the directory search
        }

        return null;
    }

    private static string? SearchForExecutable(string directory, string[] prefixes, int depth)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.exe"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (prefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                {
                    return file;
                }
            }

            if (depth <= 0)
            {
                return null;
            }

            foreach (var sub in Directory.EnumerateDirectories(directory))
            {
                var hit = SearchForExecutable(sub, prefixes, depth - 1);
                if (hit != null)
                {
                    return hit;
                }
            }
        }
        catch (Exception)
        {
            // Unreadable or protected directory - just skip it
        }

        return null;
    }

    /// <summary>
    /// Sources that auto-launch unless the user turns their toggle off. These two are
    /// required for eye tracking on the Dream Air, so a fresh setup gets the whole
    /// stack from one VRCFT start.
    /// </summary>
    private static readonly string[] AutoLaunchByDefault =
    {
        TrackingSourceDetector.BrokenEyeSourceName,
        TrackingSourceDetector.EtvrSourceName,
    };

    /// <summary>
    /// Don't auto-launch an app when a source it competes with is already live:
    /// starting the Dream Air eye stack while the Quest Pro streams over Virtual
    /// Desktop would put two eye modules in a race for the exclusive eye slot.
    /// Manual launches from the page are never blocked - the user knows best.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> AutoLaunchConflicts =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { TrackingSourceDetector.BrokenEyeSourceName, "Virtual Desktop" },
            { TrackingSourceDetector.EtvrSourceName, "Virtual Desktop" },
        };

    /// <summary>
    /// Asks the app layer which headset SteamVR considers active. Null while
    /// SteamVR is down or no HMD is connected.
    /// </summary>
    public static Func<string?>? HmdResolver;

    /// <summary>
    /// The Dream Air stack only auto-launches when SteamVR's active headset IS
    /// the Pimax. Unknown headset = defer (the hot-swap watcher fires the pass
    /// again once SteamVR reveals it) rather than launching blind. "aapvr" is
    /// the Pimax SteamVR driver's tracking-system name.
    /// </summary>
    private static readonly string[] RequiresPimaxHmd =
    {
        TrackingSourceDetector.BrokenEyeSourceName,
        TrackingSourceDetector.EtvrSourceName,
        TrackingSourceDetector.BabbleSourceName,
    };

    private static readonly string[] PimaxHmdKeywords = { "pimax", "dream", "aapvr" };

    private static (bool? IsPimax, string? Identity) ProbeHmd()
    {
        var identity = HmdResolver?.Invoke();
        if (string.IsNullOrWhiteSpace(identity))
        {
            return (null, null);
        }

        var lower = identity.ToLowerInvariant();
        return (PimaxHmdKeywords.Any(k => lower.Contains(k)), identity);
    }

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

    public bool GetAutoLaunch(string sourceName)
    {
        if (_autoLaunch.TryGetValue(sourceName, out var enabled))
        {
            return enabled;
        }

        return AutoLaunchByDefault.Contains(sourceName, StringComparer.OrdinalIgnoreCase)
               && IsConfigured(sourceName);
    }

    public void SetAutoLaunch(string sourceName, bool enabled)
    {
        // Stored explicitly (not remove-on-false) so turning a default-on
        // source off actually sticks.
        _autoLaunch[sourceName] = enabled;
        _ = PersistAsync();
    }

    /// <summary>
    /// Starts every companion app marked "launch with VRCFT" whose source isn't already
    /// live. Runs at startup and again whenever the active headset changes; idempotent
    /// by construction (already-running apps are skipped), so repeated passes and
    /// watchdog relaunches never spawn duplicates.
    /// </summary>
    public int LaunchConfiguredAtStartup()
    {
        // One forced scan up front so the skip decisions see current reality.
        var statuses = _detector?.Scan(force: true);
        var launched = 0;
        var (isPimax, hmdIdentity) = ProbeHmd();

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

            if (RequiresPimaxHmd.Contains(source.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (isPimax == false)
                {
                    _logger?.LogInformation(
                        "Skipping auto-launch of {source}: the active headset is \"{hmd}\", not a Pimax. Launch manually from the Launch Apps page if wanted.",
                        source.Name, hmdIdentity);
                    continue;
                }
                if (isPimax == null)
                {
                    _logger?.LogInformation(
                        "Deferring auto-launch of {source}: no headset visible yet - it starts automatically once SteamVR reports a Pimax.",
                        source.Name);
                    continue;
                }
            }

            if (AutoLaunchConflicts.TryGetValue(source.Name, out var rival)
                && (statuses?.FirstOrDefault(s => s.Name == rival)?.Detected ?? false))
            {
                _logger?.LogInformation(
                    "Skipping auto-launch of {source}: {rival} is actively streaming - launch it manually from the Launch Apps page if you want both.",
                    source.Name, rival);
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

        if (Defaults.TryGetValue(sourceName, out var suggestion)
            && (suggestion.Contains("://") || File.Exists(suggestion)))
        {
            return suggestion;
        }

        // Nothing configured and no universal default: go and find it. Whatever turns
        // up is saved, so the search happens once per machine rather than every time -
        // and an app found while running stays configured after it closes.
        var discovered = DiscoverExecutable(sourceName);
        if (discovered != null)
        {
            _commands[sourceName] = discovered;
            _logger?.LogInformation("Found {source} at {path}; remembering it as the launch command.",
                sourceName, discovered);
            _ = PersistAsync();
        }

        return discovered ?? string.Empty;
    }

    public bool IsConfigured(string sourceName) => !string.IsNullOrWhiteSpace(GetCommand(sourceName));

    /// <summary>
    /// The sources worth showing on the Utilities page: anything the user has
    /// given a command, plus the ones we ship a default for. Deliberately not
    /// every known source - a launcher listing SRanipal and Virtual Desktop
    /// with no command would be noise.
    /// </summary>
    public IEnumerable<string> LaunchableSourceNames()
    {
        foreach (var source in TrackingSourceDetector.KnownSources)
        {
            if (IsConfigured(source.Name)
                || Defaults.ContainsKey(source.Name)
                || DiscoverableExecutables.ContainsKey(source.Name))
            {
                yield return source.Name;
            }
        }
    }

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
