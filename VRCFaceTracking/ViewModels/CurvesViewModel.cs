using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using VRCFaceTracking.Core.Params.Data;
using VRCFaceTracking.Core.Params.Data.Mutation;
using VRCFaceTracking.Core.Params.Data.Mutation.Curves;
using VRCFaceTracking.Core.Services;

namespace VRCFaceTracking.ViewModels;

/// <summary>
/// One selectable tracking channel in the curve editor's channel list.
/// </summary>
public partial class CurveChannelItem : ObservableObject
{
    public CurveChannelItem(CurveChannel channel)
    {
        Channel = channel;
    }

    public CurveChannel Channel { get; }

    public string Key => Channel.Key;
    public string Name => Channel.Name;
    public string Category => Channel.Category;

    /// <summary> Whether an editor for this channel is currently open on the page. </summary>
    [ObservableProperty] private bool _isOpen;

    /// <summary> Shown as a marker in the list when the channel's curve is doing something. </summary>
    [ObservableProperty] private bool _isModified;
}

public partial class CurvesViewModel : ObservableRecipient
{
    private readonly UnifiedTrackingMutator _mutator;

    public ObservableCollection<CurveChannelItem> AllChannels { get; } = new();
    public ObservableCollection<CurveChannelItem> FilteredChannels { get; } = new();

    public CurvesViewModel()
    {
        _mutator = App.GetService<UnifiedTrackingMutator>();

        foreach (var channel in CurveChannels.All)
        {
            AllChannels.Add(new CurveChannelItem(channel));
        }

        Filter(string.Empty);
        RefreshModifiedMarkers();
    }

    private CurveCalibration? _mutation;

    /// <summary>
    /// The live mutation instance. Looked up lazily because the mutation list is filled on a
    /// background thread during startup; enumeration is guarded because that list can still be
    /// growing while this page is open.
    /// </summary>
    public CurveCalibration? Mutation
    {
        get
        {
            if (_mutation != null)
            {
                return _mutation;
            }

            try
            {
                _mutation = _mutator._mutations.OfType<CurveCalibration>().FirstOrDefault();
            }
            catch (InvalidOperationException)
            {
                // Collection was still being populated - try again next time
                return null;
            }

            return _mutation;
        }
    }

    /// <summary>
    /// Master switch. Curves only reach VRChat when both the mutation and the mutator itself
    /// are enabled, so this drives both.
    /// </summary>
    public bool CurvesEnabled
    {
        get
        {
            var mutation = Mutation;
            return mutation != null && mutation.IsActive && _mutator.Enabled;
        }
        set
        {
            var mutation = Mutation;
            if (mutation == null)
            {
                return;
            }

            if (value)
            {
                _mutator.Enabled = true;
            }
            mutation.IsActive = value;
            OnPropertyChanged();
        }
    }

    public void Filter(string query)
    {
        FilteredChannels.Clear();

        var trimmed = query?.Trim() ?? string.Empty;
        foreach (var item in AllChannels)
        {
            if (trimmed.Length == 0 ||
                item.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
                item.Category.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
            {
                FilteredChannels.Add(item);
            }
        }
    }

    public ResponseCurve? GetCurve(string channelKey) => Mutation?.GetOrCreateCurve(channelKey);

    public ObservableCollection<string> Profiles { get; } = new();

    /// <summary>
    /// Follow the detected source, so swapping headsets swaps curve sets automatically.
    /// </summary>
    public bool AutoSwitchBySource
    {
        get => Mutation?.AutoSwitchBySource ?? true;
        set
        {
            if (Mutation != null)
            {
                Mutation.AutoSwitchBySource = value;
                OnPropertyChanged();
            }
        }
    }

    [ObservableProperty] private string _profileStatus = string.Empty;

    public void RefreshProfiles()
    {
        var mutation = Mutation;
        Profiles.Clear();
        if (mutation == null)
        {
            return;
        }

        foreach (var name in mutation.ProfileNames)
        {
            Profiles.Add(name);
        }

        var effective = mutation.EffectiveProfileName;
        var edited = mutation.Profiles.TryGetValue(effective, out var p) ? p?.Curves?.Count ?? 0 : 0;
        ProfileStatus = mutation.AutoSwitchBySource
            ? $"editing \"{effective}\" - picked automatically from the active source - {edited} channel(s) shaped"
            : $"editing \"{effective}\" - {edited} channel(s) shaped";
    }

    /// <summary>Switches profile and returns true if the open editors need rebuilding.</summary>
    public bool SelectProfile(string? name)
    {
        var mutation = Mutation;
        if (mutation == null || string.IsNullOrWhiteSpace(name) || name == mutation.EffectiveProfileName)
        {
            return false;
        }

        mutation.SetActiveProfile(name);
        RefreshProfiles();
        RefreshModifiedMarkers();
        return true;
    }

    public string AddProfileFromActiveSource()
    {
        var mutation = Mutation;
        if (mutation == null)
        {
            return string.Empty;
        }

        // Name it after whatever is streaming, so auto-switch will pick it up later
        var suggested = _detectorName() ?? $"Profile {mutation.Profiles.Count + 1}";
        var name = suggested;
        var suffix = 2;
        while (mutation.Profiles.ContainsKey(name))
        {
            name = $"{suggested} {suffix++}";
        }

        mutation.DuplicateProfile(mutation.EffectiveProfileName, name);
        mutation.SetActiveProfile(name);
        RefreshProfiles();
        return name;
    }

    public bool DeleteActiveProfile()
    {
        var mutation = Mutation;
        if (mutation == null)
        {
            return false;
        }

        var removed = mutation.DeleteProfile(mutation.EffectiveProfileName);
        RefreshProfiles();
        RefreshModifiedMarkers();
        return removed;
    }

    private static string? _detectorName()
    {
        try
        {
            return App.GetService<TrackingSourceDetector>().Scan().FirstOrDefault(s => s.Strong)?.Name;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void RefreshModifiedMarkers()
    {
        var mutation = Mutation;

        // Markers reflect the profile currently in effect
        Dictionary<string, ResponseCurve>? curves = null;
        if (mutation != null)
        {
            curves = mutation.GetOrCreateProfile(mutation.EffectiveProfileName).Curves;
        }

        foreach (var item in AllChannels)
        {
            if (curves != null &&
                curves.TryGetValue(item.Key, out var curve) &&
                curve != null)
            {
                item.IsModified = curve.Enabled && !curve.IsIdentity;
            }
            else
            {
                item.IsModified = false;
            }
        }
    }

    /// <summary>
    /// Persists curves plus the master enable state. Saves through the mutator so
    /// <c>MutatorEnabled</c> is written too, not just this mutation's own settings.
    /// </summary>
    public async Task SaveAsync()
    {
        if (Mutation == null)
        {
            return;
        }

        await _mutator.Save();
    }
}
