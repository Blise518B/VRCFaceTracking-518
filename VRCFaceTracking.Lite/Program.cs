using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VRCFaceTracking.Core;
using VRCFaceTracking.Core.Contracts;
using VRCFaceTracking.Core.Contracts.Services;
using VRCFaceTracking.Core.Library;
using VRCFaceTracking.Core.Models;
using VRCFaceTracking.Core.mDNS;
using VRCFaceTracking.Core.OSC;
using VRCFaceTracking.Core.OSC.Query.mDNS;
using VRCFaceTracking.Core.Params.Data;
using VRCFaceTracking.Core.Params.Data.Mutation;
using VRCFaceTracking.Core.Services;

namespace VRCFaceTracking.Lite;

/// <summary>
/// Lightweight monitor build. One screen: which modules are loaded, whether an avatar is
/// connected, and the OSC message rates. Everything configurable lives in the full app -
/// this shares its settings files, so curve profiles and mutations tuned there apply here.
/// </summary>
public static class Program
{
    private static readonly DateTime Started = DateTime.Now;
    private static int _messagesIn;
    private static int _messagesOut;
    private static int _inPerSec;
    private static int _outPerSec;
    private static DateTime _lastRate = DateTime.Now;

    public static async Task<int> Main(string[] args)
    {
        // SteamVR launches one binary. If the user asked for the full app, hand over and exit.
        // Wrapped tightly so a fault here can never break auto-launch entirely.
        try
        {
            if (!args.Contains("--force-lite") && LaunchMode.Read() == AppLaunchMode.Full)
            {
                if (LaunchMode.TryHandOff(LaunchMode.FullExeName))
                {
                    return 0;
                }
                // Full build missing - carry on as the monitor rather than starting nothing
            }
        }
        catch (Exception)
        {
        }

        Console.Title = "VRCFaceTracking Lite - Blise518B Edition";
        CrashReportService.EarlyStartup();

        // Only one instance may own the OSC ports (KillAllProcessesOfName skips our own PID,
        // so killing our own name here only removes other lite instances)
        Core.Utils.KillAllProcessesOfName("VRCFaceTracking");
        Core.Utils.KillAllProcessesOfName("VRCFaceTracking.Lite");
        Core.Utils.KillAllProcessesOfName("VRCFaceTracking.ModuleProcess");

        var services = BuildServices();

        var crash = services.GetRequiredService<CrashReportService>();
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            crash.ReportException(e.ExceptionObject as Exception, "AppDomain.UnhandledException");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            crash.ReportException(e.Exception, "TaskScheduler.UnobservedTaskException", fatal: false);
            e.SetObserved();
        };
        crash.ReportPreviousRunIfCrashed();

        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Lite");

