using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using VRCFaceTracking.Core.Params.Data;
using VRCFaceTracking.Core.Params.Data.Mutation;
using VRCFaceTracking.Core.Params.Data.Mutation.Curves;

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

    public void RefreshModifiedMarkers()
    {
        var mutation = Mutation;
        foreach (var item in AllChannels)
        {
            if (mutation != null &&
                mutation.Curves.TryGetValue(item.Key, out var curve) &&
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
