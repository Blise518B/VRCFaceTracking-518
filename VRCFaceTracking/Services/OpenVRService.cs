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