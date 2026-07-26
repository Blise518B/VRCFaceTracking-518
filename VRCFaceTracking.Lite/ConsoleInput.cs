using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace VRCFaceTracking.Lite;

/// <summary>
/// Raw Win32 console input: mouse clicks and key presses via ReadConsoleInput on a
/// background thread. This is the whole "TUI framework" the monitor needs - the
/// renderer already knows where every button lives, so all we want from the OS is
/// "left button went down at cell (x, y)". Zero dependencies, works in both the
/// classic console host and Windows Terminal. Mouse coordinates arrive in buffer
/// cells, the same space Draw's SetCursorPosition uses, so hit-testing is a plain
/// row/column compare.
/// </summary>
internal sealed class ConsoleInput : IDisposable
{
    /// <summary>A click at a cell, or a key press - exactly one of the two.</summary>
    public readonly record struct Event(bool IsClick, short X, short Y, ConsoleKey Key);

    private readonly ConcurrentQueue<Event> _events = new();
    private readonly nint _handle;
    private readonly uint _originalMode;
    private volatile bool _stopped;

    public bool Active { get; }

    public ConsoleInput()
    {
        try
        {
            _handle = GetStdHandle(STD_INPUT_HANDLE);
            if (_handle == -1 || _handle == 0 || !GetConsoleMode(_handle, out _originalMode))
            {
                return; // Redirected or absent stdin: stay inert, caller falls back
            }

            // Quick-edit mode eats mouse events for its select-to-copy feature, and
            // clearing it only takes effect alongside ENABLE_EXTENDED_FLAGS.
            var mode = (_originalMode | ENABLE_MOUSE_INPUT | ENABLE_EXTENDED_FLAGS)
                       & ~ENABLE_QUICK_EDIT_MODE;
            if (!SetConsoleMode(_handle, mode))
            {
                return;
            }

            Active = true;
            new Thread(ReadLoop) { IsBackground = true, Name = "Console-Input" }.Start();
        }
        catch (Exception)
        {
            Active = false;
        }
    }

    public bool TryDequeue(out Event e) => _events.TryDequeue(out e);

    private void ReadLoop()
    {
        var records = new INPUT_RECORD[16];
        while (!_stopped)
        {
            try
            {
                if (!ReadConsoleInput(_handle, records, (uint)records.Length, out var read) || read == 0)
                {
                    return;
                }

                for (var i = 0; i < read && i < records.Length; i++)
                {
                    var rec = records[i];
                    if (rec.EventType == MOUSE_EVENT
                        && rec.MouseEvent.dwEventFlags == 0                       // a button change, not move/wheel
                        && (rec.MouseEvent.dwButtonState & LEFT_BUTTON) != 0)     // left button now down
                    {
                        _events.Enqueue(new Event(true,
                            rec.MouseEvent.dwMousePosition.X, rec.MouseEvent.dwMousePosition.Y, default));
                    }
                    else if (rec.EventType == KEY_EVENT && rec.KeyEvent.bKeyDown != 0)
                    {
                        _events.Enqueue(new Event(false, 0, 0, (ConsoleKey)rec.KeyEvent.wVirtualKeyCode));
                    }
                }
            }
            catch (Exception)
            {
                return; // Console gone; the app is on its way out anyway
            }
        }
    }

    public void Dispose()
    {
        _stopped = true;
        try
        {
            if (Active)
            {
                SetConsoleMode(_handle, _originalMode);
            }
        }
        catch (Exception)
        {
        }
        // ReadConsoleInput blocks, but the reader is a background thread - it dies
        // with the process instead of holding shutdown hostage.
    }

    private const int STD_INPUT_HANDLE = -10;
    private const uint ENABLE_MOUSE_INPUT = 0x0010;
    private const uint ENABLE_QUICK_EDIT_MODE = 0x0040;
    private const uint ENABLE_EXTENDED_FLAGS = 0x0080;
    private const ushort KEY_EVENT = 1;
    private const ushort MOUSE_EVENT = 2;
    private const uint LEFT_BUTTON = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSE_EVENT_RECORD
    {
        public COORD dwMousePosition;
        public uint dwButtonState;
        public uint dwControlKeyState;
        public uint dwEventFlags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct KEY_EVENT_RECORD
    {
        public int bKeyDown;
        public ushort wRepeatCount;
        public ushort wVirtualKeyCode;
        public ushort wVirtualScanCode;
        public char UnicodeChar;
        public uint dwControlKeyState;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT_RECORD
    {
        [FieldOffset(0)] public ushort EventType;
        [FieldOffset(4)] public KEY_EVENT_RECORD KeyEvent;
        [FieldOffset(4)] public MOUSE_EVENT_RECORD MouseEvent;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(nint hConsoleInput, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(nint hConsoleInput, uint dwMode);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "ReadConsoleInputW")]
    private static extern bool ReadConsoleInput(nint hConsoleInput, [Out] INPUT_RECORD[] lpBuffer,
        uint nLength, out uint lpNumberOfEventsRead);
}