        try
        {
            await services.GetRequiredService<ModuleActivationService>().InitializeAsync();

            CurveCalibration.SourceNameResolver = () =>
            {
                try
                {
                    return services.GetRequiredService<TrackingSourceDetector>()
                        .Scan().FirstOrDefault(s => s.Strong)?.Name;
                }
                catch (Exception)
                {
                    return null;
                }
            };

            var oscQuery = services.GetRequiredService<OscQueryService>();
            var recv = services.GetRequiredService<OscRecvService>();
            var send = services.GetRequiredService<OscSendService>();
            var libManager = services.GetRequiredService<ILibManager>();
            var mainService = services.GetRequiredService<IMainService>();

            recv.OnMessageReceived += _ => Interlocked.Increment(ref _messagesIn);
            send.OnMessagesDispatched += count => Interlocked.Add(ref _messagesOut, count);

            logger.LogInformation("Initializing OSC...");
            await oscQuery.InitializeAsync();

            logger.LogInformation("Initializing tracking...");
            await mainService.InitializeAsync();

            // These are IHostedService in the full app; started directly here
            using var cts = new CancellationTokenSource();
            await recv.StartAsync(cts.Token);
            await services.GetRequiredService<ParameterSenderService>().StartAsync(cts.Token);

            libManager.Initialize();

            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            await RenderLoop(services, cts.Token);

            logger.LogInformation("Shutting down...");
            await mainService.Teardown();
            crash.MarkCleanExit();
            return 0;
        }
        catch (Exception ex)
        {
            crash.ReportException(ex, "Lite.Main");
            Console.WriteLine();
            Console.WriteLine($"Fatal: {ex.Message}");
            Console.WriteLine("A crash report was written to Documents\\VRCFaceTracking\\crashes.");
            return 1;
        }
    }

    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();

        services.AddLogging(b => b
            .ClearProviders()
            .SetMinimumLevel(LogLevel.Information)
            .AddProvider(new LiteLogFileProvider()));

        // Console stand-ins for the three services the WinUI project normally supplies
        services.AddSingleton<IFileService, PlainFileService>();
        services.AddSingleton<ILocalSettingsService, SharedSettingsService>();
        services.AddSingleton<IDispatcherService, ImmediateDispatcherService>();

        // Everything below is Core, shared with the full app
        services.AddSingleton<IModuleDataService, ModuleDataService>();
        services.AddSingleton<ModuleInstaller>();
        services.AddSingleton<IIdentityService, LiteIdentityService>();
        services.AddSingleton<HttpHandler>();
        services.AddSingleton<IOscTarget, OscTarget>();
        services.AddSingleton<OscSendService>();
        services.AddSingleton<OscRecvService>();
        services.AddSingleton<OscQueryService>();
        services.AddSingleton<MulticastDnsService>();
        services.AddSingleton<ParameterSenderService>();
        services.AddSingleton<UnifiedTracking>();
        services.AddSingleton<UnifiedTrackingMutator>();
        services.AddSingleton<CrashReportService>();
        services.AddSingleton<AutoRestartService>();
        services.AddSingleton<TrackingSourceDetector>();
        services.AddSingleton<ModuleActivationService>();
        services.AddSingleton<ILibManager, UnifiedLibManager>();
        services.AddSingleton<IMainService, MainStandalone>();
        services.AddTransient<AvatarConfigParser>();
        services.AddTransient<OscQueryConfigParser>();

        return services.BuildServiceProvider();
    }

    private static async Task RenderLoop(ServiceProvider services, CancellationToken token)
    {
        var libManager = services.GetRequiredService<ILibManager>();
        var oscQuery = services.GetRequiredService<OscQueryService>();
        var oscTarget = services.GetRequiredService<IOscTarget>();

        Console.Clear();
        Console.CursorVisible = false;

        while (!token.IsCancellationRequested)
        {
            UpdateRates();

            var lines = new List<string>
            {
                "VRCFaceTracking - Blise518B Edition   [ lite monitor ]",
                new string('=', 62),
                $"  uptime      {Describe(DateTime.Now - Started)}",
                string.Empty,
                "  MODULES",
            };

            var modules = SafeSnapshot(libManager);
            if (modules.Count == 0)
            {
                lines.Add("    (none loaded)");
            }
            foreach (var module in modules)
            {
                var state = module.IsDormant ? "idle  " : module.Active ? "ACTIVE" : "off   ";
                var uses = (module.UsingEye, module.UsingExpression) switch
                {
                    (true, true) => "eye + face",
                    (true, false) => "eye",
                    (false, true) => "face",
                    _ => string.Empty,
                };
                lines.Add($"    [{state}] {Trim(module.Name, 34),-34} {uses}");
                if (module.IsDormant && !string.IsNullOrWhiteSpace(module.StatusNote))
                {
                    lines.Add($"             {Trim(module.StatusNote, 48)}");
                }
            }

            lines.Add(string.Empty);
            lines.Add("  AVATAR");
            var avatar = SafeAvatar(oscQuery);
            lines.Add(avatar == null
                ? "    not connected"
                : $"    {Trim(avatar.Value.Name, 34),-34} {avatar.Value.Parameters} parameters");

            lines.Add(string.Empty);
            lines.Add("  OSC");
            lines.Add($"    in  {_inPerSec,5} msg/s   port {oscTarget.InPort}");
            lines.Add($"    out {_outPerSec,5} msg/s   port {oscTarget.OutPort}");
            lines.Add(string.Empty);
            lines.Add("  Settings, curves and calibration live in the full app.");
            lines.Add("  Ctrl+C to exit.");

            Draw(lines);

            try
            {
                await Task.Delay(500, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        Console.CursorVisible = true;
    }

    private static void UpdateRates()
    {
        var elapsed = (DateTime.Now - _lastRate).TotalSeconds;
        if (elapsed < 1.0)
        {
            return;
        }

        _inPerSec = (int)(Interlocked.Exchange(ref _messagesIn, 0) / elapsed);
        _outPerSec = (int)(Interlocked.Exchange(ref _messagesOut, 0) / elapsed);
        _lastRate = DateTime.Now;
    }

    private static List<ModuleMetadataInternal> SafeSnapshot(ILibManager libManager)
    {
        try
        {
            return libManager.LoadedModulesMetadata
                .Where(m => m != null && !string.IsNullOrWhiteSpace(m.Name))
                .ToList();
        }
        catch (Exception)
        {
            // Collection is written from the tracking thread; a torn read is not worth crashing over
            return new List<ModuleMetadataInternal>();
        }
    }

    private static (string Name, int Parameters)? SafeAvatar(OscQueryService oscQuery)
    {
        try
        {
            var info = oscQuery.AvatarInfo;
            if (info == null || string.IsNullOrWhiteSpace(info.Name))
            {
                return null;
            }
            return (info.Name, oscQuery.AvatarParameters?.Count ?? 0);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Redraws in place rather than clearing, so the screen doesn't flicker. Lines are padded
    /// to wipe whatever was longer before them.
    /// </summary>
    private static void Draw(List<string> lines)
    {
        try
        {
            var width = Math.Max(40, Math.Min(Console.WindowWidth - 1, 100));
            Console.SetCursorPosition(0, 0);
            foreach (var line in lines)
            {
                Console.WriteLine(Trim(line, width).PadRight(width));
            }
            // Clear any leftovers from a previously taller frame
            for (var i = 0; i < 4; i++)
            {
                Console.WriteLine(new string(' ', width));
            }
        }
        catch (Exception)
        {
            // Console resized mid-draw; next frame will sort it out
        }
    }

    private static string Trim(string? text, int max)
    {
        text ??= string.Empty;
        return text.Length <= max ? text : text[..Math.Max(0, max - 1)] + "â€¦";
    }

    private static string Describe(TimeSpan span) =>
        span.TotalHours >= 1
            ? $"{(int)span.TotalHours}h {span.Minutes}m"
            : span.TotalMinutes >= 1 ? $"{span.Minutes}m {span.Seconds}s" : $"{span.Seconds}s";
}
