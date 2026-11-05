using System.Reflection;
using Microsoft.Extensions.Logging;
using Valve.VR;

namespace VRCFaceTracking.Services;

public class OpenVRService
{
    // This fork registers under its own app key so SteamVR auto-launches THIS edition,
    // not a stock VRCFaceTracking install that may also be registered on the system.
    private const string AppKey = "blise518b.vrcft";
    private const string StockAppKey = "benaclejames.vrcft";

    private CVRSystem _system;
    private readonly ILogger<OpenVRService> _logger;
    
    public OpenVRService(ILogger<OpenVRService> logger)
    {
        _logger = logger;
    }

    public bool Initialize()
    {
        EVRInitError error = EVRInitError.None;
        _system = OpenVR.Init(ref error, EVRApplicationType.VRApplication_Background);

        if (error != EVRInitError.None)
        {
            _logger.LogWarning("Failed to initialize OpenVR: {0}", error);
            IsInitialized = false;
            return IsInitialized;
        }

        // A working OpenVR CONNECTION is what everything else needs (headset
        // identity, auto-launch toggles). Manifest registration is a separate
        // best-effort concern: failing it used to mark the whole service dead,
        // which also silently disabled every consumer of the connection.
        IsInitialized = true;
        _logger.LogInformation("Successfully initialized OpenVR");

        RegisterManifest();
        return IsInitialized;
    }

    private void RegisterManifest()
    {
        var currentDirectory = Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location);
        if (string.IsNullOrEmpty(currentDirectory))
        {
            return;
        }

