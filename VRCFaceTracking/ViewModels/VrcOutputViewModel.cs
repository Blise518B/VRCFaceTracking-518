using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using VRCFaceTracking.Core.Contracts;
using VRCFaceTracking.Core.Services;

namespace VRCFaceTracking.ViewModels;

/// <summary>One OSC address that has actually been sent to VRChat this session.</summary>
public partial class OutputRow : ObservableObject
{
    public required string Address { get; init; }

    [ObservableProperty] private string _valueText = "";
    [ObservableProperty] private string _typeName = "";
    [ObservableProperty] private double _barNorm;
    [ObservableProperty] private bool _isFloat;
    [ObservableProperty] private int _rate;
    [ObservableProperty] private string _sentAgo = "";
    [ObservableProperty] private bool _recentlySent;
    [ObservableProperty] private bool _visible = true;

    public long LastChangeTicks;
    public string? LastValueText;
}

/// <summary>
/// Ground truth for "what does VRChat actually receive": every row comes from
/// the tap inside OscSendService, recorded at the moment a message goes onto
/// the wire. If a value looks right on Tracking Debug but is missing here,
/// the avatar's config never requested that parameter (or it left re-encoded
/// as a different address, e.g. binary bits or a combined lid).
/// </summary>
public partial class VrcOutputViewModel : ObservableRecipient
{
    private readonly IOscTarget _oscTarget;
    private readonly OscQueryService _oscQuery;
    private readonly DispatcherQueue? _dispatcher;
    private DispatcherQueueTimer? _timer;

    public ObservableCollection<OutputRow> Rows { get; } = new();
    private readonly Dictionary<string, OutputRow> _byAddress = new(StringComparer.Ordinal);

    [ObservableProperty] private string _filter = string.Empty;
    [ObservableProperty] private bool _onlyMoving;
    [ObservableProperty] private string _headerLine = "-";
    [ObservableProperty] private string _avatarLine = "-";

    public VrcOutputViewModel()
    {
        _oscTarget = App.GetService<IOscTarget>();
        _oscQuery = App.GetService<OscQueryService>();
        _dispatcher = DispatcherQueue.GetForCurrentThread();
    }

    partial void OnFilterChanged(string value) => ApplyVisibility();
    partial void OnOnlyMovingChanged(bool value) => ApplyVisibility();

    public void Start()
    {
        OutputTap.Enabled = true;
        if (_dispatcher is null || _timer is not null)
        {
            return;
        }

        _timer = _dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(200);
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    public void Stop()
    {
        OutputTap.Enabled = false;
        _timer?.Stop();
        _timer = null;
    }

    public void ClearSeen()
    {
        OutputTap.Clear();
        Rows.Clear();
        _byAddress.Clear();
    }

    private void Tick()
    {
        var now = Environment.TickCount64;

        var avatar = _oscQuery.AvatarInfo;
        AvatarLine = $"Avatar: {avatar?.Name ?? "?"}   ({avatar?.Id ?? "?"})";
        var target = $"{_oscTarget.DestinationAddress}:{_oscTarget.OutPort}";
        var conn = _oscTarget.IsConnected ? "connected" : "NOT CONNECTED - nothing reaches VRChat";
        HeaderLine = $"Sending to {target} ({conn})   |   {OutputTap.AddressCount} addresses seen   |   {OutputTap.TotalRate} msg/s total";

        foreach (var (address, entry) in OutputTap.Snapshot())
        {
            if (!_byAddress.TryGetValue(address, out var row))
            {
                row = new OutputRow { Address = address };
                _byAddress[address] = row;
                InsertSorted(row);
            }

            var valueText = entry.LastValue switch
            {
                float f => f.ToString("F3"),
                bool b => b ? "true" : "false",
                null => "-",
                var v => v.ToString() ?? "-",
            };

            if (valueText != row.LastValueText)
            {
                row.LastValueText = valueText;
                row.LastChangeTicks = now;
            }

            row.ValueText = valueText;
            row.TypeName = entry.TypeName;
            row.IsFloat = entry.LastValue is float;
            if (entry.LastValue is float fv)
            {
                // Bipolar signals (gaze) fold into the same 0..1 bar; the
                // number next to it stays authoritative.
                row.BarNorm = Math.Clamp(fv < 0 ? -fv : fv, 0d, 1d);
            }
            row.Rate = Math.Min(entry.Rate, 999);

            var age = now - entry.LastSentTicks;
            row.SentAgo = age < 1000 ? "live"
                : age < 60_000 ? $"{age / 1000d:F0} s ago"
                : "> 1 min ago";
            row.RecentlySent = now - row.LastChangeTicks < 400;
        }

        ApplyVisibility();
    }

    private void InsertSorted(OutputRow row)
    {
        var i = 0;
        while (i < Rows.Count && string.CompareOrdinal(Rows[i].Address, row.Address) < 0)
        {
            i++;
        }
        Rows.Insert(i, row);
    }

    private void ApplyVisibility()
    {
        var now = Environment.TickCount64;
        foreach (var row in Rows)
        {
            var matches = string.IsNullOrWhiteSpace(Filter)
                          || row.Address.Contains(Filter, StringComparison.OrdinalIgnoreCase);
            var moving = !OnlyMoving || now - row.LastChangeTicks < 2000;
            row.Visible = matches && moving;
        }
    }
}
