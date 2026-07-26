using System.Diagnostics;
using System.Reflection;
using System.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Sentry.Protocol;
using VRCFaceTracking.Activation;
using VRCFaceTracking.Contracts.Services;
using VRCFaceTracking.Core;
using VRCFaceTracking.Core.Contracts;
using VRCFaceTracking.Core.Contracts.Services;
using VRCFaceTracking.Core.Library;
using VRCFaceTracking.Core.mDNS;
using VRCFaceTracking.Core.Models;
using VRCFaceTracking.Core.OSC.Query.mDNS;
using VRCFaceTracking.Core.Params.Data;
using VRCFaceTracking.Core.Services;
using VRCFaceTracking.Models;
using VRCFaceTracking.Services;
using VRCFaceTracking.ViewModels;
using VRCFaceTracking.Views;
using UnhandledExceptionEventArgs = Microsoft.UI.Xaml.UnhandledExceptionEventArgs;

namespace VRCFaceTracking;

// To learn more about WinUI 3, see https://docs.microsoft.com/windows/apps/winui/winui3/.
public partial class App : Application
{
    // The .NET Generic Host provides dependency injection, configuration, logging, and other services.
    // https://docs.microsoft.com/dotnet/core/extensions/generic-host
    // https://docs.microsoft.com/dotnet/core/extensions/dependency-injection
    // https://docs.microsoft.com/dotnet/core/extensions/configuration
    // https://docs.microsoft.com/dotnet/core/extensions/logging
    public IHost Host
    {
        get;
    }
    
    private ILogger? _logger;