        // A dev build running from a bin folder must not claim the SteamVR auto-launch
        // registration: every debug run re-registered the bin path, SteamVR then kept
        // launching the STALE bin build instead of the installed app, and the installed
        // app's own registration failed with InvalidManifest against the existing key.
        // (Measured live: SteamVR resurrected a bin build days after it was built.)
        if (currentDirectory.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "Running from a build folder - leaving the SteamVR auto-launch registration to the installed app.");
            return;
        }

        var manifestPath = Path.Combine(currentDirectory, "app.vrmanifest");

        var result = OpenVR.Applications.AddApplicationManifest(manifestPath, false);
        if (result == EVRApplicationError.None)
        {
            return;
        }

        // SteamVR rejects re-registering an app key from a new location while
        // an old one is on file (measured: a dev-folder registration made every
        // install-folder registration fail with InvalidManifest). Ask SteamVR
        // where the old binary lived, evict that manifest, and retry once.
        try
        {
            var err = EVRApplicationError.None;
            var sb = new System.Text.StringBuilder(512);
            OpenVR.Applications.GetApplicationPropertyString(
                AppKey, EVRApplicationProperty.BinaryPath_String, sb, 512, ref err);
            if (err == EVRApplicationError.None && sb.Length > 0)
            {
                var oldDir = Path.GetDirectoryName(sb.ToString());
                if (!string.IsNullOrEmpty(oldDir))
                {
                    var oldManifest = Path.Combine(oldDir, "app.vrmanifest");
                    if (!string.Equals(oldManifest, manifestPath, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation(
                            "Evicting stale SteamVR registration from {old} and re-registering from {new}",
                            oldManifest, manifestPath);
                        OpenVR.Applications.RemoveApplicationManifest(oldManifest);
                        result = OpenVR.Applications.AddApplicationManifest(manifestPath, false);
                    }
                }
            }
        }
        catch (Exception e)
        {
            _logger.LogWarning("Manifest re-registration attempt failed: {0}", e.Message);
        }

        if (result != EVRApplicationError.None)
        {
            _logger.LogWarning(
                "Failed to register manifest ({0}) - SteamVR auto-launch of this app may not work, everything else is unaffected.",
                result);
        }
    }

    public void InitIfNotAlready()
    {
        if (!IsInitialized)
        {
            Initialize();
        }
    }

    public bool IsInitialized { get; private set; }

    private System.Threading.Timer? _connectionWatch;
    private long _lastReconnectAttempt;

    /// <summary>
    /// Keeps the OpenVR connection honest across SteamVR's own lifecycle. Two jobs:
    ///
    /// 1. While connected: drain the event queue. On VREvent_Quit (SteamVR shutting
    ///    down), acknowledge and disconnect cleanly. Upstream ignored the event queue
    ///    entirely, so SteamVR's shutdown escalated to force-killing the process -
    ///    which the watchdog logged as a hard crash and relaunched, over and over,
    ///    while a headset swap cycled SteamVR (measured live, 2026-08-01).
    ///
    /// 2. While disconnected: retry Initialize() whenever vrserver is running again.
    ///    Without this, a process that survived a SteamVR restart kept a dead handle
    ///    forever - GetHmdIdentity() returned null, the Pimax gate never opened, and
    ///    the hands-free headset swap silently stopped working.
    /// </summary>
    public void StartConnectionWatch()
    {
        _connectionWatch ??= new System.Threading.Timer(_ => WatchConnection(), null,
            TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
    }

    private void WatchConnection()
    {
        try
        {
            if (IsInitialized)
            {
                PumpEvents();
                return;
            }

            // Reconnect only when SteamVR is actually up, and not more than once per
            // 15s - OpenVR.Init against a starting SteamVR is slow and logs noise.
            if (Environment.TickCount64 - _lastReconnectAttempt < 15000)
            {
                return;
            }

            if (System.Diagnostics.Process.GetProcessesByName("vrserver").Length == 0)
            {
                return;
            }

            _lastReconnectAttempt = Environment.TickCount64;
            _logger.LogInformation("SteamVR is running again - reconnecting OpenVR...");
            Initialize();
        }
        catch (Exception e)
        {
            _logger.LogDebug("OpenVR connection watch: {message}", e.Message);
        }
    }

    private void PumpEvents()
    {
        try
        {
            var ev = new VREvent_t();
            var size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<VREvent_t>();
            while (_system != null && _system.PollNextEvent(ref ev, size))
            {
                if ((EVREventType)ev.eventType != EVREventType.VREvent_Quit)
                {
                    continue;
                }

                _logger.LogInformation(
                    "SteamVR is shutting down - acknowledging and disconnecting cleanly. "
                    + "The app stays up and reconnects when SteamVR returns.");

                // Tell SteamVR we heard it, so it doesn't escalate to killing us.
                try { _system.AcknowledgeQuit_Exiting(); } catch (Exception) { }
                Disconnect();
                return;
            }
        }
        catch (Exception)
        {
            // A dead runtime mid-poll means SteamVR is gone; treat it as a disconnect
            Disconnect();
        }
    }

    private void Disconnect()
    {
        IsInitialized = false;
        _system = null;
        try
        {
            OpenVR.Shutdown();
        }
        catch (Exception)
        {
            // Already gone - the goal (no live handle) is met either way
        }
    }

    /// <summary>
    /// Identity of the active SteamVR headset ("Manufacturer Model TrackingSystem",
    /// e.g. "Oculus Quest Pro oculus" or "Pimax Dream Air aapvr"), or null when
    /// SteamVR is not up or no HMD is connected. Used to decide which companion
    /// apps belong to the headset actually in use.
    /// </summary>
    public string? GetHmdIdentity()
    {
        if (!IsInitialized || _system == null)
        {
            return null;
        }

        try
        {
            var parts = new List<string>();
            var sb = new System.Text.StringBuilder(128);
            foreach (var prop in new[]
                     {
                         ETrackedDeviceProperty.Prop_ManufacturerName_String,
                         ETrackedDeviceProperty.Prop_ModelNumber_String,
                         ETrackedDeviceProperty.Prop_TrackingSystemName_String,
                     })
            {
                sb.Clear();
                var err = ETrackedPropertyError.TrackedProp_Success;
                _system.GetStringTrackedDeviceProperty(OpenVR.k_unTrackedDeviceIndex_Hmd, prop, sb, 128, ref err);
                if (err == ETrackedPropertyError.TrackedProp_Success && sb.Length > 0)
                {
                    parts.Add(sb.ToString());
                }
            }
            return parts.Count > 0 ? string.Join(" ", parts) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Name of the SteamVR driver that registered the active headset - "vrlink" for
    /// Steam Link, "oculus_virtualdesktop" for Virtual Desktop, "aapvr" for a Pimax -
    /// or null when SteamVR is not up or no HMD is connected. Read from the registered
    /// device type, which SteamVR builds as "driver/serial". Unlike the tracking-system
    /// name in <see cref="GetHmdIdentity"/>, which drivers choose freely (Steam Link
    /// and Virtual Desktop can both report a Quest as "oculus"), this names the
    /// software actually streaming to the headset.
    /// </summary>
    public string? GetHmdDriver()
    {
        var system = _system;
        if (!IsInitialized || system == null)
        {
            return null;
        }

        try
        {
            var sb = new System.Text.StringBuilder(256);
            var err = ETrackedPropertyError.TrackedProp_Success;
            system.GetStringTrackedDeviceProperty(OpenVR.k_unTrackedDeviceIndex_Hmd,
                ETrackedDeviceProperty.Prop_RegisteredDeviceType_String, sb, 256, ref err);
            if (err != ETrackedPropertyError.TrackedProp_Success || sb.Length == 0)
            {
                return null;
            }

            var registered = sb.ToString();
            var slash = registered.IndexOf('/');
            return slash > 0 ? registered[..slash] : registered;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public bool AutoStart
    {
        get => IsInitialized && OpenVR.Applications.GetApplicationAutoLaunch(AppKey);
        set
        {
            if (!IsInitialized && !Initialize())
            {
                _logger.LogWarning("Failed to set AutoStart preference. OpenVR couldn't be initialized.");
                return;
            }

            var setAutoLaunchResult = OpenVR.Applications.SetApplicationAutoLaunch(AppKey, value);
            if (setAutoLaunchResult != EVRApplicationError.None)
            {
                _logger.LogError("Failed to set auto launch: {0}", setAutoLaunchResult);
            }

            // If a stock VRCFaceTracking install also has auto-launch enabled, both versions
            // would start with SteamVR and fight each other - silence the stock one.
            if (value)
            {
                try
                {
                    if (OpenVR.Applications.GetApplicationAutoLaunch(StockAppKey))
                    {
                        OpenVR.Applications.SetApplicationAutoLaunch(StockAppKey, false);
                        _logger.LogInformation("Disabled auto-launch of the stock VRCFaceTracking so only this edition starts with SteamVR.");
                    }
                }
                catch (Exception e)
                {
                    _logger.LogWarning("Could not check/disable stock VRCFaceTracking auto-launch: {0}", e.Message);
                }
            }
        }
    }
} 