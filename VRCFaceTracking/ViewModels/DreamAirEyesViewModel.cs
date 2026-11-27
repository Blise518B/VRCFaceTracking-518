using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using VRCFaceTracking.Core.Contracts.Services;
using VRCFaceTracking.Core.Library;
using VRCFaceTracking.Core.Services;
using VRCFaceTracking.Services;

namespace VRCFaceTracking.ViewModels;

/// <summary>One of the four big LIDS / WIDE / SQUINT / BROWS chips: what drives it right now.</summary>
public partial class MixChip : ObservableObject
{
    public required string Signal { get; init; }   // "lids" | "wide" | "squint" | "brows"
    public required string Label { get; init; }    // "LIDS"

    [ObservableProperty] private string _sourceText = "-";
    [ObservableProperty] private string _note = string.Empty;
    [ObservableProperty] private bool _isFallback;
    [ObservableProperty] private Brush? _foreground;
    [ObservableProperty] private Brush? _background;
    [ObservableProperty] private Brush? _edge;

    public Visibility NoteVisibility => string.IsNullOrEmpty(Note) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility FallbackVisibility => IsFallback ? Visibility.Visible : Visibility.Collapsed;

    partial void OnNoteChanged(string value) => OnPropertyChanged(nameof(NoteVisibility));
    partial void OnIsFallbackChanged(bool value) => OnPropertyChanged(nameof(FallbackVisibility));
}

/// <summary>A one-click variation button.</summary>
public partial class PresetButton : ObservableObject
{
    private readonly DreamAirEyesViewModel _owner;

    public PresetButton(DreamAirEyesViewModel owner, EyeMixPreset preset)
    {
        _owner = owner;
        Preset = preset;
    }

    public EyeMixPreset Preset { get; }
    public string Key => Preset.Key;
    public string Title => Preset.Title;

    public string Detail => Preset.Key == "E"
        ? "old single switch, follows whichever app streams"
        : $"lids {DreamAirModuleLink.SourceLabel(Preset.Lids)} · wide {DreamAirModuleLink.SourceLabel(Preset.Wide)} · " +
          $"squint {DreamAirModuleLink.SourceLabel(Preset.Squint)} · brows {DreamAirModuleLink.SourceLabel(Preset.Brows)}";

    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private Brush? _background;
    [ObservableProperty] private Brush? _edge;
    [ObservableProperty] private Brush? _foreground;

    public void Apply() => _owner.ApplyPreset(this);
}

/// <summary>
/// The Dream Air Eyes page: which eye-mix variation drives the avatar right now, one-click
/// variations, per-signal sources, the Tobii bridge (SRanibro or BrokenEye) and its watchdog,
/// and a guard against the Babble module holding the eye slot.
/// </summary>
public partial class DreamAirEyesViewModel : ObservableRecipient
{
    private static readonly string[] LidsValues = { "auto", "sranibro", "babble", "etvr", "tobii" };
    private static readonly string[] ShapeValues = { "auto", "sranibro", "babble", "etvr", "off" };

    private readonly ILibManager _libManager;
    private readonly CompanionAppService _companions;
    private readonly DispatcherQueue? _dispatcher;
    private DispatcherQueueTimer? _timer;

    private DreamAirRouting? _routing;
    private long _sendGraceUntil;
    private bool _suppressSend;
    private bool _selectorsSynced;
    private long _nextSlowRefresh;
    private bool _slowRefreshRunning;

    public ObservableCollection<MixChip> Chips { get; } = new();
    public ObservableCollection<PresetButton> Presets { get; } = new();

    // Banner
    [ObservableProperty] private string _variationTitle = "NOW: -";
    [ObservableProperty] private string _variationDetail = string.Empty;
    [ObservableProperty] private bool _moduleRunning;
    [ObservableProperty] private string _moduleStateText = "Looking for the DreamAir eye module...";
    [ObservableProperty] private bool _mixSupported = true;

    // Per-signal selectors (indexes into LidsValues / ShapeValues)
    [ObservableProperty] private int _lidsIndex;
    [ObservableProperty] private int _wideIndex;
    [ObservableProperty] private int _squintIndex;
    [ObservableProperty] private int _browsIndex;