    /// <summary>
    /// Crash reporter, resolved defensively: it must still work if DI itself is the thing
    /// that broke, which is exactly when a crash report matters most.
    /// </summary>
    private static CrashReportService? CrashReporter
    {
        get
        {
            try
            {
                return GetService<CrashReportService>();
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    public static T GetService<T>()
        where T : class
    {
        if ((App.Current as App)!.Host.Services.GetService(typeof(T)) is not T service)
        {
            throw new ArgumentException($"{typeof(T)} needs to be registered in ConfigureServices within App.xaml.cs.");
        }

        return service;
    }

    public static WindowEx MainWindow { get; } = new MainWindow();

    public App()
    {
        InitializeComponent();

        // Before anything configures logging: LogFileProvider opens latest.log with
        // FileMode.Create, so the previous run's log has to be archived first or the
        // watchdog's automatic relaunch destroys the crash evidence within seconds.
        CrashReportService.EarlyStartup();

        // Check for a "reset" file in the root of the app directory. If one is found, wipe all files from inside it
        // and delete the file.
        var resetFile = Path.Combine(VRCFaceTracking.Core.Utils.PersistentDataDirectory, "reset");
        if (File.Exists(resetFile))
        {
            // Delete everything including files and folders in Utils.PersistentDataDirectory
            foreach (var file in Directory.EnumerateFiles(VRCFaceTracking.Core.Utils.PersistentDataDirectory, "*", SearchOption.AllDirectories))
            {
                File.Delete(file);
            }
        }


        Host = Microsoft.Extensions.Hosting.Host.
        CreateDefaultBuilder().
        ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddDebug();
            logging.AddConsole();
            logging.AddSentry(o =>
                o.Dsn =
                    "https://444b0799dd2b670efa85d866c8c12134@o4506152235237376.ingest.us.sentry.io/4506152246575104");
            logging.AddProvider(new OutputLogProvider(DispatcherQueue.GetForCurrentThread()));
            logging.AddProvider(new LogFileProvider());
        }).
        UseContentRoot(AppContext.BaseDirectory).
        ConfigureServices((context, services) =>
        {
            // Default Activation Handler
            services.AddTransient<ActivationHandler<LaunchActivatedEventArgs>, DefaultActivationHandler>();

            services.AddSingleton<ILocalSettingsService, LocalSettingsService>();
            services.AddSingleton<IThemeSelectorService, ThemeSelectorService>();
            services.AddTransient<INavigationViewService, NavigationViewService>();

            services.AddSingleton<IActivationService, ActivationService>();
            services.AddSingleton<IPageService, PageService>();
            services.AddSingleton<INavigationService, NavigationService>();
            services.AddSingleton<IDispatcherService, DispatcherService>();

            // Core Services
            services.AddTransient<IIdentityService, IdentityService>();
            services.AddSingleton<ModuleInstaller>();
            services.AddSingleton<IModuleDataService, ModuleDataService>();
            services.AddTransient<IFileService, FileService>();
            services.AddSingleton<OscQueryService>();
            services.AddSingleton<MulticastDnsService>();
            services.AddSingleton<IMainService, MainStandalone>();
            services.AddTransient<AvatarConfigParser>();
            services.AddTransient<OscQueryConfigParser>();
            services.AddSingleton<UnifiedTracking>();
            services.AddSingleton<ILibManager, UnifiedLibManager>();
            services.AddSingleton<OpenVRService>();
            services.AddSingleton<IOscTarget, OscTarget>();
            services.AddSingleton<HttpHandler>();
            services.AddSingleton<OscSendService>();
            services.AddSingleton<OscRecvService>();
            services.AddSingleton<ParameterSenderService>();
            services.AddSingleton<UnifiedTrackingMutator>();
            services.AddSingleton<AutoRestartService>();
            services.AddSingleton<CrashReportService>();
            services.AddSingleton<TrackingSourceDetector>();
            services.AddSingleton<ModuleActivationService>();
            services.AddTransient<GithubService>();

            // Views and ViewModels
            services.AddTransient<ModuleRegistryViewModel>();
            services.AddTransient<ModuleRegistryPage>();
            services.AddTransient<ParameterViewModel>();
            services.AddTransient<ParametersViewModel>();
            services.AddTransient<ParametersPage>();
            services.AddTransient<MutatorViewModel>();
            services.AddTransient<MutatorPage>();
            // Singleton so open curve editors survive navigating away and back
            services.AddSingleton<CurvesViewModel>();
            services.AddTransient<CurvesPage>();
            services.AddTransient<SourcesViewModel>();
            services.AddTransient<SourcesPage>();
            services.AddTransient<OutputViewModel>();
            services.AddTransient<OutputPage>();
            services.AddTransient<SettingsViewModel>();
            services.AddTransient<RiskySettingsViewModel>();
            services.AddTransient<SettingsPage>();
            services.AddTransient<MainViewModel>();
            services.AddTransient<MainPage>();
            services.AddTransient<ShellPage>();
            services.AddTransient<ShellViewModel>();
            
            services.AddHostedService<ParameterSenderService>(provider => provider.GetService<ParameterSenderService>());
            services.AddHostedService<OscRecvService>(provider => provider.GetService<OscRecvService>());

            // Configuration
            services.Configure<LocalSettingsOptions>(context.Configuration.GetSection(nameof(LocalSettingsOptions)));
        }).
        Build();
        
        var logBuilder = App.GetService<ILoggerFactory>();
        _logger = logBuilder.CreateLogger("App");
    }

    protected async override void OnLaunched(LaunchActivatedEventArgs args)
    {
        base.OnLaunched(args);

        SentrySdk.Init(o =>
        {
            o.Dsn = "https://444b0799dd2b670efa85d866c8c12134@o4506152235237376.ingest.sentry.io/4506152246575104";
            o.TracesSampleRate = 1.0;
            o.AutoSessionTracking = true;
            #if DEBUG
            o.Environment = "debug";
            #else
            o.Environment = "release";
            #endif
            var version = Assembly.GetEntryAssembly()?.GetName().Version;
            if (version != null)
            {
                o.Release = $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
            }

            o.IsGlobalModeEnabled = true;
        });
        Current.UnhandledException += ExceptionHandler;

        // Current.UnhandledException only covers exceptions reaching the UI thread.
        // Background threads and faulted tasks bypass it entirely, and those are exactly
        // where tracking code runs - so hook them too or their crashes go unexplained.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            CrashReporter?.ReportException(e.ExceptionObject as Exception, "AppDomain.UnhandledException");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            // Not fatal: we observe it and keep running, so it must not be recorded as this
            // session's explanation - a later hard crash still needs to be reported.
            CrashReporter?.ReportException(e.Exception, "TaskScheduler.UnobservedTaskException", fatal: false);
            e.SetObserved();
        };

        // Kill any other instances of VRCFaceTracking.exe and our module processes
        Core.Utils.KillAllProcessesOfName("VRCFaceTracking");
        Core.Utils.KillAllProcessesOfName("VRCFaceTracking.ModuleProcess");

        await StartCrashWatchdogAsync();

        // Now that logging is live, explain any previous session that died silently
        CrashReporter?.ReportPreviousRunIfCrashed();

        // Must load before ActivationService reaches _libManager.Initialize(), since that's
        // where the per-module start decisions get applied
        await App.GetService<ModuleActivationService>().InitializeAsync();

        await App.GetService<IActivationService>().ActivateAsync(args);
        await Host.StartAsync();
    }

