using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;

namespace VRCFaceTracking.Services;

public struct LogLine
{
    public string Message;
    public LogLevel Level;

    public LogLine(string message, LogLevel level)
    {
        Message = message;
        Level = level;
    }

    public override string ToString() => Message;
}    

public class OutputPageLogger : ILogger
{
    // Both buffers used to grow for the whole session. On 2026-09-14 that was 45,000
    // lines after seven hours, 5,163 of them rendered on the Output page - and with them
    // the XAML objects that made every garbage collection take 90 ms to 1.4 s, which froze
    // tracking. The page only ever shows the tail, and latest.log keeps everything.
    public const int MaxFilteredLines = 2_000;
    public const int MaxAllLines = 10_000;

    private readonly string _categoryName;
    public static readonly ObservableCollection<LogLine> FilteredLogs = new();
    public static readonly ObservableCollection<LogLine> AllLogs = new();
    private static DispatcherQueue? _dispatcher;

    public OutputPageLogger(string categoryName, DispatcherQueue? queue)
    {
        _categoryName = categoryName;
        _dispatcher = queue;
    }

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => default!;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var dispatcher = _dispatcher;
        if ( dispatcher == null )
        {
            return;
        }

        // Format once, here. The UI-thread lambda used to format every line twice and kept
        // state and exception alive until the dispatcher got round to it.
        string message;
        try
        {
            message = formatter(state, exception);
        }
        catch ( Exception e )
        {
            message = $"(log message could not be formatted: {e.Message})";
        }

        // Log events from sub-processes have the unique category name "\0VRCFT\0"
        var line = _categoryName == "\0VRCFT\0"
            ? new LogLine(message, logLevel)
            : new LogLine($"[{_categoryName}] {logLevel}: {message}", logLevel);

        // Add to the static logs from the dispatcher thread
        dispatcher.TryEnqueue(() =>
        {
            Append(AllLogs, line, MaxAllLines);
            // Filtered is what the user sees, so show Information scope
            if ( logLevel >= LogLevel.Information )
            {
                Append(FilteredLogs, line, MaxFilteredLines);
            }
        });
    }

    private static void Append(ObservableCollection<LogLine> lines, LogLine line, int max)
    {
        lines.Add(line);
        while ( lines.Count > max )
        {
            lines.RemoveAt(0);
        }
    }
}