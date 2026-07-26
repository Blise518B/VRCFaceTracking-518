using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VRCFaceTracking.Lite;

/// <summary>
/// Console window presentation: our own icon on the window, and making sure the window is
/// one that can actually show it.
///
/// Windows Terminal (the Windows 11 default host) takes its tab and taskbar icons from its
/// own profile and ignores WM_SETICON entirely, so a branded console window is only possible
/// under the classic conhost host. When we detect we've been opened in Terminal we re-launch
/// ourselves once under conhost - guarded by a marker argument AND by clearing the inherited
/// WT_SESSION, so the child can never bounce again.
/// </summary>
internal static class ConsoleWindow
{
    public const string HostedMarker = "--hosted";

    private static nint _largeIcon;
    private static nint _smallIcon;

    /// <summary>
    /// True when we should hand ourselves a classic console window.
    ///
    /// Deliberately unconditional (bar the marker) rather than "detect Terminal and react":
    /// Windows' default-terminal setting hands a freshly created console off to Terminal
    /// ASYNCHRONOUSLY, so at startup we still see a normal visible conhost window and set
    /// our icon on it moments before it's replaced by a Terminal tab that ignores it. Every
    /// detection point races that handoff. Launching conhost.exe ourselves does not - an
    /// explicitly hosted console is never handed off, so the window (and its icon, and its
    /// native mouse input) is ours for the whole session.
    /// </summary>
    public static bool WantsOwnWindow(string[] args) => !args.Contains(HostedMarker);

    /// <summary>Short description of the console we ended up with, for the startup trace.</summary>
    public static string Describe()
    {
        try
        {
            var hwnd = GetConsoleWindow();
            return hwnd == 0
                ? "no console window"
                : $"hwnd 0x{hwnd:X} visible={IsWindowVisible(hwnd)}";
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    /// <summary>
    /// Starts a copy of ourselves in a classic console window. Returns true when the caller
    /// should exit and leave the work to that copy.
    /// </summary>
    public static bool TryRelaunchInOwnWindow(string[] args)
    {
        try
        {
            var exe = Environment.ProcessPath;
            var conhost = Path.Combine(Environment.SystemDirectory, "conhost.exe");
            if (exe == null || !File.Exists(conhost))
            {
                return false;
            }

            // conhost takes the command to host as its own arguments
            var forwarded = string.Join(' ', args.Append(HostedMarker).Select(Quote));
            var startInfo = new ProcessStartInfo(conhost)
            {
                Arguments = $"{Quote(exe)} {forwarded}",
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false,
            };
            // Belt and braces with HostedMarker: the child inherits our environment, and
            // a leftover WT_SESSION would make it think it's in Terminal all over again.
            startInfo.Environment.Remove("WT_SESSION");
            startInfo.Environment.Remove("WT_PROFILE_ID");

            return Process.Start(startInfo) != null;
        }
        catch (Exception)
        {
            return false; // Carry on in whatever window we already have
        }
    }

    /// <summary>
    /// Puts the exe's embedded icon on the console window. No-op under Terminal, which owns
    /// its own chrome - that's what TryRelaunchInOwnWindow is for.
    /// </summary>
    public static void SetIcon()
    {
        try
        {
            var hwnd = GetConsoleWindow();
            var exe = Environment.ProcessPath;
            if (hwnd == 0 || exe == null)
            {
                return;
            }

            // Held in statics for the process lifetime: the window keeps using these
            // handles, so they must not be destroyed while it's on screen.
            if (ExtractIconEx(exe, 0, out _largeIcon, out _smallIcon, 1) == 0)
            {
                return;
            }

            if (_largeIcon != 0)
            {
                SendMessage(hwnd, WM_SETICON, ICON_BIG, _largeIcon);
            }
            if (_smallIcon != 0)
            {
                SendMessage(hwnd, WM_SETICON, ICON_SMALL, _smallIcon);
            }
        }
        catch (Exception)
        {
            // Cosmetic only - never let it affect startup
        }
    }

    private static string Quote(string value) =>
        value.Contains(' ') ? $"\"{value}\"" : value;

    /// <summary>
    /// One line per startup step, appended before the logging stack exists. The lite build
    /// can die between process start and the first log write (wrong exe location, a console
    /// host that won't cooperate), and without this that failure leaves no trace at all.
    /// </summary>
    public static void Breadcrumb(string step)
    {
        try
        {
            var path = Path.Combine(Core.Utils.PersistentDataDirectory, "lite-startup.log");
            Directory.CreateDirectory(Core.Utils.PersistentDataDirectory);
            var line = $"{DateTime.Now:HH:mm:ss.fff}  pid {Environment.ProcessId}  {step}{Environment.NewLine}";
            File.AppendAllText(path, line);

            // Keep it from growing forever - it's a rolling startup trace, not history
            var info = new FileInfo(path);
            if (info.Length > 64 * 1024)
            {
                var tail = File.ReadAllLines(path).TakeLast(200).ToArray();
                File.WriteAllLines(path, tail);
            }
        }
        catch (Exception)
        {
        }
    }

    private const int WM_SETICON = 0x0080;
    private const nint ICON_SMALL = 0;
    private const nint ICON_BIG = 1;

    [DllImport("kernel32.dll")]
    private static extern nint GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessage(nint hWnd, int msg, nint wParam, nint lParam);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index,
        out nint largeIcon, out nint smallIcon, uint icons);
}
