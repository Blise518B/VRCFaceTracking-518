using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using VRCFaceTracking.Core.Contracts.Services;
using VRCFaceTracking.Core.Params.Data;
using VRCFaceTracking.Core.Params.Expressions;
using VRCFaceTracking.Core.Services;

namespace VRCFaceTracking.ViewModels;

/// <summary>
/// One live value on the Tracking Debug page: an eye signal or a unified
/// expression shape, with raw (module output) and mutated (after curves etc.)
/// values, where it comes from, and when it last moved.
/// </summary>
public partial class DebugRow : ObservableObject
{
    public required string Name { get; init; }

    /// <summary>Reads this row's value out of a tracking-data snapshot.</summary>
    public required Func<UnifiedTrackingData, float> Selector { get; init; }

    /// <summary>Value range for the bar (gaze is -1..1, weights 0..1, pupil in mm).</summary>
    public float BarMin { get; init; }
    public float BarMax { get; init; } = 1f;

    /// <summary>True for eye signals and the shapes that ride the eye slot.</summary>
    public bool IsEyeRange { get; init; }

    /// <summary>"gaze" | "lids" | "shapes" | "pupil" - which routing-file field labels this row.</summary>
    public required string RoutingKind { get; init; }

    [ObservableProperty] private float _raw;
    [ObservableProperty] private float _mutated;
    [ObservableProperty] private string _source = "-";
    [ObservableProperty] private string _updatedAgo = "never";
    [ObservableProperty] private bool _recentlyChanged;
    [ObservableProperty] private bool _visible = true;

    public long LastChangedTicks;

    public double BarNorm => Math.Clamp((Mutated - BarMin) / (BarMax - BarMin), 0d, 1d);
    public string RawText => Raw.ToString("F3");
    public string MutatedText => Mutated.ToString("F3");

    partial void OnRawChanged(float value) => OnPropertyChanged(nameof(RawText));

    partial void OnMutatedChanged(float value)
    {
        OnPropertyChanged(nameof(BarNorm));
        OnPropertyChanged(nameof(MutatedText));
    }
}

public partial class TrackingDebugViewModel : ObservableRecipient
{
    private static readonly int EyeShapeFirst = (int)UnifiedExpressions.EyeSquintRight;
    private static readonly int EyeShapeLast = (int)UnifiedExpressions.BrowOuterUpLeft;

    private readonly ILibManager _libManager;
    private readonly DispatcherQueue? _dispatcher;
    private DispatcherQueueTimer? _timer;

    private volatile UnifiedTrackingData? _latestMutated;

    private string? _routingFilePath;
    private long _nextRoutingProbe;
    private RoutingSnapshot? _routing;

    public ObservableCollection<DebugRow> EyeRows { get; } = new();
    public ObservableCollection<DebugRow> ShapeRows { get; } = new();

    [ObservableProperty] private string _filter = string.Empty;
    [ObservableProperty] private bool _onlyMoving;
    [ObservableProperty] private string _slotSummary = "-";
    [ObservableProperty] private string _routingSummary = "-";
    [ObservableProperty] private string _perfSummary = "-";

    // The big expression-source badge + switch. Mode is what's configured
    // (auto/etvr/babble); the badge shows what is ACTUALLY driving right now.
    [ObservableProperty] private bool _etvrActive;
    [ObservableProperty] private bool _babbleActive;
    [ObservableProperty] private bool _sourceUnknown = true;
    [ObservableProperty] private int _modeIndex; // 0 auto, 1 etvr, 2 babble
    [ObservableProperty] private bool _switchAvailable;
    private bool _suppressModeSend;
    private long _modeSendGraceUntil;

    // Panic switches, backed by the Neutral Override mutation. Raw data keeps
    // flowing; only the outgoing buffer is held at neutral, so untoggling is instant.
    [ObservableProperty] private bool _eyesNeutral;
    [ObservableProperty] private bool _faceNeutral;
    [ObservableProperty] private bool _idleEyesEnabled = true;
    private bool _suppressNeutralPush;
    private Core.Params.Data.Mutation.NeutralOverride? _neutralMutation;

    partial void OnEyesNeutralChanged(bool value) => PushNeutral(m => m.neutralEyes = value);
    partial void OnFaceNeutralChanged(bool value) => PushNeutral(m => m.neutralFace = value);
    partial void OnIdleEyesEnabledChanged(bool value) => PushNeutral(m => m.idleEyes = value);

