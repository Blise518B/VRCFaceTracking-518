using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace VRCFaceTracking.Services;

/// <summary>
/// What the DreamAir518 eye module publishes about itself in <c>DreamAirRouting.json</c>,
/// next to its DLL in CustomLibs (rewritten 4x/s). The module runs sandboxed in its own
/// process, so this file is the app's only window into its internal routing.
/// </summary>
public sealed class DreamAirRouting
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

    // Eye mix (per-signal sources). Older modules leave these empty.
    public string Bridge { get; set; } = "";
    public bool BridgeLive { get; set; }
    public string MixLids { get; set; } = "";
    public string MixWide { get; set; } = "";
    public string MixSquint { get; set; } = "";
    public string MixBrows { get; set; } = "";
    public string MixName { get; set; } = "";
    public string ActiveLids { get; set; } = "";
    public string ActiveWide { get; set; } = "";
    public string ActiveSquint { get; set; } = "";
    public string ActiveBrows { get; set; } = "";
    public string Fallback { get; set; } = "";
    public bool SranibroLive { get; set; }
    public bool BabbleLive { get; set; }
    public bool EtvrLive { get; set; }
    public bool SranibroHasWide { get; set; }
    public bool SranibroHasSquint { get; set; }
    public bool SranibroHasBrows { get; set; }

    /// <summary>True when the module is new enough to publish the eye mix.</summary>
    public bool HasMix => MixLids.Length > 0 || ActiveLids.Length > 0;

    public bool IsFallback(string signal) =>
        Fallback.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(s => s.Equals(signal, StringComparison.OrdinalIgnoreCase));
}

/// <summary>One named eye-mix variation the user can pick with one click.</summary>
public sealed record EyeMixPreset(string Key, string Title, string Lids, string Wide, string Squint, string Brows)
{
    /// <summary>The label sent to the module as MixName (ASCII, no ';' or '=').</summary>
    public string Name => $"{Key} - {Title}";

    public bool Matches(string lids, string wide, string squint, string brows) =>
        Same(Lids, lids) && Same(Wide, wide) && Same(Squint, squint) && Same(Brows, brows);

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Talks to the DreamAir518 eye module: reads its routing file, sends it runtime commands
/// over its own OSC command channel (/command/set/...), the same mechanism ETVR's GUI uses
/// for settings - instant, restart-free, persisted by the module itself.
/// </summary>
public static class DreamAirModuleLink
{
    public static readonly IReadOnlyList<EyeMixPreset> Presets = new[]
    {
        new EyeMixPreset("A", "SRanibro eyes + Babble squint", "sranibro", "sranibro", "babble", "sranibro"),
        new EyeMixPreset("B", "SRanibro eyes + ETVR squint", "sranibro", "sranibro", "etvr", "sranibro"),
        new EyeMixPreset("C", "Babble only", "babble", "babble", "babble", "babble"),
        new EyeMixPreset("D", "ETVR only", "etvr", "etvr", "etvr", "etvr"),
        new EyeMixPreset("E", "Legacy auto", "auto", "auto", "auto", "auto"),
    };

    private static readonly object Gate = new();
    private static string? _routingPath;
    private static long _nextPathSearch;

