// ui-nav-probe: times page switches in a RUNNING VRCFaceTracking from the outside.
//
// It never starts, stops or restarts the app and never moves the mouse. It:
//   * pings the main window with WM_NULL every ~15 ms (a ping only returns when the
//     UI thread pumps messages, so its latency is how long the UI thread was blocked),
//   * samples the UI thread's own CPU time and the process priority class with each ping,
//   * clicks nav items through UI Automation (Invoke), alternating a base page and each
//     target page, and times each switch until the UI thread goes quiet again.
//
// What the numbers mean for one switch:
//   ready ms  - click until the UI thread answered pings promptly again
//   ui cpu ms - CPU the UI thread itself burned in that span
//   wait ms   - ready minus ui cpu: time the UI thread was NOT running. Large wait with
//               small cpu = starved (low priority / busy cores) or blocked (GC, a lock,
//               sync I/O). Large cpu = the page itself does too much work.
//   prio      - priority class seen during the switch (BelowNormal = Process Lasso's
//               ProBalance restraint or similar)
//
// Usage: dotnet run -c Release -- [--rounds 5] [--base Sources]
//          [--pages "Module Registry,VRChat Output"] [--dwell 3000] [--out file.csv]

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

internal static class Program
{
    private static readonly Dictionary<string, string> Markers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Module Registry"] = "Install Module from .zip",
        ["VRChat Output"] = "What VRChat actually receives",
        ["Sources"] = "Tracking sources on this PC",
    };

    private static AutomationElement? _root;

    private const double QuietPingMs = 40;   // a ping under this = UI thread free
    private const int QuietPingsNeeded = 4;  // this many in a row = page settled

    private static int Main(string[] args)
    {
        var rounds = IntArg(args, "--rounds", 5);
        var dwell = IntArg(args, "--dwell", 3000);
        var basePage = StrArg(args, "--base", "Sources");
        var pages = StrArg(args, "--pages", "Module Registry,VRChat Output")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var outPath = StrArg(args, "--out", "");

        var proc = Process.GetProcessesByName("VRCFaceTracking").FirstOrDefault();
        if (proc is null)
        {
            Console.Error.WriteLine("VRCFaceTracking is not running. This probe only attaches to a running app.");
            return 2;
        }

        var hwnd = FindMainWindow(proc);
        if (hwnd == IntPtr.Zero)
        {
            Console.Error.WriteLine("Could not find the VRCFaceTracking main window (hidden to tray?). Show it and retry.");
            return 2;
        }

        var uiTid = Native.GetWindowThreadProcessId(hwnd, out _);
        Console.WriteLine($"VRCFaceTracking pid {proc.Id}, window 0x{hwnd:X}, UI thread {uiTid}, " +
                          $"minimized={Native.IsIconic(hwnd)}, foreground={Native.GetForegroundWindow() == hwnd}");
        if (Native.IsIconic(hwnd))
        {
            Console.WriteLine("Note: window is minimized - WinUI skips rendering, so times are lower than on screen.");
        }

        using var pinger = new Pinger(hwnd, (uint)proc.Id, uiTid);
        pinger.Start();

        var root = AutomationElement.FromHandle(hwnd);
        _root = root;
        var navItems = FindNavItems(root, pages.Append(basePage));
        foreach (var name in pages.Append(basePage).Where(n => !navItems.ContainsKey(n)))
        {
            Console.Error.WriteLine($"Nav item '{name}' not found. Found: {string.Join(", ", navItems.Keys)}");
            return 2;
        }

        var results = new List<SwitchResult>();
        Console.WriteLine();
        Console.WriteLine($"{"#",3} {"page",-16} {"ready",7} {"invoke",7} {"uiCpu",7} {"wait",7} {"maxBlk",7} {"prio",-12} {"dwell ui%",9} {"dwell maxBlk",12} marker");

        // Start from the base page so the first measured switch is comparable.
        Switch(pinger, navItems[basePage], basePage, 0, dwell: 500);

        var n = 0;
        for (var r = 0; r < rounds; r++)
        {
            foreach (var page in pages)
            {
                results.Add(Print(++n, Switch(pinger, navItems[page], page, r + 1, dwell)));
                results.Add(Print(++n, Switch(pinger, navItems[basePage], basePage, r + 1, dwell: 800)));
            }
        }

        Console.WriteLine();
        Console.WriteLine("Summary (ready ms: median / max, ui cpu ms median, wait ms median):");
        foreach (var g in results.GroupBy(x => x.Page))
        {
            var ready = g.Select(x => x.ReadyMs).Order().ToArray();
            var cpu = g.Select(x => x.UiCpuMs).Order().ToArray();
            var wait = g.Select(x => x.ReadyMs - x.UiCpuMs).Order().ToArray();
            var dwellUi = g.Select(x => x.DwellUiPercent).Order().ToArray();
            Console.WriteLine($"  {g.Key,-16} ready {Median(ready),6:F0} / {ready.Max(),6:F0}   ui cpu {Median(cpu),6:F0}   wait {Median(wait),6:F0}   on-page UI busy {Median(dwellUi),4:F0}%");
        }

        var byPrio = results.GroupBy(x => x.PriorityText).Select(g =>
            $"{g.Key}: {g.Count()} switches, median ready {Median(g.Select(x => x.ReadyMs).Order().ToArray()):F0} ms");
        Console.WriteLine("By priority: " + string.Join(" | ", byPrio));

        if (outPath.Length > 0)
        {
            var sb = new StringBuilder("n,round,page,ready_ms,invoke_ms,ui_cpu_ms,wait_ms,max_block_ms,priority,dwell_ui_pct,dwell_max_block_ms,marker\n");
            foreach (var x in results)
            {
                sb.AppendLine(string.Join(",", x.N, x.Round, $"\"{x.Page}\"", F(x.ReadyMs), F(x.InvokeMs), F(x.UiCpuMs),
                    F(x.ReadyMs - x.UiCpuMs), F(x.MaxBlockMs), x.PriorityText, F(x.DwellUiPercent), F(x.DwellMaxBlockMs), x.MarkerFound));
            }
            System.IO.File.WriteAllText(outPath, sb.ToString());
            Console.WriteLine($"Wrote {outPath}");
        }

        return 0;
    }

    private static SwitchResult Print(int n, SwitchResult r)
    {
        r.N = n;
        Console.WriteLine($"{n,3} {r.Page,-16} {r.ReadyMs,7:F0} {r.InvokeMs,7:F0} {r.UiCpuMs,7:F0} {r.ReadyMs - r.UiCpuMs,7:F0} {r.MaxBlockMs,7:F0} {r.PriorityText,-12} {r.DwellUiPercent,8:F0}% {r.DwellMaxBlockMs,12:F0} {r.MarkerFound}");
        return r;
    }

    private static SwitchResult Switch(Pinger pinger, AutomationElement item, string page, int round, int dwell)
    {
        var t0 = Stopwatch.GetTimestamp();
        var cpu0 = pinger.UiCpuMsNow();

        if (item.TryGetCurrentPattern(InvokePattern.Pattern, out var inv))
        {
            ((InvokePattern)inv).Invoke();
        }
        else
        {
            ((SelectionItemPattern)item.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        }

        var tInvoked = Stopwatch.GetTimestamp();

        // Wait until the UI thread answers QuietPingsNeeded pings in a row promptly.
        var deadline = t0 + Stopwatch.Frequency * 60;
        Ping[] after;
        while (true)
        {
            Thread.Sleep(20);
            after = pinger.Since(t0);
            var tail = after.TakeLast(QuietPingsNeeded).ToArray();
            if (tail.Length == QuietPingsNeeded && tail.All(p => p.LatencyMs < QuietPingMs) && tail[0].Start >= tInvoked)
            {
                break;
            }
            if (Stopwatch.GetTimestamp() > deadline)
            {
                break;
            }
        }

        // Ready = end of the last slow ping (or the end of Invoke if none was slow).
        var lastSlow = after.LastOrDefault(p => p.LatencyMs >= QuietPingMs);
        var readyTs = Math.Max(tInvoked, lastSlow.Start == 0 ? tInvoked : lastSlow.End);
        var cpuAtReady = pinger.UiCpuMsAt(readyTs);
        var span = after.Where(p => p.Start <= readyTs).ToArray();
        var prios = span.Select(p => p.Priority).Append(pinger.LastPriority).Distinct().ToArray();

        var result = new SwitchResult
        {
            Round = round,
            Page = page,
            ReadyMs = Ms(readyTs - t0),
            InvokeMs = Ms(tInvoked - t0),
            UiCpuMs = Math.Max(0, cpuAtReady - cpu0),
            MaxBlockMs = span.Length == 0 ? 0 : span.Max(p => p.LatencyMs),
            PriorityText = string.Join("+", prios.Select(Native.PriorityName)),
        };

        // Confirm the page actually changed (one UIA lookup, after the timing window).
        result.MarkerFound = Markers.TryGetValue(page, out var marker) ? FindMarker(marker) : "-";

        // Sit on the page: how busy does it keep the UI thread?
        var d0 = Stopwatch.GetTimestamp();
        var dCpu0 = pinger.UiCpuMsNow();
        Thread.Sleep(dwell);
        var d1 = Stopwatch.GetTimestamp();
        var dPings = pinger.Since(d0).Where(p => p.End <= d1).ToArray();
        result.DwellUiPercent = 100 * (pinger.UiCpuMsNow() - dCpu0) / Ms(d1 - d0);
        result.DwellMaxBlockMs = dPings.Length == 0 ? 0 : dPings.Max(p => p.LatencyMs);
        return result;
    }

    private static string FindMarker(string marker)
    {
        try
        {
            var hit = _root!.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.NameProperty, marker));
            return hit is null ? "MISSING" : "ok";
        }
        catch (Exception e)
        {
            return "err:" + e.GetType().Name;
        }
    }

    private static Dictionary<string, AutomationElement> FindNavItems(AutomationElement root, IEnumerable<string> names)
    {
        var wanted = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        var found = new Dictionary<string, AutomationElement>(StringComparer.OrdinalIgnoreCase);
        var items = root.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
        foreach (AutomationElement it in items)
        {
            var name = it.Current.Name;
            if (wanted.Contains(name) && !found.ContainsKey(name))
            {
                found[name] = it;
            }
        }
        return found;
    }

    private static IntPtr FindMainWindow(Process proc)
    {
        if (proc.MainWindowHandle != IntPtr.Zero)
        {
            return proc.MainWindowHandle;
        }

        var hit = IntPtr.Zero;
        Native.EnumWindows((h, _) =>
        {
            Native.GetWindowThreadProcessId(h, out var pid);
            if (pid != proc.Id)
            {
                return true;
            }
            var cls = new StringBuilder(256);
            Native.GetClassName(h, cls, cls.Capacity);
            if (cls.ToString() == "WinUIDesktopWin32WindowClass")
            {
                hit = h;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return hit;
    }

    private static double Median(double[] sorted) => sorted.Length == 0 ? 0 : sorted[sorted.Length / 2];
    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
    private static string F(double v) => v.ToString("F1", CultureInfo.InvariantCulture);

    private static int IntArg(string[] a, string name, int def) =>
        Array.IndexOf(a, name) is var i and >= 0 && i + 1 < a.Length && int.TryParse(a[i + 1], out var v) ? v : def;

    private static string StrArg(string[] a, string name, string def) =>
        Array.IndexOf(a, name) is var i and >= 0 && i + 1 < a.Length ? a[i + 1] : def;

    private sealed class SwitchResult
    {
        public int N;
        public int Round;
        public string Page = "";
        public double ReadyMs, InvokeMs, UiCpuMs, MaxBlockMs, DwellUiPercent, DwellMaxBlockMs;
        public string PriorityText = "";
        public string MarkerFound = "";
    }
}

internal readonly record struct Ping(long Start, long End, double LatencyMs, double UiCpuMs, uint Priority);

/// <summary>WM_NULL pinger on its own thread; also samples UI-thread CPU and priority class.</summary>
internal sealed class Pinger : IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly IntPtr _hProc;
    private readonly IntPtr _hThread;
    private readonly List<Ping> _pings = new();
    private readonly Thread _thread;
    private volatile bool _stop;

    public uint LastPriority { get; private set; }

    public Pinger(IntPtr hwnd, uint pid, uint uiTid)
    {
        _hwnd = hwnd;
        _hProc = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        _hThread = Native.OpenThread(Native.THREAD_QUERY_LIMITED_INFORMATION, false, uiTid);
        if (_hProc == IntPtr.Zero || _hThread == IntPtr.Zero)
        {
            throw new InvalidOperationException("Cannot open the VRCFaceTracking process/UI thread for querying.");
        }
        LastPriority = Native.GetPriorityClass(_hProc);
        _thread = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "pinger" };
    }

    public void Start() => _thread.Start();

    private void Run()
    {
        while (!_stop)
        {
            var s = Stopwatch.GetTimestamp();
            Native.SendMessageTimeout(_hwnd, Native.WM_NULL, IntPtr.Zero, IntPtr.Zero, Native.SMTO_NORMAL, 60_000, out _);
            var e = Stopwatch.GetTimestamp();
            var prio = Native.GetPriorityClass(_hProc);
            LastPriority = prio;
            var ping = new Ping(s, e, (e - s) * 1000.0 / Stopwatch.Frequency, UiCpuMsNow(), prio);
            lock (_pings)
            {
                _pings.Add(ping);
            }
            Thread.Sleep(15);
        }
    }

    public double UiCpuMsNow()
    {
        Native.GetThreadTimes(_hThread, out _, out _, out var k, out var u);
        return (k + u) / 10_000.0;
    }

    /// <summary>UI CPU at a past timestamp, from the nearest ping that ended at or before it.</summary>
    public double UiCpuMsAt(long ts)
    {
        lock (_pings)
        {
            for (var i = _pings.Count - 1; i >= 0; i--)
            {
                if (_pings[i].End <= ts)
                {
                    return _pings[i].UiCpuMs;
                }
            }
        }
        return UiCpuMsNow();
    }

    public Ping[] Since(long ts)
    {
        lock (_pings)
        {
            return _pings.Where(p => p.Start >= ts).ToArray();
        }
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(2000);
        Native.CloseHandle(_hThread);
        Native.CloseHandle(_hProc);
    }
}

internal static class Native
{
    public const uint WM_NULL = 0;
    public const uint SMTO_NORMAL = 0;
    public const uint THREAD_QUERY_LIMITED_INFORMATION = 0x0800;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] public static extern IntPtr OpenThread(uint access, bool inherit, uint tid);
    [DllImport("kernel32.dll")] public static extern bool GetThreadTimes(IntPtr h, out long create, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll")] public static extern uint GetPriorityClass(IntPtr h);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);

    public static string PriorityName(uint p) => p switch
    {
        0x40 => "Idle",
        0x4000 => "BelowNormal",
        0x20 => "Normal",
        0x8000 => "AboveNormal",
        0x80 => "High",
        0x100 => "Realtime",
        _ => $"0x{p:X}",
    };
}