    private void PushNeutral(Action<Core.Params.Data.Mutation.NeutralOverride> apply)
    {
        if (_suppressNeutralPush)
        {
            return;
        }

        var mutation = FindNeutralMutation();
        if (mutation == null)
        {
            return;
        }

        apply(mutation);
        mutation.IsActive = true;

        // A panic switch that silently does nothing because the mutation master
        // toggle happens to be off is worse than none - force the chain on.
        var mutator = App.GetService<UnifiedTrackingMutator>();
        if (!mutator.Enabled)
        {
            mutator.Enabled = true;
        }

        _ = mutation.Save();
    }

    private Core.Params.Data.Mutation.NeutralOverride? FindNeutralMutation()
    {
        if (_neutralMutation != null)
        {
            return _neutralMutation;
        }

        try
        {
            _neutralMutation = App.GetService<UnifiedTrackingMutator>()
                ._mutations.OfType<Core.Params.Data.Mutation.NeutralOverride>().FirstOrDefault();
        }
        catch (Exception)
        {
            // Mutations may not be published yet during startup; try again next tick
        }
        return _neutralMutation;
    }

    /// <summary>Reflect changes made on the Tracking Mutations page back into the toggles.</summary>
    private void SyncNeutralFromMutation()
    {
        var mutation = FindNeutralMutation();
        if (mutation == null)
        {
            return;
        }

        _suppressNeutralPush = true;
        try
        {
            if (EyesNeutral != mutation.neutralEyes)
            {
                EyesNeutral = mutation.neutralEyes;
            }
            if (FaceNeutral != mutation.neutralFace)
            {
                FaceNeutral = mutation.neutralFace;
            }
            if (IdleEyesEnabled != mutation.idleEyes)
            {
                IdleEyesEnabled = mutation.idleEyes;
            }
        }
        finally
        {
            _suppressNeutralPush = false;
        }
    }

    private sealed class RoutingSnapshot
    {
        public string Module { get; set; } = "";
        public string Gaze { get; set; } = "";
        public string Lids { get; set; } = "";
        public string Shapes { get; set; } = "";
        public string Pupil { get; set; } = "";
        public string Relay { get; set; } = "";
        public int TobiiFps { get; set; }
        public int EtvrRate { get; set; }
        public int BabbleRate { get; set; }
        public long TimestampMs { get; set; }
        public string Mode { get; set; } = "auto";
        public string ActiveSource { get; set; } = "";
    }

    public TrackingDebugViewModel()
    {
        _libManager = App.GetService<ILibManager>();
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        // The mutator publishes the post-mutation frame here on the tracking
        // thread; keep the latest reference and read it on the UI tick.
        UnifiedTracking.OnUnifiedDataUpdated += data => _latestMutated = data;

        BuildRows();
    }

    partial void OnFilterChanged(string value) => ApplyVisibility();
    partial void OnOnlyMovingChanged(bool value) => ApplyVisibility();

    partial void OnModeIndexChanged(int value)
    {
        if (_suppressModeSend)
        {
            return;
        }

        var mode = value switch { 1 => "etvr", 2 => "babble", _ => "auto" };
        // The routing file lags the command by up to ~500 ms; don't let a
        // stale read snap the selector back while the module catches up.
        _modeSendGraceUntil = Environment.TickCount64 + 2000;
        SendExpressionSourceCommand(mode);
    }

