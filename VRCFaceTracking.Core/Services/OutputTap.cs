using System.Collections.Concurrent;
using VRCFaceTracking.Core.OSC;

namespace VRCFaceTracking.Core.Services;

/// <summary>
/// Records every OSC message at the last point before it leaves for VRChat,
/// so the VRChat Output page can show ground truth instead of an upstream
/// buffer that later filtering might disagree with. A message appears here if
/// and only if it went onto the wire this session.
///
/// Static and gated: OscSendService calls Record() only while a viewer has
/// set Enabled, so the marshalling cost exists only while the page is open.
/// </summary>
public static class OutputTap
{
    public sealed class Entry
    {
        public object? LastValue;
        public string TypeName = "";
        public long LastSentTicks;
        public long WindowStart;
        public int WindowCount;
        public int Rate;
        public long TotalCount;
    }

    private static readonly ConcurrentDictionary<string, Entry> Entries = new();

    private static long _totalWindowStart;
    private static int _totalWindowCount;
    private static int _totalRate;

    public static volatile bool Enabled;

    public static void Record(OscMessage[] messages)
    {
        var now = Environment.TickCount64;

        if (now - _totalWindowStart >= 1000)
        {
            _totalRate = _totalWindowCount;
            _totalWindowCount = 0;
            _totalWindowStart = now;
        }

        foreach (var message in messages)
        {
            var address = message.Address;
            if (string.IsNullOrEmpty(address))
            {
                continue;
            }

            _totalWindowCount++;

            var entry = Entries.GetOrAdd(address, _ => new Entry { WindowStart = now });

            object? value;
            try
            {
                // Marshals from the message's native allocation - valid here,
                // because the batch is alive until the send completes.
                value = message.Value;
            }
            catch (Exception)
            {
                value = null;
            }

            entry.LastValue = value;
            entry.TypeName = value switch
            {
                float => "float",
                bool => "bool",
                int => "int",
                null => "?",
                _ => value.GetType().Name,
            };
            entry.LastSentTicks = now;
            entry.TotalCount++;
            if (now - entry.WindowStart >= 1000)
            {
                entry.Rate = entry.WindowCount;
                entry.WindowCount = 0;
                entry.WindowStart = now;
            }
            entry.WindowCount++;
        }
    }

    /// <summary>Messages per second across all addresses (completed 1s window).</summary>
    public static int TotalRate =>
        Environment.TickCount64 - _totalWindowStart <= 2000 ? _totalRate : 0;

    public static int AddressCount => Entries.Count;

    public static IReadOnlyList<KeyValuePair<string, Entry>> Snapshot() => Entries.ToArray();

    public static void Clear()
    {
        Entries.Clear();
        _totalRate = 0;
        _totalWindowCount = 0;
    }
}
