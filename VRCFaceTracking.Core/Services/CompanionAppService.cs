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
    private const string TobiiBridgeSettingKey = "TobiiBridge";

    private readonly ILocalSettingsService _localSettingsService;
    private readonly TrackingSourceDetector? _detector;
    private readonly ILogger<CompanionAppService>? _logger;

    private Dictionary<string, string> _commands = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, bool> _autoLaunch = new(StringComparer.OrdinalIgnoreCase);
    private string? _tobiiBridge; // "sranibro" | "brokeneye" | null = not chosen yet
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
            { TrackingSourceDetector.SranibroSourceName, new[] { "SRanibro" } },
            { TrackingSourceDetector.EtvrSourceName, new[] { "eyetrackapp", "EyeTrackVR" } },
            { TrackingSourceDetector.BabbleSourceName, new[] { "Baballonia.Desktop", "Baballonia", "BabbleApp" } },
        };

    /// <summary>
    /// Words that mark an executable as a companion utility rather than the tracking app
    /// itself. Measured need: "EyeTrackVR Firmware Flashing Tool.exe" matched the EyeTrackVR
    /// prefix and got saved as the launch command, so VRCFT would have opened the firmware
    /// flasher every time it wanted the tracker. "recorder": SRanibro ships a separate
    /// data recorder next to it.
    /// </summary>
    private static readonly string[] NotTheTrackingApp =
    {
        "firmware", "flash", "install", "setup", "uninstall", "update", "crash", "debug", "config",
        "recorder",
    };

    /// <summary>
    /// Whether an executable's name is this source's actual app.
    /// </summary>
    /// <remarks>
    /// Prefix matching alone is too loose - it has to accept version suffixes
    /// ("BrokenEye_v1.4.0") while rejecting sibling tools ("EyeTrackVR Firmware Flashing
    /// Tool"). The rule: after the prefix there may only be version-ish punctuation or
    /// digits, never a space followed by more words.
    /// </remarks>
    private static bool IsExecutableFor(string fileName, string[] prefixes)
    {
        if (NotTheTrackingApp.Any(w => fileName.Contains(w, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        foreach (var prefix in prefixes)
        {
            if (fileName.Equals(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (fileName.Length > prefix.Length &&
                fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var next = fileName[prefix.Length];
                if (next is '_' or '-' or '.' or 'v' or 'V' || char.IsDigit(next))
                {
                    return true;
                }
            }
        }

        return false;
    }

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
    private static string? DiscoverExecutable(string sourceName, IEnumerable<string>? nearbyRoots = null)
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
                // Folders next to the companion apps already configured come first: people
                // keep their VR tracking tools together (BrokenEye, EyeTrackVR and SRanibro
                // side by side in one folder), wherever that folder is.
                var roots = (nearbyRoots ?? Enumerable.Empty<string>())
                    .Where(Directory.Exists)
                    .Concat(DiscoveryRoots())
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                foreach (var root in roots)
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
                    if (!IsExecutableFor(process.ProcessName, prefixes))
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
                if (IsExecutableFor(Path.GetFileNameWithoutExtension(file), prefixes))
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
    /// The configured companion exes' folders and their parents: where a sibling app
    /// (SRanibro next to BrokenEye) most likely lives.
    /// </summary>
    private List<string> FoldersNearConfiguredApps()
    {
        var folders = new List<string>();
        try
        {
            foreach (var command in _commands.Values.ToArray())
            {
                var (file, _) = SplitCommand(command);
                if (string.IsNullOrWhiteSpace(file) || file.Contains("://") || !File.Exists(file))
                {
                    continue;
                }

                var folder = Path.GetDirectoryName(file);
                var parent = folder is null ? null : Path.GetDirectoryName(folder);
                // Parent first: it covers the folder itself at depth 1. A drive root as the
                // parent would be a full-drive crawl, so stop at the app's own folder there.
                if (parent != null && Path.GetPathRoot(parent) != parent)
                {
                    folders.Add(parent);
                }
                else if (folder != null)
                {
                    folders.Add(folder);
                }
            }
        }
        catch (Exception)
        {
            // Settings changing underneath us - discovery just uses the standard roots
        }

        return folders;
    }

    /// <summary>
    /// Sources that auto-launch unless the user turns their toggle off. These are
    /// required for eye tracking on the Dream Air, so a fresh setup gets the whole
    /// stack from one VRCFT start. Of the two Tobii bridges only the selected one
    /// (<see cref="GetTobiiBridge"/>) is ever started.
    /// </summary>
    private static readonly string[] AutoLaunchByDefault =
    {
        TrackingSourceDetector.BrokenEyeSourceName,
        TrackingSourceDetector.SranibroSourceName,
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
            { TrackingSourceDetector.SranibroSourceName, "Virtual Desktop" },
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
        TrackingSourceDetector.SranibroSourceName,
        TrackingSourceDetector.EtvrSourceName,
        TrackingSourceDetector.BabbleSourceName,
    };

    private static readonly string[] PimaxHmdKeywords = { "pimax", "dream", "aapvr" };

    private static (bool? IsPimax, string? Identity) ProbeHmd()
    {
        string? identity;
        try
        {
            identity = HmdResolver?.Invoke();
        }
        catch (Exception)
        {
            identity = null;
        }

        if (string.IsNullOrWhiteSpace(identity))
        {
            return (null, null);
        }

        var lower = identity.ToLowerInvariant();
        return (PimaxHmdKeywords.Any(k => lower.Contains(k)), identity);
    }

    /// <summary>
    /// True when SteamVR's active headset is a Pimax, false for another headset, null while
    /// SteamVR shows none. Asks OpenVR, so keep it off the tracking thread.
    /// </summary>
    public static bool? ActiveHmdIsPimax() => ProbeHmd().IsPimax;

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

            var bridge = await _localSettingsService.ReadSettingAsync<string>(TobiiBridgeSettingKey);
            _tobiiBridge = NormalizeBridge(bridge);
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
        var bridge = GetTobiiBridge();
        var bridgeSource = BridgeSourceName(bridge);

        // The Tobii bridge goes first: the camera apps (Babble, EyeTrackVR) read their eye
        // images from it, so they come up to a stream that already exists.
        var ordered = TrackingSourceDetector.KnownSources
            .OrderBy(s => s.Name == bridgeSource ? 0 : 1)
            .ToList();

        foreach (var source in ordered)
        {
            if (!GetAutoLaunch(source.Name) || !IsConfigured(source.Name))
            {
                continue;
            }

            if (IsBridgeSource(source.Name))
            {
                if (source.Name != bridgeSource)
                {
                    _logger?.LogInformation(
                        "Not auto-launching {source}: the selected Tobii bridge is {bridge} (the two cannot share the tracker).",
                        source.Name, bridgeSource);
                    continue;
                }

                var other = bridge == TobiiBridgeKind.Sranibro ? TobiiBridgeKind.BrokenEye : TobiiBridgeKind.Sranibro;
                if (IsBridgeProcessRunning(other))
                {
                    _logger?.LogInformation(
                        "Not auto-launching {source}: {other} is running and holds the Tobii tracker. Close it, or switch the bridge on the Dream Air Eyes page.",
                        source.Name, BridgeSourceName(other));
                    continue;
                }
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

    // ------------------------------------------------------------------ Tobii bridge

    /// <summary>How long a freshly (re)launched bridge gets before the watchdog judges it.</summary>
    private static readonly TimeSpan BridgeGrace = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan BridgeActionSpacing = TimeSpan.FromSeconds(60);
    private const int MaxBridgeActionsPerHeadset = 3;

    private readonly object _bridgeLock = new();
    private long _bridgeGraceUntil;
    private long _lastBridgeActionAt;
    private int _bridgeActions;
    private string? _watchedHmd;
    private bool _loggedGiveUp;
    private bool _loggedOtherBridge;

    /// <summary>The watchdog's last verdict in words, for the Dream Air Eyes page.</summary>
    public string BridgeWatchStatus { get; private set; } = "waiting for SteamVR to show a headset";

    private static string? NormalizeBridge(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "sranibro" => "sranibro",
        "brokeneye" => "brokeneye",
        _ => null,
    };

    /// <summary>
    /// Which Tobii bridge this PC uses. Both take the headset's tracker, so only one may run.
    /// Until the user picks one: SRanibro when it is configured or can be found, else BrokenEye.
    /// </summary>
    /// <remarks>The fallback may run a (cached) executable search, so avoid the UI thread
    /// the very first time.</remarks>
    public TobiiBridgeKind GetTobiiBridge()
    {
        return _tobiiBridge switch
        {
            "sranibro" => TobiiBridgeKind.Sranibro,
            "brokeneye" => TobiiBridgeKind.BrokenEye,
            _ => IsConfigured(TrackingSourceDetector.SranibroSourceName)
                ? TobiiBridgeKind.Sranibro
                : TobiiBridgeKind.BrokenEye,
        };
    }

    /// <summary>True once the user picked a bridge (as opposed to the automatic default).</summary>
    public bool TobiiBridgeChosen => _tobiiBridge != null;

    public void SetTobiiBridge(TobiiBridgeKind kind)
    {
        var value = kind == TobiiBridgeKind.Sranibro ? "sranibro" : "brokeneye";
        if (_tobiiBridge == value)
        {
            return;
        }

        _tobiiBridge = value;
        lock (_bridgeLock)
        {
            // A different bridge deserves its own fresh restart budget
            _bridgeActions = 0;
            _loggedGiveUp = false;
            _loggedOtherBridge = false;
        }
        _logger?.LogInformation("Tobii bridge set to {bridge}", BridgeSourceName(kind));
        _ = PersistAsync();
    }

    public static string BridgeSourceName(TobiiBridgeKind kind) => kind == TobiiBridgeKind.Sranibro
        ? TrackingSourceDetector.SranibroSourceName
        : TrackingSourceDetector.BrokenEyeSourceName;

    private static TobiiBridgeKind OtherBridge(TobiiBridgeKind kind) =>
        kind == TobiiBridgeKind.Sranibro ? TobiiBridgeKind.BrokenEye : TobiiBridgeKind.Sranibro;

    private static bool IsBridgeSource(string sourceName) =>
        string.Equals(sourceName, TrackingSourceDetector.SranibroSourceName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(sourceName, TrackingSourceDetector.BrokenEyeSourceName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Running processes of one bridge, matched by its exe-name prefix only.</summary>
    private static List<Process> FindBridgeProcesses(TobiiBridgeKind kind)
    {
        var prefixes = DiscoverableExecutables[BridgeSourceName(kind)];
        var found = new List<Process>();
        try
        {
            foreach (var process in Process.GetProcesses())
            {
                bool match;
                try
                {
                    match = IsExecutableFor(process.ProcessName, prefixes);
                }
                catch (Exception)
                {
                    match = false; // exited while we looked
                }

                if (match)
                {
                    found.Add(process);
                }
                else
                {
                    process.Dispose();
                }
            }
        }
        catch (Exception)
        {
            // Process enumeration unavailable
        }

        return found;
    }

    public static bool IsBridgeProcessRunning(TobiiBridgeKind kind)
    {
        var processes = FindBridgeProcesses(kind);
        foreach (var process in processes)
        {
            process.Dispose();
        }
        return processes.Count > 0;
    }

    /// <summary>Ends one bridge's processes (and nothing else). Returns how many it ended.</summary>
    private int KillBridge(TobiiBridgeKind kind)
    {
        var killed = 0;
        foreach (var process in FindBridgeProcesses(kind))
        {
            try
            {
                var name = process.ProcessName;
                process.Kill();
                process.WaitForExit(3000);
                killed++;
                _logger?.LogInformation("Tobii bridge: ended {process} (pid {pid})", name, process.Id);
            }
            catch (Exception e)
            {
                _logger?.LogWarning("Tobii bridge: could not end a {bridge} process: {message}",
                    BridgeSourceName(kind), e.Message);
            }
            finally
            {
                process.Dispose();
            }
        }

        return killed;
    }

    /// <summary>
    /// Is the bridge delivering what it exists for? BrokenEye: any frames on 5555 (it only
    /// streams once it has the tracker). SRanibro: a frame with a valid eye value, or else
    /// a real eye image from its camera server (the headset is there, just not worn); its
    /// all-invalid frames alone prove nothing because it sends them with no tracker too.
    /// Blocks up to ~2 s - never on the tracking or UI thread.
    /// </summary>
    public static bool IsBridgeHealthy(TobiiBridgeKind kind, out string evidence)
    {
        var sample = TobiiBridgeProbe.Probe(400);

        if (kind == TobiiBridgeKind.BrokenEye)
        {
            if (sample.Connected && sample.Kind == TobiiBridgeKind.BrokenEye)
            {
                evidence = "Tobii frames on TCP 5555";
                return true;
            }

            evidence = sample.Connected
                ? "TCP 5555 is open but no Tobii frames arrive (no tracker found)"
                : "nothing answers on TCP 5555";
            return false;
        }

        if (!sample.Connected)
        {
            evidence = "nothing answers on TCP 5555";
            return false;
        }

        if (sample.Kind == TobiiBridgeKind.Sranibro && sample.AnyValid)
        {
            evidence = "valid eye data on TCP 5555";
            return true;
        }

        switch (TobiiBridgeProbe.ProbeSranibroCamera())
        {
            case TobiiBridgeProbe.CameraState.Frames:
                evidence = "eye cameras deliver images (headset not on the face?)";
                return true;
            case TobiiBridgeProbe.CameraState.Busy:
                evidence = "eye cameras streaming to the camera apps";
                return true;
            case TobiiBridgeProbe.CameraState.NoFrame:
                evidence = "no eye data and no eye camera image (the tracker was not found)";
                return false;
            case TobiiBridgeProbe.CameraState.Unreachable:
                evidence = "no valid eye data, and its eye image output is off, so 'not worn' and "
                           + "'no tracker' look the same - enable eye image output in SRanibro";
                return false;
            default:
                evidence = "no valid eye data and an unexpected camera answer";
                return false;
        }
    }

    /// <summary>
    /// The bridge watchdog. Called every few seconds from the hot-swap watcher's timer
    /// thread. While SteamVR's headset is a Pimax and the selected bridge is set to launch
    /// with VRCFaceTracking: launches it when it is not running, and restarts it when it
    /// runs without delivering. That recovers the failure where the bridge started before
    /// Pimax's runtime had the eye tracker and then sat there with no device for the whole
    /// session. Bounded: 45 s grace after every launch, at least 60 s between actions, at
    /// most 3 per headset connection.
    /// </summary>
    public void CheckBridge()
    {
        if (!Monitor.TryEnter(_bridgeLock))
        {
            return; // a manual restart or switch is in progress
        }

        try
        {
            var now = Environment.TickCount64;
            var (isPimax, identity) = ProbeHmd();

            if (!string.Equals(identity, _watchedHmd, StringComparison.OrdinalIgnoreCase))
            {
                _watchedHmd = identity;
                _bridgeActions = 0;
                _loggedGiveUp = false;
                _loggedOtherBridge = false;
                // The launch pass for this headset runs right now; let it work first.
                Interlocked.Exchange(ref _bridgeGraceUntil,
                    Math.Max(Interlocked.Read(ref _bridgeGraceUntil), now + (long)BridgeGrace.TotalMilliseconds));
            }

            if (isPimax != true)
            {
                BridgeWatchStatus = isPimax == null
                    ? "idle - SteamVR shows no headset"
                    : "idle - the headset is not a Pimax";
                return;
            }

            var kind = GetTobiiBridge();
            var source = BridgeSourceName(kind);
            if (!IsConfigured(source))
            {
                BridgeWatchStatus = $"no launch command for {source} - set it on the Launch Apps page";
                return;
            }

            if (!GetAutoLaunch(source))
            {
                BridgeWatchStatus = $"off - {source} is not set to launch with VRCFaceTracking";
                return;
            }

            var other = OtherBridge(kind);
            if (IsBridgeProcessRunning(other))
            {
                BridgeWatchStatus = $"{BridgeSourceName(other)} is running instead - not touching it";
                if (!_loggedOtherBridge)
                {
                    _loggedOtherBridge = true;
                    _logger?.LogInformation(
                        "Tobii bridge watchdog: {other} is running although {selected} is selected; leaving both alone. Switch the bridge on the Dream Air Eyes page to change.",
                        BridgeSourceName(other), source);
                }
                return;
            }

            var running = IsBridgeProcessRunning(kind);
            if (now < Interlocked.Read(ref _bridgeGraceUntil))
            {
                BridgeWatchStatus = running ? $"{source} starting up" : $"waiting for {source} to be launched";
                return;
            }

            if (!running)
            {
                if (!TryTakeActionSlot(now, source))
                {
                    return;
                }

                _logger?.LogWarning(
                    "Tobii bridge watchdog: {source} is not running while the Pimax is connected - launching it ({count}/{max}).",
                    source, _bridgeActions, MaxBridgeActionsPerHeadset);
                var (started, message) = Launch(source);
                BridgeWatchStatus = started ? $"launched {source}" : $"could not launch {source}: {message}";
                return;
            }

            if (IsBridgeHealthy(kind, out var evidence))
            {
                BridgeWatchStatus = $"healthy - {evidence}";
                return;
            }

            BridgeWatchStatus = $"unhealthy - {evidence}";
            if (!TryTakeActionSlot(now, source))
            {
                return;
            }

            _logger?.LogWarning(
                "Tobii bridge watchdog: {source} is running but {evidence} - restarting it ({count}/{max}).",
                source, evidence, _bridgeActions, MaxBridgeActionsPerHeadset);
            var (ok, result) = RestartBridgeCore(kind);
            BridgeWatchStatus = ok ? $"restarted {source} ({evidence})" : $"restart of {source} failed: {result}";
        }
        catch (Exception e)
        {
            _logger?.LogWarning("Tobii bridge watchdog check failed: {message}", e.Message);
        }
        finally
        {
            Monitor.Exit(_bridgeLock);
        }
    }

    private bool TryTakeActionSlot(long now, string source)
    {
        if (_bridgeActions >= MaxBridgeActionsPerHeadset)
        {
            BridgeWatchStatus = $"gave up after {MaxBridgeActionsPerHeadset} tries for this headset connection - press Restart eye bridge";
            if (!_loggedGiveUp)
            {
                _loggedGiveUp = true;
                _logger?.LogWarning(
                    "Tobii bridge watchdog: {source} still not delivering after {max} tries - giving up until the headset reconnects or the bridge is restarted by hand.",
                    source, MaxBridgeActionsPerHeadset);
            }
            return false;
        }

        if (_lastBridgeActionAt != 0 && now - _lastBridgeActionAt < (long)BridgeActionSpacing.TotalMilliseconds)
        {
            BridgeWatchStatus += " (next try within a minute)";
            return false;
        }

        _bridgeActions++;
        _lastBridgeActionAt = now;
        return true;
    }

    /// <summary>Ends the selected bridge and starts it again. Caller holds <see cref="_bridgeLock"/>.</summary>
    private (bool Started, string Message) RestartBridgeCore(TobiiBridgeKind kind)
    {
        var source = BridgeSourceName(kind);
        if (KillBridge(kind) > 0)
        {
            // Give the Tobii runtime a moment to let go of the tracker
            Thread.Sleep(1500);
        }

        var (started, message) = Launch(source);
        if (!started && message == "already running")
        {
            Thread.Sleep(2000);
            (started, message) = Launch(source);
        }

        if (!started)
        {
            _logger?.LogWarning("Tobii bridge: could not start {source} again: {message}", source, message);
        }
        return (started, message);
    }

    /// <summary>
    /// Manual "Restart eye bridge": ends the selected bridge and starts it again, with a fresh
    /// watchdog budget. Blocks for a few seconds - call it off the UI thread.
    /// </summary>
    public (bool Started, string Message) RestartBridge()
    {
        lock (_bridgeLock)
        {
            var kind = GetTobiiBridge();
            var source = BridgeSourceName(kind);
            if (!IsConfigured(source))
            {
                return (false, $"no launch command for {source} - set it on the Launch Apps page");
            }

            _bridgeActions = 0;
            _loggedGiveUp = false;
            _logger?.LogInformation("Tobii bridge: restart of {source} requested by hand", source);
            var result = RestartBridgeCore(kind);
            BridgeWatchStatus = result.Started ? $"restarted {source} by hand" : $"restart failed: {result.Message}";
            return result;
        }
    }

    /// <summary>
    /// Explicit switch from the Dream Air Eyes page: selects the bridge, ends the other one if
    /// it runs (they cannot share the tracker), and starts the selected one when it is needed
    /// now (the other was running, or the headset is a Pimax). Blocks - call off the UI thread.
    /// </summary>
    public (bool Started, string Message) SwitchBridge(TobiiBridgeKind kind)
    {
        SetTobiiBridge(kind);
        lock (_bridgeLock)
        {
            var source = BridgeSourceName(kind);
            var other = OtherBridge(kind);
            var killed = KillBridge(other);
            if (killed > 0)
            {
                Thread.Sleep(1500);
            }

            if (!IsConfigured(source))
            {
                return (false, $"no launch command for {source} - set it on the Launch Apps page");
            }

            if (IsBridgeProcessRunning(kind))
            {
                return (true, $"{source} is already running");
            }

            if (killed == 0 && ActiveHmdIsPimax() != true)
            {
                return (true, $"{source} selected - it starts with the Pimax");
            }

            _bridgeActions = 0;
            _loggedGiveUp = false;
            var result = Launch(source);
            return result.Started ? (true, $"switched to {source}") : result;
        }
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
        var discovered = DiscoverExecutable(sourceName, FoldersNearConfiguredApps());
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
            if (IsBridgeSource(sourceName))
            {
                // Whoever launched it, the watchdog gives the new bridge its grace period
                Interlocked.Exchange(ref _bridgeGraceUntil, Environment.TickCount64 + (long)BridgeGrace.TotalMilliseconds);
            }
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
            if (_tobiiBridge != null)
            {
                await _localSettingsService.SaveSettingAsync(TobiiBridgeSettingKey, _tobiiBridge);
            }
        }
        catch (Exception e)
        {
            _logger?.LogWarning("Could not save companion app commands: {message}", e.Message);
        }
    }
}