    /// <summary>
    /// Flips the module's ExpressionSource at runtime over its own OSC command
    /// channel (/command/set/ExpressionSource) - the same mechanism the ETVR
    /// GUI uses for its settings, so no restart is involved. The module
    /// persists the change and the routing file confirms it within ~500 ms.
    /// </summary>
    private void SendExpressionSourceCommand(string mode)
    {
        try
        {
            var port = ReadModulePort();
            using var socket = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Dgram,
                System.Net.Sockets.ProtocolType.Udp);
            socket.SendTo(
                EncodeOscString("/command/set/ExpressionSource", mode),
                new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, port));
        }
        catch (Exception)
        {
            // The routing file simply won't confirm; the badge stays truthful.
        }
    }

    private int ReadModulePort()
    {
        try
        {
            var dir = Path.GetDirectoryName(_routingFilePath);
            var configPath = dir is null ? null : Path.Combine(dir, "ETVRModuleConfig.json");
            if (configPath is not null && File.Exists(configPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
                if (doc.RootElement.TryGetProperty("PortNumber", out var port))
                {
                    return port.GetInt32();
                }
            }
        }
        catch (Exception)
        {
        }
        return 8889;
    }

    private static byte[] EncodeOscString(string address, string value)
    {
        static byte[] Pad(byte[] raw)
        {
            var padded = new byte[(raw.Length + 1 + 3) & ~3];
            Array.Copy(raw, padded, raw.Length);
            return padded;
        }

        var a = Pad(System.Text.Encoding.ASCII.GetBytes(address));
        var t = Pad(System.Text.Encoding.ASCII.GetBytes(",s"));
        var v = Pad(System.Text.Encoding.ASCII.GetBytes(value));
        return a.Concat(t).Concat(v).ToArray();
    }

    public void Start()
    {
        if (_dispatcher is null || _timer is not null)
        {
            return;
        }

        _timer = _dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(100);
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    public void Stop()
    {
        _timer?.Stop();
        _timer = null;
    }

    private void BuildRows()
    {
        EyeRows.Add(EyeRow("Gaze Left X", d => d.Eye.Left.Gaze.x, -1f, 1f, "gaze"));
        EyeRows.Add(EyeRow("Gaze Left Y", d => d.Eye.Left.Gaze.y, -1f, 1f, "gaze"));
        EyeRows.Add(EyeRow("Gaze Right X", d => d.Eye.Right.Gaze.x, -1f, 1f, "gaze"));
        EyeRows.Add(EyeRow("Gaze Right Y", d => d.Eye.Right.Gaze.y, -1f, 1f, "gaze"));
        EyeRows.Add(EyeRow("Openness Left", d => d.Eye.Left.Openness, 0f, 1f, "lids"));
        EyeRows.Add(EyeRow("Openness Right", d => d.Eye.Right.Openness, 0f, 1f, "lids"));
        EyeRows.Add(EyeRow("Pupil Left (mm)", d => d.Eye.Left.PupilDiameter_MM, 0f, 10f, "pupil"));
        EyeRows.Add(EyeRow("Pupil Right (mm)", d => d.Eye.Right.PupilDiameter_MM, 0f, 10f, "pupil"));

        for (var i = 0; i < (int)UnifiedExpressions.Max; i++)
        {
            var index = i;
            ShapeRows.Add(new DebugRow
            {
                Name = ((UnifiedExpressions)index).ToString(),
                Selector = d => d.Shapes[index].Weight,
                BarMin = 0f,
                BarMax = 1f,
                IsEyeRange = index >= EyeShapeFirst && index <= EyeShapeLast,
                RoutingKind = "shapes",
            });
        }
    }

    private static DebugRow EyeRow(string name, Func<UnifiedTrackingData, float> selector,
        float min, float max, string kind) =>
        new()
        {
            Name = name,
            Selector = selector,
            BarMin = min,
            BarMax = max,
            IsEyeRange = true,
            RoutingKind = kind,
        };

    private void Tick()
    {
        var raw = UnifiedTracking.Data;
        var mutated = _latestMutated;
        var now = Environment.TickCount64;

        RefreshRouting(now);
        RefreshSummaries();
        SyncNeutralFromMutation();

        var eyeOwner = _libManager.LoadedModulesMetadata.FirstOrDefault(m => m.UsingEye)?.Name;
        var exprOwner = _libManager.LoadedModulesMetadata.FirstOrDefault(m => m.UsingExpression)?.Name;

        foreach (var row in EyeRows)
        {
            UpdateRow(row, raw, mutated, now, eyeOwner, exprOwner);
        }
        foreach (var row in ShapeRows)
        {
            UpdateRow(row, raw, mutated, now, eyeOwner, exprOwner);
        }
    }

    private void UpdateRow(DebugRow row, UnifiedTrackingData raw, UnifiedTrackingData? mutated,
        long now, string? eyeOwner, string? exprOwner)
    {
        var newRaw = row.Selector(raw);
        if (Math.Abs(newRaw - row.Raw) > 0.0001f)
        {
            row.LastChangedTicks = now;
        }
        row.Raw = newRaw;
        row.Mutated = mutated is null ? newRaw : row.Selector(mutated);

        var age = row.LastChangedTicks == 0 ? long.MaxValue : now - row.LastChangedTicks;
        row.RecentlyChanged = age < 400;
        row.UpdatedAgo = age == long.MaxValue ? "never"
            : age < 1000 ? $"{age} ms"
            : age < 60_000 ? $"{age / 1000d:F1} s"
            : "> 1 min";

        row.Source = ResolveSource(row, eyeOwner, exprOwner);

        row.Visible = MatchesFilter(row) && (!OnlyMoving || age < 2000);
    }

    private string ResolveSource(DebugRow row, string? eyeOwner, string? exprOwner)
    {
        // The eye slot's fine-grained split (Tobii vs ETVR vs Babble) only the
        // DreamAir518 module knows; it publishes it via DreamAirRouting.json.
        if (row.IsEyeRange)
        {
            if (_routing is not null)
            {
                return row.RoutingKind switch
                {
                    "gaze" => _routing.Gaze,
                    "lids" => _routing.Lids,
                    "pupil" => _routing.Pupil,
                    _ => _routing.Shapes,
                };
            }
            return eyeOwner is null ? "no eye module" : $"{eyeOwner} (eye slot)";
        }

        return exprOwner is null ? "no expression module" : $"{exprOwner} (expression slot)";
    }

    private bool MatchesFilter(DebugRow row) =>
        string.IsNullOrWhiteSpace(Filter)
        || row.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase);

    private void ApplyVisibility()
    {
        var now = Environment.TickCount64;
        foreach (var row in EyeRows.Concat(ShapeRows))
        {
            var age = row.LastChangedTicks == 0 ? long.MaxValue : now - row.LastChangedTicks;
            row.Visible = MatchesFilter(row) && (!OnlyMoving || age < 2000);
        }
    }

    private void RefreshSummaries()
    {
        var eyeOwner = _libManager.LoadedModulesMetadata.FirstOrDefault(m => m.UsingEye)?.Name ?? "-";
        var exprOwner = _libManager.LoadedModulesMetadata.FirstOrDefault(m => m.UsingExpression)?.Name ?? "-";
        SlotSummary = $"Eye slot: {eyeOwner}     Expression slot: {exprOwner}";

        RoutingSummary = _routing is null
            ? "Module routing file not found or stale - showing slot owners only."
            : $"{_routing.Module}:  gaze = {_routing.Gaze}   |   lids = {_routing.Lids}   |   " +
              $"shapes = {_routing.Shapes} (ETVR {_routing.EtvrRate}/s, Babble {_routing.BabbleRate}/s)   |   " +
              $"pupil = {_routing.Pupil}   |   relay = {_routing.Relay}";

        // Live pipeline health from the always-on performance watch
        var moduleAge = PipelineWatch.ModuleDataAgeMs;
        PerfSummary =
            $"module data {(moduleAge < 0 ? "none yet" : $"{moduleAge:F0} ms ago")}   |   " +
            $"{PipelineWatch.LastSummary}";

        // Badge + switch state. The switch needs a module new enough to
        // publish Mode/ActiveSource; older files leave ActiveSource empty.
        var known = _routing is not null && _routing.ActiveSource.Length > 0;
        SourceUnknown = !known;
        EtvrActive = known && _routing!.ActiveSource == "etvr";
        BabbleActive = known && _routing!.ActiveSource == "babble";
        SwitchAvailable = known;

        if (known && Environment.TickCount64 >= _modeSendGraceUntil)
        {
            var fileMode = _routing!.Mode switch { "etvr" => 1, "babble" => 2, _ => 0 };
            if (fileMode != ModeIndex)
            {
                _suppressModeSend = true;
                ModeIndex = fileMode;
                _suppressModeSend = false;
            }
        }
    }

    private void RefreshRouting(long now)
    {
        if (now < _nextRoutingProbe)
        {
            return;
        }
        _nextRoutingProbe = now + 500;

        try
        {
            if (_routingFilePath is null || !File.Exists(_routingFilePath))
            {
                _routingFilePath = Directory
                    .EnumerateFiles(Core.Utils.CustomLibsDirectory, "DreamAirRouting.json", SearchOption.AllDirectories)
                    .FirstOrDefault();
            }

            if (_routingFilePath is null)
            {
                _routing = null;
                return;
            }

            var parsed = JsonSerializer.Deserialize<RoutingSnapshot>(File.ReadAllText(_routingFilePath));
            var fresh = parsed is not null
                        && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - parsed.TimestampMs < 3000;
            _routing = fresh ? parsed : null;
        }
        catch (Exception)
        {
            _routing = null;
        }
    }
}