    // Status
    [ObservableProperty] private string _bridgeText = "-";
    [ObservableProperty] private string _sourcesText = "-";
    [ObservableProperty] private string _capabilityText = string.Empty;
    [ObservableProperty] private string _slotText = "-";
    [ObservableProperty] private string _watchdogText = "-";
    [ObservableProperty] private string _cameraUrlsText = string.Empty;

    // Eye-slot guard
    [ObservableProperty] private bool _babbleHoldsEye;
    [ObservableProperty] private bool _babbleEyeRisk;
    [ObservableProperty] private string _guardText = string.Empty;
    [ObservableProperty] private string _guardResult = string.Empty;
    [ObservableProperty] private bool _guardBusy;
    private string? _babbleConfigPath;

    // Tobii bridge choice: 0 SRanibro, 1 BrokenEye. _knownBridge = the service's current
    // choice, null until the first background refresh has read it.
    [ObservableProperty] private int _bridgeIndex = -1;
    private TobiiBridgeKind? _knownBridge;
    [ObservableProperty] private string _bridgeActionText = string.Empty;
    [ObservableProperty] private bool _bridgeBusy;

    public Visibility GuardVisibility => BabbleHoldsEye || BabbleEyeRisk ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CapabilityVisibility => string.IsNullOrEmpty(CapabilityText) ? Visibility.Collapsed : Visibility.Visible;

    partial void OnBabbleHoldsEyeChanged(bool value) => OnPropertyChanged(nameof(GuardVisibility));
    partial void OnBabbleEyeRiskChanged(bool value) => OnPropertyChanged(nameof(GuardVisibility));
    partial void OnCapabilityTextChanged(string value) => OnPropertyChanged(nameof(CapabilityVisibility));

    public DreamAirEyesViewModel()
    {
        _libManager = App.GetService<ILibManager>();
        _companions = App.GetService<CompanionAppService>();
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        Chips.Add(new MixChip { Signal = "lids", Label = "LIDS" });
        Chips.Add(new MixChip { Signal = "wide", Label = "WIDE" });
        Chips.Add(new MixChip { Signal = "squint", Label = "SQUINT" });
        Chips.Add(new MixChip { Signal = "brows", Label = "BROWS" });

        foreach (var preset in DreamAirModuleLink.Presets)
        {
            Presets.Add(new PresetButton(this, preset));
        }

        // Paint the initial (unknown) state so nothing renders without brushes
        foreach (var chip in Chips)
        {
            PaintChip(chip, "none");
        }
        foreach (var preset in Presets)
        {
            PaintPreset(preset, false);
        }
    }

    public void Start()
    {
        if (_dispatcher is null || _timer is not null)
        {
            return;
        }

        _timer = _dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(250);
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Tick();
    }

    public void Stop()
    {
        _timer?.Stop();
        _timer = null;
    }

    // ------------------------------------------------------------------ sending

    public void ApplyPreset(PresetButton button)
    {
        _sendGraceUntil = Environment.TickCount64 + 2000;
        var sent = DreamAirModuleLink.SendPreset(button.Preset);

        // Show the choice at once; the routing file confirms within ~500 ms
        foreach (var preset in Presets)
        {
            PaintPreset(preset, ReferenceEquals(preset, button));
        }
        _suppressSend = true;
        try
        {
            LidsIndex = IndexOf(LidsValues, button.Preset.Lids);
            WideIndex = IndexOf(ShapeValues, button.Preset.Wide);
            SquintIndex = IndexOf(ShapeValues, button.Preset.Squint);
            BrowsIndex = IndexOf(ShapeValues, button.Preset.Brows);
        }
        finally
        {
            _suppressSend = false;
        }

        VariationTitle = $"NOW: {button.Preset.Name}";
        if (!sent)
        {
            VariationDetail = "Could not reach the DreamAir module's command port.";
        }
    }

    partial void OnLidsIndexChanged(int value) => SendSignal("MixLids", LidsValues, value);
    partial void OnWideIndexChanged(int value) => SendSignal("MixWide", ShapeValues, value);
    partial void OnSquintIndexChanged(int value) => SendSignal("MixSquint", ShapeValues, value);
    partial void OnBrowsIndexChanged(int value) => SendSignal("MixBrows", ShapeValues, value);

