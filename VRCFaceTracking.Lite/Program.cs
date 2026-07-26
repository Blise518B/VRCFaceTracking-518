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

    /// <summary>One colored run of text; a line is a sequence of these. A background
    /// color turns the run into a solid block - that's what a "button" looks like.</summary>
    private readonly record struct Seg(string Text, ConsoleColor Color, ConsoleColor? Bg = null);

    private enum UiAction { None, SwitchFull, ToggleRouter, Reinit, Quit }

    /// <summary>Clickable cells of the last drawn frame: row + column span per button.</summary>
    private static readonly List<(int Row, int ColStart, int ColEnd, UiAction Action)> HitRegions = new();

    private static int _reinitRunning;
    private static string _status = string.Empty;
    private static DateTime _statusUntil = DateTime.MinValue;

    private static void SetStatus(string text)
    {
        _status = text;
        _statusUntil = DateTime.Now.AddSeconds(4);
    }

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
        var routerClient = services.GetRequiredService<OscRouterClient>();
        var mainService = services.GetRequiredService<IMainService>();
        var switchToFull = false;
        var canSwitch = LaunchMode.CanHandOff(LaunchMode.FullExeName);

        void Perform(UiAction action)
        {
            switch (action)
            {
                case UiAction.SwitchFull when canSwitch:
                    switchToFull = true;
                    cts.Cancel();
                    break;
                case UiAction.Quit:
                    cts.Cancel();
                    break;
                case UiAction.ToggleRouter:
                    routerClient.Enabled = !routerClient.Enabled;
                    SetStatus(routerClient.Enabled
                        ? "router 518: subscribed again"
                        : "router 518: off (native OSC only)");
                    break;
                case UiAction.Reinit:
                    // Same as the full app's Force Re-init; guarded so mashing the
                    // button can't stack teardowns on top of each other.
                    if (Interlocked.CompareExchange(ref _reinitRunning, 1, 0) == 0)
                    {
                        SetStatus("reinitializing modules...");
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await mainService.Teardown();
                                await mainService.InitializeAsync();
                                SetStatus("modules reinitialized");
                            }
                            catch (Exception e)
                            {
                                SetStatus($"reinit failed: {e.Message}");
                            }
                            finally
                            {
                                _reinitRunning = 0;
                            }
                        });
                    }
                    break;
            }
        }

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

        // Mouse + keyboard reader; if it can't take over the input buffer (redirected
        // stdin, exotic host) the loop below falls back to plain key polling.
        using var input = interactive ? new ConsoleInput() : null;

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
            var statusVisible = DateTime.Now < _statusUntil && _status.Length > 0;
            lines.Add(statusVisible
                ? Line(new Seg($"  {_status}", Note))
                : Line(new Seg("  Settings, curves and calibration live in the full app.", Dim)));

            if (interactive)
            {
                // Button row: solid color blocks, click regions recorded per frame
                HitRegions.Clear();
                var buttonRow = lines.Count;
                var buttonSegs = new List<Seg> { new("  ", Body) };
                var col = 2;

                void AddButton(string label, ConsoleColor fg, ConsoleColor bg, UiAction action)
                {
                    HitRegions.Add((buttonRow, col, col + label.Length - 1, action));
                    buttonSegs.Add(new Seg(label, fg, bg));
                    buttonSegs.Add(new Seg("  ", Body));
                    col += label.Length + 2;
                }

                if (canSwitch)
                {
                    AddButton("[ FULL APP ]", ConsoleColor.Black, ConsoleColor.Green, UiAction.SwitchFull);
                }
                AddButton(routerClient.Enabled ? "[ ROUTER ON ]" : "[ ROUTER OFF ]",
                    routerClient.Enabled ? ConsoleColor.Black : ConsoleColor.Gray,
                    routerClient.Enabled ? ConsoleColor.Cyan : ConsoleColor.DarkGray,
                    UiAction.ToggleRouter);
                AddButton(_reinitRunning == 1 ? "[ REINIT... ]" : "[ REINIT ]",
                    ConsoleColor.Black, ConsoleColor.DarkYellow, UiAction.Reinit);
                AddButton("[ QUIT ]", ConsoleColor.White, ConsoleColor.DarkRed, UiAction.Quit);
                lines.Add(buttonSegs.ToArray());
                lines.Add(Line(new Seg("  click a button, or keys: ", Dim),
                    new Seg("F", KeyHint), new Seg(" full app  ", Dim),
                    new Seg("R", KeyHint), new Seg(" router  ", Dim),
                    new Seg("I", KeyHint), new Seg(" reinit  ", Dim),
                    new Seg("Q", KeyHint), new Seg(" quit", Dim)));
            }
            else
            {
                lines.Add(Line(new Seg("  [Ctrl+C]", KeyHint), new Seg(" quit", Dim)));
            }

            if (interactive)
            {
                Draw(lines);

                if (input is { Active: true })
                {
                    while (input.TryDequeue(out var ev))
                    {
                        var action = UiAction.None;
                        if (ev.IsClick)
                        {
                            foreach (var region in HitRegions)
                            {
                                if (ev.Y == region.Row && ev.X >= region.ColStart && ev.X <= region.ColEnd)
                                {
                                    action = region.Action;
                                    break;
                                }
                            }
                        }
                        else
                        {
                            action = ev.Key switch
                            {
                                ConsoleKey.F => UiAction.SwitchFull,
                                ConsoleKey.R => UiAction.ToggleRouter,
                                ConsoleKey.I => UiAction.Reinit,
                                ConsoleKey.Q => UiAction.Quit,
                                _ => UiAction.None,
                            };
                        }

                        Perform(action);
                    }
                }
                else
                {
                    // Input reader unavailable: plain key polling still works
                    try
                    {
                        while (Console.KeyAvailable)
                        {
                            var key = Console.ReadKey(intercept: true);
                            Perform(key.Key switch
                            {
                                ConsoleKey.F => UiAction.SwitchFull,
                                ConsoleKey.R => UiAction.ToggleRouter,
                                ConsoleKey.I => UiAction.Reinit,
                                ConsoleKey.Q => UiAction.Quit,
                                _ => UiAction.None,
                            });
                        }
                    }
                    catch (Exception)
                    {
                        // Redirected/absent stdin; keys simply aren't available
                    }
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
            Console.ResetColor();
            var defaultBg = Console.BackgroundColor;
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
                    Console.BackgroundColor = seg.Bg ?? defaultBg;
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
