using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VRCFaceTracking.Core;
using VRCFaceTracking.Core.Contracts.Services;
using VRCFaceTracking.Core.Services;

namespace VRCFaceTracking.Lite;

/// <summary>
/// Stable per-machine id for the module registry. The WinUI build derives one from Windows
/// app identity, which isn't available here, so this hashes the machine and user name.
/// </summary>
public class LiteIdentityService : IIdentityService
{
    public string GetUniqueUserId()
    {
        try
        {
            var seed = $"{Environment.MachineName}|{Environment.UserName}";
            var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed));
            return Convert.ToHexString(bytes)[..32];
        }
        catch (Exception)
        {
            return "lite-unknown";
        }
    }
}

/// <summary>
/// Writes to the same latest.log the full app uses, so crash reports embed a real log tail.
/// Mirrors the WinUI build's provider, which lives in that project and isn't reachable here.
/// </summary>
public class LiteLogFileProvider : ILoggerProvider
{
    private readonly StreamWriter? _writer;
    private readonly ConcurrentDictionary<string, LogFileLogger> _loggers = new(StringComparer.OrdinalIgnoreCase);

    public LiteLogFileProvider()
    {
        try
        {
            Directory.CreateDirectory(Utils.UserAccessibleDataDirectory);
            var path = Path.Combine(Utils.UserAccessibleDataDirectory, "latest.log");
            var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, 4096,
                FileOptions.WriteThrough);
            _writer = new StreamWriter(file);
        }
        catch (Exception)
        {
            // Running without a log file is better than not running
        }
    }

    public ILogger CreateLogger(string categoryName) =>
        _writer == null
            ? NullLogger.Instance
            : _loggers.GetOrAdd(categoryName, name => new LogFileLogger(name, _writer));

    public void Dispose() => _loggers.Clear();
}