    private void SendSignal(string field, string[] values, int index)
    {
        // Only real clicks on a selector that already shows the module's state: nothing is
        // sent before the first sync, so a control settling while the page loads can't
        // overwrite the module's saved mix.
        if (_suppressSend || !_selectorsSynced || index < 0 || index >= values.Length)
        {
            return;
        }

        // The routing file lags the command by up to ~500 ms; don't let a stale read snap
        // the selector back while the module catches up.
        _sendGraceUntil = Environment.TickCount64 + 2000;
        DreamAirModuleLink.SendSet(field, values[index]);
    }

    private static int IndexOf(string[] values, string value)
    {
        var index = Array.FindIndex(values, v => v.Equals(value, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? 0 : index;
    }

    // ------------------------------------------------------------------ refresh

    private void Tick()
    {
        _routing = DreamAirModuleLink.ReadRouting();
        RefreshBanner();
        RefreshSelectors();
        RefreshSlots();

        var now = Environment.TickCount64;
        if (now >= _nextSlowRefresh && !_slowRefreshRunning)
        {
            _nextSlowRefresh = now + 2000;
            StartSlowRefresh();
        }
    }

    private void RefreshBanner()
    {
        var routing = _routing;
        ModuleRunning = routing is not null;

        if (routing is null)
        {
            MixSupported = true;
            VariationTitle = "NOW: DreamAir eye module not running";
            VariationDetail = "No fresh DreamAirRouting.json - the module is not loaded or not getting data. " +
                              "It starts once a Tobii bridge (SRanibro or BrokenEye) streams; check the eye-slot owner below.";
            ModuleStateText = "module not running";
            foreach (var chip in Chips)
            {
                chip.SourceText = "-";
                chip.Note = string.Empty;
                chip.IsFallback = false;
                PaintChip(chip, "none");
            }
            foreach (var button in Presets)
            {
                PaintPreset(button, false);
            }
            return;
        }

        ModuleStateText = $"{routing.Module} running";
        MixSupported = routing.HasMix;

        if (!routing.HasMix)
        {
            VariationTitle = $"NOW: single switch - {DreamAirModuleLink.SourceLabel(routing.ActiveSource)}";
            VariationDetail = "This DreamAir module build has no per-signal eye mix yet. Deploy the new module build to use the variations.";
            foreach (var chip in Chips)
            {
                var src = chip.Signal == "lids" && routing.Lids.Contains("Tobii openness") ? "tobii" : routing.ActiveSource;
                chip.SourceText = DreamAirModuleLink.SourceLabel(src);
                chip.Note = string.Empty;
                chip.IsFallback = false;
                PaintChip(chip, src);
            }
            return;
        }

        var preset = DreamAirModuleLink.MatchPreset(routing);
        var name = preset?.Name ?? "Custom mix";
        var fallbacks = routing.Fallback.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (Environment.TickCount64 >= _sendGraceUntil)
        {
            VariationTitle = $"NOW: {name}";
            foreach (var button in Presets)
            {
                PaintPreset(button, ReferenceEquals(button.Preset, preset));
            }
        }

        VariationDetail = fallbacks.Length == 0
            ? $"Configured: lids {Label(routing.MixLids)} · wide {Label(routing.MixWide)} · squint {Label(routing.MixSquint)} · brows {Label(routing.MixBrows)}"
            : $"Partly falling back ({string.Join(", ", fallbacks)}): the chosen app is not streaming - see the red chips.";

        UpdateChip(Chips[0], routing.MixLids, routing.ActiveLids, routing);
        UpdateChip(Chips[1], routing.MixWide, routing.ActiveWide, routing);
        UpdateChip(Chips[2], routing.MixSquint, routing.ActiveSquint, routing);
        UpdateChip(Chips[3], routing.MixBrows, routing.ActiveBrows, routing);
    }

    private static string Label(string source) => DreamAirModuleLink.SourceLabel(source);

    private void UpdateChip(MixChip chip, string configured, string active, DreamAirRouting routing)
    {
        var fallback = routing.IsFallback(chip.Signal);
        chip.IsFallback = fallback;
        chip.SourceText = Label(active);

        if (fallback)
        {
            chip.Note = $"{Label(configured)} not streaming -> fallback";
        }
        else if (configured.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            chip.Note = "auto";
        }
        else
        {
            chip.Note = string.Empty;
        }

        PaintChip(chip, active);
        if (fallback)
        {
            chip.Edge = Res("Cat518Red");
        }
    }

    private void RefreshSelectors()
    {
        var routing = _routing;
        if (routing is null || !routing.HasMix || Environment.TickCount64 < _sendGraceUntil)
        {
            return;
        }

        _suppressSend = true;
        try
        {
            var lids = IndexOf(LidsValues, routing.MixLids);
            var wide = IndexOf(ShapeValues, routing.MixWide);
            var squint = IndexOf(ShapeValues, routing.MixSquint);
            var brows = IndexOf(ShapeValues, routing.MixBrows);
            if (LidsIndex != lids) LidsIndex = lids;
            if (WideIndex != wide) WideIndex = wide;
            if (SquintIndex != squint) SquintIndex = squint;
            if (BrowsIndex != brows) BrowsIndex = brows;
        }
        finally
        {
            _suppressSend = false;
        }
        _selectorsSynced = true;
    }

    private void RefreshSlots()
    {
        ModuleMetadataInternal? eyeOwner = null;
        ModuleMetadataInternal? exprOwner = null;
        try
        {
            eyeOwner = _libManager.LoadedModulesMetadata.FirstOrDefault(m => m.UsingEye);
            exprOwner = _libManager.LoadedModulesMetadata.FirstOrDefault(m => m.UsingExpression);
        }
        catch (Exception)
        {
            // Collection changing during a re-init - next tick
        }

        SlotText = $"Eye slot: {eyeOwner?.Name ?? "-"}     Expression slot: {exprOwner?.Name ?? "-"}";
        BabbleHoldsEye = eyeOwner is not null && IsBabble(eyeOwner) && DreamAirModuleLink.ModuleInstalled();

        var routing = _routing;
        if (routing is null)
        {
            SourcesText = "-";
            return;
        }

        var bridge = routing.Bridge.Length == 0 ? "unknown" : DreamAirModuleLink.SourceLabel(routing.Bridge);
        var sranibro = routing.SranibroLive ? "streaming" : "silent";
        var babble = routing.BabbleLive || routing.BabbleRate > 0 ? $"{routing.BabbleRate}/s" : "silent";
        var etvr = routing.EtvrLive || routing.EtvrRate > 0 ? $"{routing.EtvrRate}/s" : "silent";
        SourcesText = $"Tobii via {bridge}: {(routing.BridgeLive ? $"{routing.TobiiFps} fps" : "no data")}   |   " +
                      $"SRanibro eye model {sranibro}   |   Babble {babble}   |   ETVR {etvr}   |   gaze = {routing.Gaze}";

        var notes = new List<string>();
        if (routing.Bridge.Equals("sranibro", StringComparison.OrdinalIgnoreCase) || routing.SranibroLive)
        {
            if (!routing.SranibroHasSquint)
            {
                notes.Add("SRanibro sends no squint on the Dream Air (XR5 model) - take squint from Babble or ETVR.");
            }
            if (!routing.SranibroHasWide)
            {
                notes.Add("No SRanibro wide-eyes seen yet this session.");
            }
            if (!routing.SranibroHasBrows)
            {
                notes.Add("No SRanibro eyebrows seen yet (needs its eyebrow model).");
            }
        }
        CapabilityText = string.Join("  ", notes);
    }

    /// <summary>The Babble/Baballonia face module - not our DreamAir module, whose name says Babble too.</summary>
    private static bool IsBabble(ModuleMetadataInternal module)
    {
        if (DreamAirModuleLink.IsDreamAirModule(module.Name) || DreamAirModuleLink.IsDreamAirModule(module.SourceDll))
        {
            return false;
        }

        return DreamAirModuleLink.IsBabbleModule(module.Name)
               || (module.SourceDll?.Contains("Baballonia", StringComparison.OrdinalIgnoreCase) ?? false);
    }

    /// <summary>Bridge, watchdog and Babble config: process enumeration and file reads, off the UI thread.</summary>
    private void StartSlowRefresh()
    {
        _slowRefreshRunning = true;
        Task.Run(() =>
        {
            try
            {
                var bridge = _companions.GetTobiiBridge();
                var sranibroRunning = CompanionAppService.IsBridgeProcessRunning(TobiiBridgeKind.Sranibro);
                var brokenEyeRunning = CompanionAppService.IsBridgeProcessRunning(TobiiBridgeKind.BrokenEye);
                var watch = _companions.BridgeWatchStatus;
                var (configPath, eyeClaim) = ReadBabbleEyeClaim();

                _dispatcher?.TryEnqueue(() =>
                {
                    if (!BridgeBusy)
                    {
                        _knownBridge = bridge;
                        _suppressSend = true;
                        try
                        {
                            var index = bridge == TobiiBridgeKind.Sranibro ? 0 : 1;
                            if (BridgeIndex != index)
                            {
                                BridgeIndex = index;
                            }
                        }
                        finally
                        {
                            _suppressSend = false;
                        }
                    }

                    var selected = CompanionAppService.BridgeSourceName(bridge);
                    var running = bridge == TobiiBridgeKind.Sranibro ? sranibroRunning : brokenEyeRunning;
                    var otherRunning = bridge == TobiiBridgeKind.Sranibro ? brokenEyeRunning : sranibroRunning;
                    BridgeText = $"Selected bridge: {selected} - {(running ? "running" : "not running")}" +
                                 (otherRunning ? $"   |   WARNING: {(bridge == TobiiBridgeKind.Sranibro ? "BrokenEye" : "SRanibro")} is running too and holds the tracker" : string.Empty);
                    WatchdogText = $"Watchdog: {watch}";
                    CameraUrlsText = bridge == TobiiBridgeKind.Sranibro
                        ? "Camera apps read: http://127.0.0.1:5556/left.mjpg and /right.mjpg (SRanibro eye image output on)"
                        : "Camera apps read: http://127.0.0.1:5555/eye/left and /eye/right (BrokenEye)";

                    _babbleConfigPath = configPath;
                    BabbleEyeRisk = eyeClaim && !BabbleHoldsEye && DreamAirModuleLink.ModuleInstalled();
                    GuardText = BabbleHoldsEye
                        ? "Babble is holding the eye slot - your Dream Air mix never reaches the avatar."
                        : BabbleEyeRisk
                            ? "The Babble module is set to claim eyes too (BabbleConfig IsEyeSupported = true). " +
                              "If it starts before the DreamAir module it takes the eye slot."
                            : string.Empty;
                });
            }
            catch (Exception)
            {
                // Next round
            }
            finally
            {
                _slowRefreshRunning = false;
            }
        });
    }

    private static (string? Path, bool ClaimsEyes) ReadBabbleEyeClaim()
    {
        try
        {
            if (!Directory.Exists(Core.Utils.CustomLibsDirectory))
            {
                return (null, false);
            }

            var path = Directory
                .EnumerateFiles(Core.Utils.CustomLibsDirectory, "BabbleConfig.json", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (path is null)
            {
                return (null, false);
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var claims = doc.RootElement.TryGetProperty("IsEyeSupported", out var eye)
                         && eye.ValueKind == JsonValueKind.True;
            return (path, claims);
        }
        catch (Exception)
        {
            return (null, false);
        }
    }

    // ------------------------------------------------------------------ actions

    /// <summary>
    /// Sets the Babble module to face-only (IsEyeSupported=false, IsFaceSupported=true, every
    /// other setting kept, one-time backup) and re-initializes tracking so the DreamAir module
    /// can take the eye slot.
    /// </summary>
    public void MakeBabbleFaceOnly()
    {
        if (GuardBusy)
        {
            return;
        }

        GuardBusy = true;
        GuardResult = "Updating the Babble module...";
        var path = _babbleConfigPath;

        Task.Run(() =>
        {
            string result;
            var ok = false;
            try
            {
                path ??= ReadBabbleEyeClaim().Path;
                if (path is null)
                {
                    result = "No BabbleConfig.json found in CustomLibs.";
                }
                else
                {
                    var backup = path + ".bak-eyemix";
                    if (!File.Exists(backup))
                    {
                        File.Copy(path, backup);
                    }

                    var node = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
                    node["IsEyeSupported"] = false;
                    node["IsFaceSupported"] = true;
                    File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    ok = true;
                    result = "Babble module set to face-only (backup: BabbleConfig.json.bak-eyemix). Restarting tracking...";
                }
            }
            catch (Exception e)
            {
                result = $"Could not update the Babble module: {e.Message}";
            }

            _dispatcher?.TryEnqueue(() =>
            {
                GuardResult = result;
                if (ok)
                {
                    RestartTracking();
                }
                GuardBusy = false;
            });
        });
    }

    /// <summary>Same as the Sources page's Restart tracking: tear down and re-run the start decisions.</summary>
    private void RestartTracking()
    {
        _dispatcher?.TryEnqueue(() =>
        {
            try
            {
                _libManager.TeardownAllAndResetAsync();
                _libManager.Initialize();
                GuardResult = "Babble module set to face-only; tracking restarted.";
            }
            catch (Exception e)
            {
                GuardResult = $"Restart failed: {e.Message}";
            }
        });
    }

    partial void OnBridgeIndexChanged(int value)
    {
        // A switch ends the other bridge's process, so it must only ever come from a real
        // click: ignore everything until the current choice is known, any out-of-range
        // index a control may push while it loads, and "changes" to the current choice.
        if (_suppressSend || _knownBridge is null || value is < 0 or > 1)
        {
            return;
        }

        var kind = value == 0 ? TobiiBridgeKind.Sranibro : TobiiBridgeKind.BrokenEye;
        if (kind == _knownBridge)
        {
            return;
        }

        _knownBridge = kind;
        BridgeBusy = true;
        BridgeActionText = $"Switching to {CompanionAppService.BridgeSourceName(kind)}...";
        Task.Run(() =>
        {
            var (_, message) = _companions.SwitchBridge(kind);
            _dispatcher?.TryEnqueue(() =>
            {
                BridgeActionText = message;
                BridgeBusy = false;
                _nextSlowRefresh = 0;
            });
        });
    }

    public void RestartBridge()
    {
        if (BridgeBusy)
        {
            return;
        }

        BridgeBusy = true;
        BridgeActionText = "Restarting the eye bridge...";
        Task.Run(() =>
        {
            var (_, message) = _companions.RestartBridge();
            _dispatcher?.TryEnqueue(() =>
            {
                BridgeActionText = message;
                BridgeBusy = false;
                _nextSlowRefresh = 0;
            });
        });
    }

    // ------------------------------------------------------------------ colours

    private static Brush? Res(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) ? value as Brush : null;

    /// <summary>Per-source identity hue: SRanibro purple, Babble yellow, ETVR cyan, Tobii blue.</summary>
    private static (string Fg, string Bg, string Edge) Hue(string source) => source.ToLowerInvariant() switch
    {
        "sranibro" => ("Cat518Purple", "Cat518PurpleTint", "Cat518PurpleEdge"),
        "babble" => ("Cat518Yellow", "Cat518YellowTint", "Cat518YellowEdge"),
        "etvr" => ("Cat518Cyan", "Cat518CyanTint", "Cat518CyanEdge"),
        "tobii" => ("Cat518Blue", "Cat518BlueTint", "Cat518BlueEdge"),
        _ => ("Neon518TextDimBrush", "Neon518ChipBrush", "Neon518TextDimBrush"),
    };

    private static void PaintChip(MixChip chip, string source)
    {
        var (fg, bg, edge) = Hue(source);
        chip.Foreground = Res(fg);
        chip.Background = Res(bg);
        chip.Edge = Res(edge);
    }

    private static void PaintPreset(PresetButton button, bool active)
    {
        button.IsActive = active;
        button.Foreground = active ? Res("Cat518Green") : Res("Neon518TextDimBrush");
        button.Background = active ? Res("Cat518GreenTint") : Res("Neon518CardBrush");
        button.Edge = active ? Res("Cat518Green") : Res("Cat518GreenEdge");
    }
}
