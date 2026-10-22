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

    public static void Record(OscMessage[] messages) => Record(messages, messages.Length);

    public static void Record(OscMessage[] messages, int count)
    {
        var now = Environment.TickCount64;
        AdvanceTotalWindow(now);

        for (var i = 0; i < count; i++)
        {
            var message = messages[i];
            var address = message.Address;
            if (string.IsNullOrEmpty(address))
            {
                continue;
            }

            // Scalars only, read in place. Going through message.Value marshalled the
            // whole native struct, string pointer included, and a message queued before
            // its first value was written made that pointer garbage: an access violation
            // that took the app down (2026-09-14). It also allocated an array per
            // message per tick while this page was open.
            var value = message.PeekScalar();

            Touch(address, value, value switch
            {
                float => "float",
                bool => "bool",
                int => "int",
                null => "?",
                _ => value.GetType().Name,
            }, now);
        }
    }

    /// <summary>
    /// A datagram encoded without fti_osc (the native face tracking blob). Shown by its size
    /// in bytes: the page lists single values, and the blob holds 102 of them.
    /// </summary>
    public static void RecordRaw(string address, int length)
    {
        var now = Environment.TickCount64;
        AdvanceTotalWindow(now);
        Touch(address, length, "blob", now);
    }

    private static void AdvanceTotalWindow(long now)
    {
        if (now - _totalWindowStart >= 1000)
        {
            _totalRate = _totalWindowCount;
            _totalWindowCount = 0;
            _totalWindowStart = now;
        }
    }

    private static void Touch(string address, object? value, string typeName, long now)
    {
        _totalWindowCount++;

        var entry = Entries.GetOrAdd(address, _ => new Entry { WindowStart = now });
        entry.LastValue = value;
        entry.TypeName = typeName;
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