    /// <summary>
    /// The latest routing snapshot, or null when the file is missing or older than 3 s
    /// (module not running). Cheap: one small file read; the CustomLibs search for the file
    /// only repeats every 2 s while it is missing.
    /// </summary>
    public static DreamAirRouting? ReadRouting()
    {
        var path = FindRoutingFile();
        if (path is null)
        {
            return null;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<DreamAirRouting>(File.ReadAllText(path));
            var fresh = parsed is not null
                        && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - parsed.TimestampMs < 3000;
            return fresh ? parsed : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The module's folder in CustomLibs, if it has run here at least once.</summary>
    public static string? ModuleFolder
    {
        get
        {
            var path = FindRoutingFile();
            return path is null ? null : Path.GetDirectoryName(path);
        }
    }

    private static bool _installedCached;
    private static long _nextInstalledCheck;

    /// <summary>
    /// True when the DreamAir518 module is installed: it has written its routing file at least
    /// once. Safe to call at UI rate - the folder search repeats at most every 5 s.
    /// </summary>
    public static bool ModuleInstalled()
    {
        if (FindRoutingFile() != null)
        {
            return true;
        }

        lock (Gate)
        {
            var now = Environment.TickCount64;
            if (now < _nextInstalledCheck)
            {
                return _installedCached;
            }
            _nextInstalledCheck = now + 5000;

            try
            {
                _installedCached = Directory.Exists(Core.Utils.CustomLibsDirectory)
                                   && Directory.EnumerateFiles(Core.Utils.CustomLibsDirectory, "DreamAirRouting.json",
                                       SearchOption.AllDirectories).Any();
            }
            catch (Exception)
            {
                _installedCached = false;
            }
            return _installedCached;
        }
    }

    private static string? FindRoutingFile()
    {
        lock (Gate)
        {
            if (_routingPath != null && File.Exists(_routingPath))
            {
                return _routingPath;
            }

            var now = Environment.TickCount64;
            if (now < _nextPathSearch)
            {
                return null;
            }
            _nextPathSearch = now + 2000;

            try
            {
                _routingPath = Directory.Exists(Core.Utils.CustomLibsDirectory)
                    ? Directory.EnumerateFiles(Core.Utils.CustomLibsDirectory, "DreamAirRouting.json",
                        SearchOption.AllDirectories).FirstOrDefault()
                    : null;
            }
            catch (Exception)
            {
                _routingPath = null;
            }
            return _routingPath;
        }
    }

    /// <summary>The module's OSC listen port from its config (default 8889).</summary>
    public static int ReadModulePort()
    {
        try
        {
            var dir = ModuleFolder;
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

    /// <summary>Sends <c>/command/set/{field} "value"</c> to the module. Fire and forget: the
    /// routing file confirms within ~500 ms.</summary>
    public static bool SendSet(string field, string value)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.SendTo(EncodeOscString($"/command/set/{field}", value),
                new IPEndPoint(IPAddress.Loopback, ReadModulePort()));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Applies a whole preset in one atomic command. Legacy (all auto) also puts the old
    /// single expression switch back on Auto, in the same command.
    /// </summary>
    public static bool SendPreset(EyeMixPreset preset)
    {
        var expression = preset.Key == "E" ? "expression=auto;" : string.Empty;
        return SendSet("Mix",
            $"lids={preset.Lids};wide={preset.Wide};squint={preset.Squint};brows={preset.Brows};{expression}name={preset.Name}");
    }

    /// <summary>
    /// Is this loaded module the Babble/Baballonia face module? Careful: our own eye module's
    /// display name ("DreamAir518 Eye Mix (Tobii + SRanibro/ETVR/Babble)") mentions Babble too.
    /// </summary>
    public static bool IsBabbleModule(string? name) =>
        !string.IsNullOrEmpty(name)
        && !IsDreamAirModule(name)
        && (name.Contains("Baballonia", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Babble", StringComparison.OrdinalIgnoreCase));

    /// <summary>Is this loaded module the DreamAir518 eye module?</summary>
    public static bool IsDreamAirModule(string? name) =>
        !string.IsNullOrEmpty(name)
        && (name.Contains("DreamAir", StringComparison.OrdinalIgnoreCase)
            || name.Contains("ETVRTrackingModule", StringComparison.OrdinalIgnoreCase));

    public static byte[] EncodeOscString(string address, string value)
    {
        static byte[] Pad(byte[] raw)
        {
            var padded = new byte[(raw.Length + 1 + 3) & ~3];
            Array.Copy(raw, padded, raw.Length);
            return padded;
        }

        var a = Pad(Encoding.ASCII.GetBytes(address));
        var t = Pad(Encoding.ASCII.GetBytes(",s"));
        var v = Pad(Encoding.ASCII.GetBytes(value));
        return a.Concat(t).Concat(v).ToArray();
    }

    /// <summary>The preset whose four choices equal the configured mix, if any.</summary>
    public static EyeMixPreset? MatchPreset(DreamAirRouting routing) =>
        Presets.FirstOrDefault(p => p.Matches(routing.MixLids, routing.MixWide, routing.MixSquint, routing.MixBrows));

    /// <summary>"A - SRanibro eyes + Babble squint", or "Custom mix" when no preset matches.</summary>
    public static string VariationName(DreamAirRouting routing)
    {
        var preset = MatchPreset(routing);
        return preset?.Name ?? "Custom mix";
    }

    /// <summary>Display name of a source id from the routing file.</summary>
    public static string SourceLabel(string source) => source.ToLowerInvariant() switch
    {
        "sranibro" => "SRanibro",
        "babble" => "Babble",
        "etvr" => "ETVR",
        "tobii" => "Tobii",
        "auto" => "Auto",
        "off" => "Off",
        "none" or "" => "none",
        _ => source,
    };
}
