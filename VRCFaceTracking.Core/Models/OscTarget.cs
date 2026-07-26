using System.ComponentModel.DataAnnotations;
using CommunityToolkit.Mvvm.ComponentModel;
using VRCFaceTracking.Core.Contracts;
using VRCFaceTracking.Core.Contracts.Services;
using VRCFaceTracking.Core.Validation;

namespace VRCFaceTracking.Core.Models;

public partial class OscTarget : ObservableValidator, IOscTarget
{
    [ObservableProperty] private bool _isConnected;

    // The fields start at the real defaults instead of 0/null for two reasons: a save
    // that fires before settings finish loading persists sane values rather than zeros,
    // and loading a corrupted 0 is then an actual property CHANGE (0 != 9000), so the
    // coercion handlers below get to run. With a plain int field, saved-0 == initial-0
    // and the generated setter would skip both the event and the repair.
    [ObservableProperty]
    [Range(1, 25535)]
    [property: SavedSetting("OSCInPort", 9001)]
    private int _inPort = 9001;

    [ObservableProperty]
    [Range(1, 25535)]
    [property: SavedSetting("OSCOutPort", 9000)]
    private int _outPort = 9000;

    [ObservableProperty]
    [ValidIpAddress]
    [property: SavedSetting("OSCAddress", "127.0.0.1")]
    private string _destinationAddress = "127.0.0.1";

    public OscTarget(ILocalSettingsService localSettingsService)
    {
        PropertyChanged += (_, _) => localSettingsService.Save(this);
    }

    // A port of 0 can end up persisted if a save fires before settings finish loading
    // (seen after a crash loop: OSCOutPort=0 stuck in LocalSettings.json, and the send
    // socket then never connects - the app looks alive but VRChat receives nothing).
    // Coerce invalid values back to the VRChat defaults; the resulting change event
    // also rewrites the saved file, so the bad value heals itself.
    partial void OnInPortChanged(int value)
    {
        if (value <= 0 || value > 65535)
        {
            InPort = 9001;
        }
    }

    partial void OnOutPortChanged(int value)
    {
        if (value <= 0 || value > 65535)
        {
            OutPort = 9000;
        }
    }

    partial void OnDestinationAddressChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            DestinationAddress = "127.0.0.1";
        }
    }
}
