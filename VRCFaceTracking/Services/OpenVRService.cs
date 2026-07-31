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
        
        // Our app.vrmanifest is next to the executable, so we can just use the current directory of the executable
        var currentDirectory = Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location);
        var fullManifestPath = Path.Combine(currentDirectory, "app.vrmanifest"); // Replace is for Linux
        var manifestRegisterResult = OpenVR.Applications.AddApplicationManifest(fullManifestPath, false);
        if (manifestRegisterResult != EVRApplicationError.None)
        {
            _logger.LogWarning("Failed to register manifest: {0}", manifestRegisterResult);
            IsInitialized = false;
            return IsInitialized;
        }
        
        _logger.LogInformation("Successfully initialized OpenVR");
        
        IsInitialized = true;
        return IsInitialized;
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