    /// <summary>
    /// Initializes the auto-restart service, records a relaunch if the watchdog brought us
    /// back after a crash, and spawns a fresh watchdog for this instance (if enabled).
    /// Any watchdog attached to an instance we just killed notices us running and exits
    /// on its own, so it's safe to always spawn a fresh one for ourselves.
    /// </summary>
    private async Task StartCrashWatchdogAsync()
    {
        try
        {
            var autoRestart = App.GetService<AutoRestartService>();
            await autoRestart.InitializeAsync();

            // If we were just relaunched by a watchdog, it passed us the consecutive restart count
            var restartCount = 0;
            var cmdArgs = Environment.GetCommandLineArgs();
            for (var i = 0; i < cmdArgs.Length - 1; i++)
            {
                if (cmdArgs[i] == "--restarted-count" && int.TryParse(cmdArgs[i + 1], out var parsed))
                {
                    restartCount = parsed;
                    break;
                }
            }

            if (restartCount > 0)
            {
                _logger?.LogInformation("VRCFaceTracking was automatically relaunched after a crash (restart {count}).", restartCount);
                autoRestart.RecordAppRelaunch(restartCount);
            }

            if (autoRestart.Enabled)
            {
                autoRestart.StartWatchdog(restartCount);
            }
            else
            {
                _logger?.LogInformation("Auto-restart is disabled in settings; crash watchdog not started.");
            }
        }
        catch (Exception e)
        {
            _logger?.LogWarning("Failed to start crash watchdog: {message}. Auto-relaunch is disabled.", e.Message);
        }
    }
    
    [SecurityCritical]
    internal void ExceptionHandler(object sender, UnhandledExceptionEventArgs e)
    {
        // We need to hold the reference, because the Exception property is cleared when accessed.
        var exception = e.Exception;

        // Write our own report first: Sentry needs the network and the user needs a file
        // on disk they can actually read and send on.
        CrashReporter?.ReportException(exception, "Application.UnhandledException");

        if (exception != null)
        {
            // Tells Sentry this was an Unhandled Exception
            exception.Data[Mechanism.HandledKey] = false;
            exception.Data[Mechanism.MechanismKey] = "Application.UnhandledException";
            SentrySdk.CaptureException(exception);
            // Make sure the event is flushed to disk or to Sentry
            SentrySdk.FlushAsync(TimeSpan.FromSeconds(3)).Wait();
            
            _logger?.LogError(exception, "Unhandled exception");
            _logger?.LogCritical("Stacktrace: {0}", exception.StackTrace);
            _logger?.LogCritical("Inner exception: {0}", exception.InnerException);
            _logger?.LogCritical("Message: {0}", exception.Message);
        }
    }

    public static TEnum GetEnum<TEnum>(string text) where TEnum : struct
    {
        if (!typeof(TEnum).GetTypeInfo().IsEnum)
        {
            throw new InvalidOperationException("Generic parameter 'TEnum' must be an enum.");
        }
        return (TEnum)Enum.Parse(typeof(TEnum), text);
    }
}
