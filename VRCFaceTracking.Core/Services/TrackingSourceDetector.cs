using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace VRCFaceTracking.Core.Services;

/// <summary>
/// Works out which face/eye tracking sources are actually live on this PC, so only the
/// matching modules get started instead of every installed module racing to initialise.
///
/// All probes are read-only OS queries - running processes, memory-mapped files and named
/// pipes. Nothing is opened exclusively or modified, so probing can never steal a device
/// from the module that needs it.
/// </summary>
public class TrackingSourceDetector
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan UdpProbeWindow = TimeSpan.FromMilliseconds(250);

    /// <summary>Source names referenced from other services (companion app defaults).</summary>
    public const string EtvrSourceName = "EyeTrackVR (ETVR)";
    public const string BrokenEyeSourceName = "BrokenEye (Dream Air Tobii bridge)";
    public const string BabbleSourceName = "Project Babble";

    /// <summary>A thing that can feed tracking data, and how to recognise it.</summary>
    public sealed class TrackingSource
    {
        public required string Name { get; init; }

        /// <summary>Substrings matched against a module's name, dll and path.</summary>
        public required string[] ModuleKeywords { get; init; }

        /// <summary>
        /// Process names (without .exe) that mean this source is up. A trailing *
        /// matches by prefix - "BrokenEye*" catches BrokenEye_v1.4.0 and whatever
        /// version comes next.
        /// </summary>
        public string[] Processes { get; init; } = Array.Empty<string>();

        /// <summary>Memory-mapped file names that exist while this source is streaming.</summary>
        public string[] MemoryMappedFiles { get; init; } = Array.Empty<string>();

        /// <summary>Named pipes that exist while this source is streaming.</summary>
        public string[] Pipes { get; init; } = Array.Empty<string>();

        /// <summary>
        /// UDP ports this source streams TO, for sources that are just a network stream with
        /// no process on this PC (a phone app, for instance). Probed by briefly listening -
        /// see ProbeUdpStream for why that is safe and why ownership is never used.
        /// </summary>
        public int[] UdpStreamPorts { get; init; } = Array.Empty<int>();

        /// <summary>
        /// True when this source is only recognisable by a shared VR runtime (SteamVR,
        /// the Oculus service) rather than anything specific to face tracking. Those run
        /// for any headset use, so on their own they are weak evidence: a source with
        /// specific evidence wins over one of these.
        /// </summary>
        public bool GenericRuntime { get; init; }

        /// <summary>
        /// Set for sources that can never be auto-detected (fake/replay data). They are
        /// only started if the user explicitly sets the module to Always.
        /// </summary>
        public bool NeverAutoDetect { get; init; }

        public string? Notes { get; init; }
    }


    /// <summary>
    /// Known sources. Process names are the primary signal because they are reliable across
    /// versions; shared memory and pipes are extra evidence.
    ///
    /// Deliberately no UDP port probing: measuring this on a live setup showed port 8889
    /// held by VRCFaceTracking's *own* ETVR module process, so a port probe reports our own
    /// listener rather than the source and would keep a module "detected" forever once it
    /// had started once.
    /// </summary>
    public static readonly IReadOnlyList<TrackingSource> KnownSources = new List<TrackingSource>
    {
        new()
        {
            Name = "Virtual Desktop",
            ModuleKeywords = new[] { "virtualdesktop", "vdesktop", "vdxr" },
            Processes = new[] { "VirtualDesktop.Streamer", "VirtualDesktop.Server", "VirtualDesktop.Service" },
            MemoryMappedFiles = new[] { "VirtualDesktop.FaceState", "VirtualDesktop.FaceTracking", "VirtualDesktop.BodyState" },
            Notes = "Quest Pro / Pico face and eye tracking streamed over Virtual Desktop.",
        },
        new()
        {
            Name = "ALVR",
            ModuleKeywords = new[] { "alvr" },
            Processes = new[] { "ALVR Dashboard", "alvr_dashboard", "alvr_server", "ALVR" },
            Notes = "Quest face tracking streamed over ALVR.",
        },
        new()
        {
            Name = "Steam Link / SteamVR face tracking",
            ModuleKeywords = new[] { "steamlink", "steamvr", "questpro", "metaquest" },
            Processes = new[] { "vrserver", "steamlink", "vrmonitor" },
            GenericRuntime = true,
            Notes = "Face tracking delivered through SteamVR itself. SteamVR runs for any headset, "
                  + "so this only counts when nothing more specific is streaming.",
        },
        new()
        {
            Name = "Meta / Oculus Link",
            ModuleKeywords = new[] { "oculus", "metalink", "ovr" },
            Processes = new[] { "OVRServer_x64", "OculusClient", "OVRServiceLauncher" },
            GenericRuntime = true,
            Notes = "Quest Pro over a wired Meta Link connection. The Oculus service runs whenever "
                  + "it is installed, so this only counts when nothing more specific is streaming.",
        },
        new()
        {
            Name = "SRanipal (Vive Pro Eye / Vive Facial Tracker)",
            ModuleKeywords = new[] { "sranipal", "vive", "vivepro", "vft" },
            Processes = new[] { "sr_runtime", "SRanipalRuntime", "SRworks", "sr_service" },
            Notes = "HTC's runtime; must be running and showing a robot icon in the tray.",
        },
        new()
        {
            Name = EtvrSourceName,
            ModuleKeywords = new[] { "etvr", "eyetrack" },
            Processes = new[] { "EyeTrackApp", "eyetrackvr", "ETVR", "eyetrackapp" },
            Notes = "DIY eye tracking; the EyeTrackVR app must be open and streaming.",
        },
        new()
        {
            Name = "iFacialMocap (iPhone / iPad)",
            ModuleKeywords = new[] { "ifacialmocap", "ifacial" },
            UdpStreamPorts = new[] { 49983 },
            Notes = "A phone app, so there is no process on this PC to look for - detected by "
                  + "listening for the phone's stream on UDP 49983. Start iFacialMocap on the "
                  + "phone with this PC's IP set as the destination.",
        },
        new()
        {
            Name = BabbleSourceName,
            ModuleKeywords = new[] { "babble" },
            Processes = new[] { "BabbleApp", "Babble", "babbleapp", "Baballonia", "Baballonia.Desktop" },
            Notes = "DIY mouth (and, in the Baballonia Steam alpha, eye) tracking; the app must be open and streaming.",
        },
        new()
        {
            Name = BrokenEyeSourceName,
            ModuleKeywords = new[] { "brokeneye", "dreamair", "dream air" },
            // Release exes carry the version in the name (BrokenEye_v1.4.0.exe).
            Processes = new[] { "BrokenEye*", "Broken Eye*" },
            Notes = "Bridges the headset's Tobii tracker to TCP + MJPEG for the merged eye module "
                  + "and the camera apps. Must be running and connected to the headset.",
        },
        new()
        {
            Name = "Varjo",
            ModuleKeywords = new[] { "varjo" },
            Processes = new[] { "varjo-service", "VarjoService", "varjo_session" },
        },
        new()
        {
            Name = "Pimax (Droolon)",
            ModuleKeywords = new[] { "pimax", "droolon", "7invensun" },
            Processes = new[] { "PimaxClient", "aSeeVRClient", "PiService" },
        },
        new()
        {
            Name = "Tobii",
            ModuleKeywords = new[] { "tobii" },
            Processes = new[] { "Tobii.Service", "TobiiGameHub", "Tobii.EyeX.Interaction" },
        },
        new()
        {
            Name = "Simulated / replay data",
            ModuleKeywords = new[] { "simulat", "replay", "dummy", "fakeface" },
            NeverAutoDetect = true,
            Notes = "Produces synthetic data. Never started automatically - set it to Always if you want it.",
        },
    };

    private readonly ILogger<TrackingSourceDetector>? _logger;
    private readonly object _cacheLock = new();
    private List<TrackingSourceStatus>? _cached;
    private DateTime _cachedAt;

    public TrackingSourceDetector(ILogger<TrackingSourceDetector>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Probes every known source. Results are cached briefly so a UI refresh and a module
    /// startup pass don't each pay for a full sweep.
    /// </summary>
    public List<TrackingSourceStatus> Scan(bool force = false)
    {
        lock (_cacheLock)
        {
            if (!force && _cached != null && DateTime.UtcNow - _cachedAt < CacheLifetime)
            {
                return _cached;
            }

            var runningProcesses = SafeProcessNames();
            var pipes = SafePipeNames();

            var results = new List<TrackingSourceStatus>();
            foreach (var source in KnownSources)
            {
                var evidence = new List<string>();

                if (!source.NeverAutoDetect)
                {
                    foreach (var name in source.Processes)
                    {
                        var wanted = name.Replace(".exe", string.Empty);
                        var hit = wanted.EndsWith('*')
                            ? runningProcesses.Any(p => p.StartsWith(wanted[..^1], StringComparison.OrdinalIgnoreCase))
                            : runningProcesses.Contains(wanted);
                        if (hit)
                        {
                            evidence.Add($"process {name}");
                        }
                    }

                    foreach (var mmf in source.MemoryMappedFiles)
                    {
                        if (MemoryMappedFileExists(mmf))
                        {
                            evidence.Add($"shared memory {mmf}");
                        }
                    }

                    foreach (var pipe in source.Pipes)
                    {
                        if (pipes.Contains(pipe, StringComparer.OrdinalIgnoreCase))
                        {
                            evidence.Add($"pipe {pipe}");
                        }
                    }

                    foreach (var port in source.UdpStreamPorts)
                    {
                        var (bindable, dataWaiting) = ProbeUdpStream(port);
                        if (!bindable)
                        {
                            // Someone already listens there, which in practice is this
                            // module's own sandbox process: it's running, so report the
                            // source as live rather than pulling the rug out from under it.
                            evidence.Add($"UDP {port} in use (the module is already listening)");
                        }
                        else if (dataWaiting)
                        {
                            evidence.Add($"data arriving on UDP {port}");
                        }
                    }
                }

                results.Add(new TrackingSourceStatus
                {
                    Name = source.Name,
                    Detected = evidence.Count > 0,
                    Strong = evidence.Count > 0 && !source.GenericRuntime,
                    Evidence = evidence.Count > 0
                        ? string.Join(", ", evidence) + (source.GenericRuntime ? " (generic VR runtime)" : string.Empty)
                        : source.NeverAutoDetect ? "never auto-detected by design" : "not running",
                    Notes = source.Notes,
                });
            }

            _cached = results;
            _cachedAt = DateTime.UtcNow;
            return results;
        }
    }

    /// <summary>
    /// Which known sources a module belongs to, matched on its name, dll and path.
    /// Empty means we have no mapping for it.
    /// </summary>
    public List<TrackingSource> SourcesForModule(string? moduleName, string? dllName, string? path)
    {
        var haystack = string.Join(" ", new[] { moduleName, dllName, path }
            .Where(s => !string.IsNullOrWhiteSpace(s))).ToLowerInvariant();

        return KnownSources
            .Where(source => source.ModuleKeywords.Any(k => haystack.Contains(k, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    /// <summary>
    /// Should this module be started? Returns a human-readable reason either way, which the
    /// UI shows and the log records - a silent skip would be indistinguishable from a bug.
    /// </summary>
    public (bool Load, string Reason) Decide(string? moduleName, string? dllName, string? path,
                                             ModuleStartMode mode, bool gatingEnabled)
    {
        if (mode == ModuleStartMode.Never)
        {
            return (false, "set to Never");
        }
        if (mode == ModuleStartMode.Always)
        {
            return (true, "set to Always");
        }
        if (!gatingEnabled)
        {
            return (true, "source gating is off");
        }

        var matches = SourcesForModule(moduleName, dllName, path);
        if (matches.Count == 0)
        {
            // Permissive on purpose: an unrecognised module might be someone's brand new
            // hardware, and silently refusing to start it would look like the app is broken.
            return (true, "no source mapping known - started anyway");
        }

        var statuses = Scan();
        var live = matches
            .Select(m => statuses.FirstOrDefault(s => s.Name == m.Name))
            .Where(s => s is { Detected: true })
            .Select(s => s!)
            .ToList();

        if (live.Count == 0)
        {
            var names = string.Join(" / ", matches.Select(m => m.Name));
            return (false, $"{names} not detected");
        }

        var strong = live.FirstOrDefault(s => s.Strong);
        if (strong != null)
        {
            return (true, $"{strong.Name} detected ({strong.Evidence})");
        }

        // Only generic-runtime evidence. If something specific is streaming elsewhere, that
        // is what the user is actually on, so don't also start this one. With nothing
        // specific anywhere, a shared runtime is the best signal available - allow it rather
        // than leave someone with no tracking at all.
        var specificElsewhere = statuses.FirstOrDefault(s => s.Strong);
        if (specificElsewhere != null)
        {
            return (false, $"only {live[0].Name} (a shared VR runtime) matched, " +
                           $"while {specificElsewhere.Name} is actively streaming");
        }

        return (true, $"{live[0].Name} detected ({live[0].Evidence}); nothing more specific is running");
    }

    // ---------------------------------------------------------------- probes

    private HashSet<string> SafeProcessNames()
    {
        try
        {
            return Process.GetProcesses()
                .Select(p =>
                {
                    try
                    {
                        return p.ProcessName;
                    }
                    catch (Exception)
                    {
                        return string.Empty;
                    }
                })
                .Where(n => !string.IsNullOrEmpty(n))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e)
        {
            _logger?.LogWarning("Could not enumerate processes: {message}", e.Message);
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static List<string> SafePipeNames()
    {
        try
        {
            return Directory.GetFiles(@"\\.\pipe\")
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!)
                .ToList();
        }
        catch (Exception)
        {
            return new List<string>();
        }
    }

    /// <summary>
    /// Listens on a stream port for a moment to see whether anything is actually sending.
    ///
    /// Two rules make this safe. It only ever binds a port that is FREE, so it can never take
    /// a datagram away from the module that needs it - a failed bind is itself the answer
    /// ("the module already has it"). And it asks whether data is waiting rather than reading
    /// it, so nothing is consumed even in the moment we hold the socket.
    ///
    /// Port OWNERSHIP is deliberately not used as evidence anywhere: measuring that on a live
    /// setup found port 8889 held by VRCFaceTracking's own ETVR module, i.e. a probe reporting
    /// our own listener back to us and keeping a module "detected" forever.
    /// </summary>
    private static (bool Bindable, bool DataWaiting) ProbeUdpStream(int port)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            // No address reuse: a busy port MUST fail to bind, that's the signal.
            socket.ExclusiveAddressUse = true;
            // Phones send to this PC's LAN address, so loopback-only would never see it.
            socket.Bind(new IPEndPoint(IPAddress.Any, port));

            // iFacialMocap streams at ~60 Hz, so a live phone shows up in a few ms;
            // this window is generous while staying short enough for a UI refresh.
            return (true, socket.Poll(UdpProbeWindow, SelectMode.SelectRead));
        }
        catch (SocketException)
        {
            return (false, false);
        }
        catch (Exception)
        {
            return (false, false);
        }
    }

    private static bool MemoryMappedFileExists(string name)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            // Read-only open, immediately disposed: never takes the mapping away from
            // whichever process is actually streaming into it.
            using var mmf = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>
/// Result of probing one tracking source. Top-level rather than nested inside the detector
/// so XAML can bind to it directly - x:DataType handling of nested types is unreliable.
/// </summary>
public sealed class TrackingSourceStatus
{
    public required string Name { get; init; }
    public bool Detected { get; init; }
    public required string Evidence { get; init; }
    public string? Notes { get; init; }

    /// <summary>Detected via something specific to this source, not a shared VR runtime.</summary>
    public bool Strong { get; init; }

    /// <summary>Detected, but only by a shared VR runtime that any headset would start.</summary>
    public bool WeakOnly => Detected && !Strong;
}

/// <summary>How a module participates in source-based auto-start.</summary>
public enum ModuleStartMode
{
    /// <summary>Start only when a matching source is detected.</summary>
    Auto,

    /// <summary>Always start, regardless of detection.</summary>
    Always,

    /// <summary>Never start.</summary>
    Never,
}
