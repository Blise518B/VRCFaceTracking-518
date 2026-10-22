using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
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
    // iFacialMocap streams at ~60Hz (a packet every ~16ms), so 50ms is three packet
    // periods - plenty. This window is a hard BLOCK for whoever runs the sweep, and it
    // used to be 250ms: measured on a live rig, that single Poll made every cold scan
    // cost ~430ms. See ProbeUdpStream for why the probe must wait at all.
    private static readonly TimeSpan UdpProbeWindow = TimeSpan.FromMilliseconds(50);

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
        /// Bespoke probe for sources none of the generic evidence types can
        /// judge correctly. Returns an evidence string when live, else null.
        /// </summary>
        public Func<string?>? CustomProbe { get; init; }

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
            // Neither generic evidence type can judge VD correctly, measured live
            // both ways: its Streamer/Service processes run from Windows startup
            // (permanent false positive that stole the eye slot from a Dream Air),
            // and the only shared memory it ever creates is VirtualDesktop.BodyState
            // (the FaceState/FaceTracking names never exist, which made the
            // process-free fix a permanent false NEGATIVE on a real Quest Pro).
            // The custom probe reads the validity flags inside BodyState - the same
            // flags the VD module itself keys tracking on - and requires the
            // expression weights to actually be changing, so stale flags from an
            // ended session don't count.
            CustomProbe = ProbeVirtualDesktopFace,
            Notes = "Quest Pro / Pico face and eye tracking streamed over Virtual Desktop. Detected "
                  + "by live face data inside VirtualDesktop.BodyState, not by installed software.",
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
            // "etvr" is in this list ON PURPOSE: the merged DreamAir eye module ships
            // under the stock dll/manifest name "ETVRTrackingModule" / "ETVR Tracking
            // Module", so nothing in its identity says BrokenEye or Dream Air. Without
            // this mapping the module was gated ONLY on the EyeTrackVR app - and on a
            // Babble-for-expressions setup that app never runs, so a Pimax day ended
            // with BrokenEye streaming Tobii frames and "no module loaded" (measured
            // live, 2026-08-01). BrokenEye is niche enough that an etvr-named module
            // starting when it streams is exactly what every BrokenEye user wants.
            Name = BrokenEyeSourceName,
            ModuleKeywords = new[] { "brokeneye", "dreamair", "dream air", "etvr" },
            // NOT process-based: BrokenEye auto-launches with VRCFT, so on a
            // Quest Pro day the process runs while the Dream Air sits on the
            // desk - and a process-detected BrokenEye would start the DreamAir
            // module to race Virtual Desktop for the eye slot. The probe asks
            // its TCP API for actual frames instead: BrokenEye only streams
            // while the headset is live (measured), so frames = Dream Air
            // really in use. The app being merely open is the Launch Apps
            // page's business, not module gating's.
            CustomProbe = ProbeBrokenEyeStream,
            Notes = "Bridges the headset's Tobii tracker to TCP + MJPEG for the merged eye module "
                  + "and the camera apps. Detected by live Tobii frames, not by the app being open.",
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
    ///
    /// The sweep itself runs OUTSIDE the cache lock: probes block for real time (process
    /// enumeration, a UDP listen window), and holding the lock through them made every
    /// other caller queue behind the slowest one - including, before ScanCached existed,
    /// the 100Hz tracking thread. Two threads sweeping concurrently is harmless; every
    /// probe is a read-only OS query.
    /// </summary>
    public List<TrackingSourceStatus> Scan(bool force = false)
    {
        lock (_cacheLock)
        {
            if (!force && _cached != null && DateTime.UtcNow - _cachedAt < CacheLifetime)
            {
                return _cached;
            }
        }

        var results = Sweep(force);

        lock (_cacheLock)
        {
            _cached = results;
            _cachedAt = DateTime.UtcNow;
            return results;
        }
    }

    private int _backgroundRefreshInFlight;

    /// <summary>
    /// Never-blocking read for hot paths (the 100Hz tracking thread, per-frame UI): returns
    /// the last completed sweep immediately and, when that is stale, refreshes it on a
    /// background thread for the NEXT caller. The one thing this must never do is make the
    /// caller pay for a probe - a cold sweep costs hundreds of milliseconds, and paying it
    /// on the tracking thread froze the avatar for exactly that long every few seconds.
    /// </summary>
    public List<TrackingSourceStatus> ScanCached()
    {
        List<TrackingSourceStatus>? cached;
        bool stale;
        lock (_cacheLock)
        {
            cached = _cached;
            stale = cached == null || DateTime.UtcNow - _cachedAt >= CacheLifetime;
        }

        if (stale && Interlocked.CompareExchange(ref _backgroundRefreshInFlight, 1, 0) == 0)
        {
            Task.Run(() =>
            {
                try
                {
                    Scan();
                }
                catch (Exception e)
                {
                    _logger?.LogDebug("Background source refresh failed: {message}", e.Message);
                }
                finally
                {
                    Volatile.Write(ref _backgroundRefreshInFlight, 0);
                }
            });
        }

        return cached ?? new List<TrackingSourceStatus>();
    }

    private List<TrackingSourceStatus> Sweep(bool force = false)
    {
        {
            var runningProcesses = SafeProcessNames();
            var pipes = SafePipeNames();

            var results = new List<TrackingSourceStatus>();
            foreach (var source in KnownSources)
            {
                var evidence = new List<string>();
                var selfSustainOnly = false;

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

                    if (source.CustomProbe != null)
                    {
                        var custom = source.CustomProbe();
                        if (custom != null)
                        {
                            evidence.Add(custom);
                        }
                    }

                    foreach (var port in source.UdpStreamPorts)
                    {
                        var (bindable, dataWaiting) = ProbeUdpStreamCached(port, force);
                        if (!bindable)
                        {
                            // A held port only means something when WE hold it: our own
                            // module process listening there proves the module runs, so
                            // don't pull the rug out from under it. Held by any other
                            // process, the port says nothing about a phone streaming -
                            // and treating it as evidence once created a start->can't
                            // bind->die->restart loop feeding on its own listener.
                            if (UdpPortOwnedByOurModuleProcess(port))
                            {
                                evidence.Add($"UDP {port} in use (the module is already listening)");
                                selfSustainOnly = evidence.Count == 1;
                            }
                        }
                        else if (dataWaiting)
                        {
                            evidence.Add($"data arriving on UDP {port}");
                            selfSustainOnly = false;
                        }
                    }
                }

                results.Add(new TrackingSourceStatus
                {
                    Name = source.Name,
                    Detected = evidence.Count > 0,
                    Strong = evidence.Count > 0 && !source.GenericRuntime,
                    SelfSustainOnly = selfSustainOnly,
                    Evidence = evidence.Count > 0
                        ? string.Join(", ", evidence) + (source.GenericRuntime ? " (generic VR runtime)" : string.Empty)
                        : source.NeverAutoDetect ? "never auto-detected by design" : "not running",
                    Notes = source.Notes,
                });
            }

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
    /// <param name="forStart">
    /// True when the answer will be used to START a module (initial load, hot-plug,
    /// post-death restart): self-sustain-only evidence is ignored, because a fresh
    /// module can't use a port our previous instance holds. False when the answer only
    /// decides whether an already-running module still belongs (the hot-swap set):
    /// there, self-sustain evidence must count or a live module gets torn down purely
    /// because its own listener hides the stream from the probe.
    /// </param>
    public (bool Load, string Reason) Decide(string? moduleName, string? dllName, string? path,
                                             ModuleStartMode mode, bool gatingEnabled,
                                             bool forStart = true)
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
            .Where(s => s is { Detected: true } && !(forStart && s.SelfSustainOnly))
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
            // Dispose every Process: each one is finalizable, and a sweep every 10 s left a
            // few hundred of them per sweep for the finalizer thread and the GC to clean up.
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    try
                    {
                        var name = p.ProcessName;
                        if (!string.IsNullOrEmpty(name))
                        {
                            names.Add(name);
                        }
                    }
                    catch (Exception)
                    {
                        // Exited between the snapshot and the read
                    }
                }
            }

            return names;
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
    // Even at 50ms, the listen window is a real block - and this probe runs for a port
    // nobody is usually streaming to, so it pays the FULL window on every quiet sweep.
    // Cached like the other live-data probes so at most one sweep per lifetime pays it.
    private static readonly Dictionary<int, (long At, bool Bindable, bool DataWaiting)> UdpProbeCache = new();

    private static (bool Bindable, bool DataWaiting) ProbeUdpStreamCached(int port, bool force = false)
    {
        // A forced scan means the caller needs the truth RIGHT NOW - the canonical case
        // is the just-died-module re-check, where the port was freed milliseconds ago
        // and a <=5s-old "in use" verdict is precisely the wrong answer (it restarted a
        // dead module into an infinite start->die loop, measured live 2026-08-01).
        if (!force)
        {
            lock (ProbeCacheLock)
            {
                if (UdpProbeCache.TryGetValue(port, out var hit) &&
                    Environment.TickCount64 - hit.At < ProbeCacheMs)
                {
                    return (hit.Bindable, hit.DataWaiting);
                }
            }
        }

        var result = ProbeUdpStream(port);
        lock (ProbeCacheLock)
        {
            UdpProbeCache[port] = (Environment.TickCount64, result.Bindable, result.DataWaiting);
        }
        return result;
    }

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

    // Live-data probes can cost tens to hundreds of milliseconds, and the
    // Launch Apps page force-scans once a second on the UI thread - so each
    // probe caches its own verdict briefly regardless of Scan's force flag.
    private static readonly object ProbeCacheLock = new();
    private static long _vdProbeAt;
    private static string? _vdProbeResult;
    private static long _beProbeAt;
    private static string? _beProbeResult;
    private const int ProbeCacheMs = 5000;

    /// <summary>
    /// Reads the validity flags inside VirtualDesktop.BodyState (offsets from
    /// the VD module's own FaceState struct: FaceFlags at 0, eye-following
    /// bool at 1, per-eye validity at 292/293) and then samples the first
    /// expression weights twice, 60 ms apart. Flags say a face-capable
    /// headset delivered data; changing weights say it is happening NOW
    /// rather than being a leftover of an earlier session.
    /// </summary>
    private static string? ProbeVirtualDesktopFace()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        lock (ProbeCacheLock)
        {
            if (Environment.TickCount64 - _vdProbeAt < ProbeCacheMs)
            {
                return _vdProbeResult;
            }
        }

        var result = ProbeVirtualDesktopFaceUncached();
        lock (ProbeCacheLock)
        {
            _vdProbeAt = Environment.TickCount64;
            _vdProbeResult = result;
        }
        return result;
    }

    private static string? ProbeVirtualDesktopFaceUncached()
    {
        try
        {
            using var mmf = MemoryMappedFile.OpenExisting("VirtualDesktop.BodyState", MemoryMappedFileRights.Read);
            using var view = mmf.CreateViewAccessor(0, 300, MemoryMappedFileAccess.Read);

            var faceFlags = view.ReadByte(0);
            var eyeFollowing = view.ReadBoolean(1);
            var leftEye = view.ReadBoolean(292);
            var rightEye = view.ReadBoolean(293);

            if (!leftEye && !rightEye && !eyeFollowing && (faceFlags & 0b0001) == 0)
            {
                return null;
            }

            var first = new byte[32];
            view.ReadArray(4, first, 0, first.Length);
            Thread.Sleep(60);
            var second = new byte[32];
            view.ReadArray(4, second, 0, second.Length);

            return first.AsSpan().SequenceEqual(second)
                ? null
                : "live face data in VirtualDesktop.BodyState";
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Asks BrokenEye's TCP API (127.0.0.1:5555) for actual frames: connect,
    /// send the 0x00 subscribe byte, and see whether any framed data arrives
    /// within ~400 ms. BrokenEye serves multiple clients by design and only
    /// streams while the headset is live, so this cannot disturb the module's
    /// own connection and cleanly separates "app open" from "Dream Air in use".
    /// </summary>
    private static string? ProbeBrokenEyeStream()
    {
        lock (ProbeCacheLock)
        {
            if (Environment.TickCount64 - _beProbeAt < ProbeCacheMs)
            {
                return _beProbeResult;
            }
        }

        var result = ProbeBrokenEyeStreamUncached();
        lock (ProbeCacheLock)
        {
            _beProbeAt = Environment.TickCount64;
            _beProbeResult = result;
        }
        return result;
    }

    private static string? ProbeBrokenEyeStreamUncached()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            // Cancel and wait for the connect, never just walk away from it. Windows takes
            // about 2 s to refuse a loopback connect, so with BrokenEye closed this always
            // timed out - and BeginConnect + WaitOne then disposed the socket with the
            // connect still pending. That connect faulted with nobody observing it: the
            // "[App] Ignored an abandoned socket task from connection teardown" line once
            // per sweep, ~6 a minute all session.
            using (var timeout = new CancellationTokenSource(250))
            {
                try
                {
                    socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, 5555), timeout.Token)
                        .AsTask().GetAwaiter().GetResult();
                }
                catch (Exception)
                {
                    return null; // refused, timed out or wedged - not live either way
                }
            }

            socket.NoDelay = true;
            socket.ReceiveTimeout = 400;
            socket.Send(new byte[] { 0x00 });

            // A packet header is [1B id][4B length]; any bytes at all mean the
            // tracker is delivering frames right now.
            var buffer = new byte[64];
            var read = socket.Receive(buffer);
            return read > 0 ? "Tobii frames streaming on TCP 5555" : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ------------------------------------------------ UDP port owner resolution

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_UDPROW_OWNER_PID
    {
        public uint LocalAddr;
        public uint LocalPort; // network byte order in the low 16 bits
        public uint OwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(IntPtr pUdpTable, ref int pdwSize,
        bool sort, int ipVersion, int tableClass, uint reserved);

    private const int AF_INET = 2;
    private const int UDP_TABLE_OWNER_PID = 1;

    /// <summary>
    /// True when the process listening on <paramref name="port"/> is one of our own
    /// sandbox module processes. That distinction decides whether a busy port is
    /// evidence ("our module is running") or noise ("someone else owns that port").
    /// Conservative on failure: unknown owner counts as not-ours.
    /// </summary>
    private static bool UdpPortOwnedByOurModuleProcess(int port)
    {
        try
        {
            var size = 0;
            GetExtendedUdpTable(IntPtr.Zero, ref size, false, AF_INET, UDP_TABLE_OWNER_PID, 0);
            if (size <= 0)
            {
                return false;
            }

            var table = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedUdpTable(table, ref size, false, AF_INET, UDP_TABLE_OWNER_PID, 0) != 0)
                {
                    return false;
                }

                var rowCount = Marshal.ReadInt32(table);
                var rowPtr = table + sizeof(int);
                var rowSize = Marshal.SizeOf<MIB_UDPROW_OWNER_PID>();

                for (var i = 0; i < rowCount; i++)
                {
                    var row = Marshal.PtrToStructure<MIB_UDPROW_OWNER_PID>(rowPtr + i * rowSize);
                    var rowPort = (ushort)System.Net.IPAddress.NetworkToHostOrder((short)(row.LocalPort & 0xFFFF));
                    if (rowPort != port)
                    {
                        continue;
                    }

                    try
                    {
                        using var owner = Process.GetProcessById((int)row.OwningPid);
                        return owner.ProcessName.StartsWith("VRCFaceTracking.ModuleProcess",
                            StringComparison.OrdinalIgnoreCase);
                    }
                    catch (Exception)
                    {
                        return false; // owner already gone or inaccessible
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(table);
            }
        }
        catch (Exception)
        {
            // Interop unavailable - err on "not ours" so a foreign port never fakes a source
        }

        return false;
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

    /// <summary>
    /// The only evidence is our own already-running module (its listener on the stream
    /// port). Good enough to KEEP that module alive - it may be mid-stream and its
    /// traffic is invisible from outside - but never a reason to START one: a fresh
    /// module can neither bind the port nor receive anything through it.
    /// </summary>
    public bool SelfSustainOnly { get; init; }
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
