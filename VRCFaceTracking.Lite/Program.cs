using System.Diagnostics;
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

        // When the full app hands over to us it is still finishing its clean shutdown
        // (saving mutation data, stopping its watchdog). Give it a few seconds before
        // the exclusivity sweep below, so a live switch never becomes a hard kill
        // mid-save. Costs nothing when no full app is running.
        try
        {
            for (var waited = 0; waited < 8000 && Process.GetProcessesByName("VRCFaceTracking").Length > 0; waited += 200)
            {
                Thread.Sleep(200);
            }
        }
        catch (Exception)
        {
        }

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
            await services.GetRequiredService<OscRouterClient>().InitializeAsync();

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

            send.OnMessagesDispatched += count => Interlocked.Add(ref _messagesOut, count);

            logger.LogInformation("Initializing OSC...");
            await oscQuery.InitializeAsync();

            // Attach AFTER InitializeAsync: it ASSIGNS OnMessageReceived (rather than
            // adding to it), so a counter hooked earlier gets silently wiped and the
            // monitor reports "in 0" forever no matter how much data arrives.
            recv.OnMessageReceived += _ => Interlocked.Increment(ref _messagesIn);

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

            var switchToFull = await RenderLoop(services, cts);

            logger.LogInformation("Shutting down...");
            await mainService.Teardown();
            crash.MarkCleanExit();

            if (switchToFull)
            {
                // Teardown finished, so the full app boots into free OSC ports.
                logger.LogInformation("Handing off to the full app...");
                LaunchMode.TryHandOff(LaunchMode.FullExeName);
            }

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
        services.AddSingleton<OscRouterClient>();
        services.AddSingleton<ILibManager, UnifiedLibManager>();
        services.AddSingleton<IMainService, MainStandalone>();
        services.AddTransient<AvatarConfigParser>();
        services.AddTransient<OscQueryConfigParser>();

        return services.BuildServiceProvider();
    }

    /// <summary>One colored run of text; a line is a sequence of these.</summary>
    private readonly record struct Seg(string Text, ConsoleColor Color);

    // The neon palette, mapped onto the 16 console colors: green frame/headers,
    // cyan live values, purple (magenta) secondary info, amber notes, red failures.
    private const ConsoleColor Frame = ConsoleColor.DarkGreen;
    private const ConsoleColor Header = ConsoleColor.Green;
    private const ConsoleColor Value = ConsoleColor.Cyan;
    private const ConsoleColor Secondary = ConsoleColor.Magenta;
    private const ConsoleColor Note = ConsoleColor.DarkYellow;
    private const ConsoleColor Alert = ConsoleColor.Red;
    private const ConsoleColor KeyHint = ConsoleColor.Yellow;
    private const ConsoleColor Dim = ConsoleColor.DarkGray;
    private const ConsoleColor Body = ConsoleColor.Gray;

    private static Seg[] Line(params Seg[] segs) => segs;
    private static readonly Seg[] Blank = Array.Empty<Seg>();

    /// <summary>Runs until cancelled. Returns true when the user asked to switch to the full app.</summary>
    private static async Task<bool> RenderLoop(ServiceProvider services, CancellationTokenSource cts)
    {
        var token = cts.Token;
        var libManager = services.GetRequiredService<ILibManager>();
        var oscQuery = services.GetRequiredService<OscQueryService>();
        var oscTarget = services.GetRequiredService<IOscTarget>();
        var switchToFull = false;
        var canSwitch = LaunchMode.CanHandOff(LaunchMode.FullExeName);

        // A redirected stdout (SteamVR's log capture, `lite.exe > file`) has no
        // console buffer, so Clear/SetCursorPosition throw "handle is invalid".
        // In-place rendering only makes sense on a real console; otherwise fall
        // back to printing a snapshot every few seconds.
        var interactive = !Console.IsOutputRedirected;
        if (interactive)
        {
            try
            {
                Console.Clear();
                Console.CursorVisible = false;
            }
            catch (IOException)
            {
                interactive = false;
            }
        }

        var lastPlainPrint = DateTime.MinValue;
        while (!token.IsCancellationRequested)
        {
            UpdateRates();

            var modules = SafeSnapshot(libManager);
            var anyActive = modules.Any(m => m.Active && !m.IsDormant);

            var lines = new List<Seg[]>
            {
                Line(new Seg("VRCFaceTracking", Header), new Seg(" - Blise518B Edition   ", Body),
                     new Seg("[ lite monitor ]", Secondary)),
                Line(new Seg(new string('=', 62), Frame)),
                Line(new Seg("  uptime      ", Dim), new Seg(Describe(DateTime.Now - Started), Value)),
                Blank,
                Line(new Seg("  MODULES", Header)),
            };

            if (modules.Count == 0)
            {
                lines.Add(Line(new Seg("    (none loaded)", Dim)));
            }
            foreach (var module in modules)
            {
                var (state, stateColor) = module.IsDormant
                    ? ("idle  ", Note)
                    : module.Active ? ("ACTIVE", Header) : ("off   ", Dim);
                var uses = (module.UsingEye, module.UsingExpression) switch
                {
                    (true, true) => "eye + face",
                    (true, false) => "eye",
                    (false, true) => "face",
                    _ => string.Empty,
                };
                lines.Add(Line(new Seg("    [", Dim), new Seg(state, stateColor), new Seg("] ", Dim),
                               new Seg($"{Trim(module.Name, 34),-34} ", Body), new Seg(uses, Secondary)));
                if (module.IsDormant && !string.IsNullOrWhiteSpace(module.StatusNote))
                {
                    lines.Add(Line(new Seg($"             {Trim(module.StatusNote, 48)}", Note)));
                }
            }

            lines.Add(Blank);
            lines.Add(Line(new Seg("  AVATAR", Header)));
            var avatar = SafeAvatar(oscQuery);
            lines.Add(avatar == null
                ? Line(new Seg("    not connected", Alert))
                : Line(new Seg($"    {Trim(avatar.Value.Name, 34),-34} ", Value),
                       new Seg(avatar.Value.Parameters.ToString(), Value), new Seg(" parameters", Dim)));

            lines.Add(Blank);
            lines.Add(Line(new Seg("  OSC", Header)));
            // A zero rate while tracking is active is the "why is my face frozen"
            // signal - paint it red so it can't hide in the noise.
            var inColor = _inPerSec > 0 ? Value : anyActive ? Alert : Dim;
            var outColor = _outPerSec > 0 ? Value : anyActive ? Alert : Dim;
            lines.Add(Line(new Seg("    in  ", Dim), new Seg($"{_inPerSec,5}", inColor),
                           new Seg(" msg/s   port ", Dim), new Seg(oscTarget.InPort.ToString(), Body)));
            lines.Add(Line(new Seg("    out ", Dim), new Seg($"{_outPerSec,5}", outColor),
                           new Seg(" msg/s   port ", Dim), new Seg(oscTarget.OutPort.ToString(), Body)));
            lines.Add(Blank);
            lines.Add(Line(new Seg("  Settings, curves and calibration live in the full app.", Dim)));
            lines.Add(canSwitch && interactive
                ? Line(new Seg("  [F]", KeyHint), new Seg(" switch to the full app    ", Dim),
                       new Seg("[Ctrl+C]", KeyHint), new Seg(" quit", Dim))
                : Line(new Seg("  [Ctrl+C]", KeyHint), new Seg(" quit", Dim)));

            if (interactive)
            {
                Draw(lines);

                // Keyboard: F hands this session back to the full app
                try
                {
                    while (Console.KeyAvailable)
                    {
                        var key = Console.ReadKey(intercept: true);
                        if (key.Key == ConsoleKey.F && canSwitch)
                        {
                            switchToFull = true;
                            cts.Cancel();
                        }
                    }
                }
                catch (Exception)
                {
                    // Redirected/absent stdin; keys simply aren't available
                }
            }
            else if (DateTime.Now - lastPlainPrint >= TimeSpan.FromSeconds(5))
            {
                // Sequential snapshots for logs/pipes - no cursor tricks, no colors
                lastPlainPrint = DateTime.Now;
                foreach (var line in lines)
                {
                    Console.WriteLine(string.Concat(line.Select(s => s.Text)));
                }
                Console.WriteLine();
            }

            try
            {
                await Task.Delay(500, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        if (interactive)
        {
            try
            {
                Console.ResetColor();
                Console.CursorVisible = true;
            }
            catch (IOException)
            {
                // Console vanished mid-run; nothing to restore
            }
        }

        return switchToFull;
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
    /// to wipe whatever was longer before them; each segment carries its own color.
    /// </summary>
    private static void Draw(List<Seg[]> lines)
    {
        try
        {
            var width = Math.Max(40, Math.Min(Console.WindowWidth - 1, 100));
            Console.SetCursorPosition(0, 0);
            foreach (var line in lines)
            {
                var remaining = width;
                foreach (var seg in line)
                {
                    if (remaining <= 0)
                    {
                        break;
                    }

                    var text = seg.Text.Length > remaining ? seg.Text[..remaining] : seg.Text;
                    Console.ForegroundColor = seg.Color;
                    Console.Write(text);
                    remaining -= text.Length;
                }

                Console.ResetColor();
                if (remaining > 0)
                {
                    Console.Write(new string(' ', remaining));
                }
                Console.WriteLine();
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
