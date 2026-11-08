using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using VRCFaceTracking.Core.Contracts.Services;

namespace VRCFaceTracking.Core.Services.Updates;

public enum UpdateState
{
    /// <summary>Nothing checked yet this session.</summary>
    Idle,
    Checking,
    UpToDate,
    /// <summary>A newer release exists; <see cref="UpdateService.Latest"/> describes it.</summary>
    Available,
    Downloading,
    /// <summary>Downloaded, verified and unpacked; one click from installing.</summary>
    ReadyToInstall,
    Installing,
    Failed,
}

/// <summary>What the shell's banner is showing, mapped to an InfoBar severity by the UI.</summary>
public enum UpdateBannerKind
{
    Update,
    Success,
    Error,
}

/// <summary>
/// The app's view of its own updates: checks GitHub once per start (unless switched off),
/// downloads and unpacks a newer release when asked, and remembers across the restart
/// which version it was installing so the next start can say whether it worked.
///
/// Never installs on its own: an update restarts the app, and tracking dropping out in
/// the middle of a VR session because a release went up would be worse than a banner.
/// All public members are meant for the UI thread; heavy work runs on the thread pool.
/// </summary>
public partial class UpdateService : ObservableObject
{
    /// <summary>Lets startup finish (modules, OSC, the first avatar) before touching the network.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(15);

    private static readonly HashSet<string> SavedProperties =
        new() { nameof(CheckOnStartup), nameof(SkippedVersion), nameof(PendingVersion) };

    [ObservableProperty]
    [property: SavedSetting("UpdateCheckOnStartup", true)]
    private bool _checkOnStartup = true;

    /// <summary>A version the user chose to skip: the startup check stays quiet about it.</summary>
    [ObservableProperty]
    [property: SavedSetting("UpdateSkippedVersion", "")]
    private string _skippedVersion = "";

    /// <summary>Set right before the app closes to install; read back on the next start.</summary>
    [ObservableProperty]
    [property: SavedSetting("UpdatePendingVersion", "")]
    private string _pendingVersion = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy), nameof(CanCheck), nameof(CanSkip), nameof(CanInstall), nameof(ShowProgress), nameof(InstallButtonText))]
    private UpdateState _state = UpdateState.Idle;

    [ObservableProperty]
    private string _statusText = "Not checked yet.";

    [ObservableProperty]
    private double _downloadProgress;

    [ObservableProperty]
    private ReleaseInfo? _latest;

    [ObservableProperty]
    private bool _showBanner;

    [ObservableProperty]
    private UpdateBannerKind _bannerKind;

    [ObservableProperty]
    private string _bannerTitle = "";

    [ObservableProperty]
    private string _bannerMessage = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBannerButtons))]
    private bool _bannerOffersUpdate;

    private readonly ILocalSettingsService _localSettingsService;
    private readonly ILogger<UpdateService> _logger;
    private bool _initialized;
    private string? _stagedAppDir;
    private CancellationTokenSource? _downloadCts;

    public UpdateService(ILocalSettingsService localSettingsService, ILogger<UpdateService> logger)
    {
        _localSettingsService = localSettingsService;
        _logger = logger;
        SelfUpdateBlocker = ReleaseUpdater.SelfUpdateBlocker(ReleaseUpdater.InstallDirectory);

        PropertyChanged += (_, e) =>
        {
            if (_initialized && e.PropertyName != null && SavedProperties.Contains(e.PropertyName))
            {
                _localSettingsService.Save(this);
            }
        };
    }

    public string CurrentVersion => ForkVersion.Number;

    /// <summary>Null when this install replaces itself; otherwise why it can't (dev build, Program Files, ...).</summary>
    public string? SelfUpdateBlocker { get; }

    public bool CanSelfUpdate => SelfUpdateBlocker == null;

    public string ReleasesPage => ReleaseUpdater.ReleasesPage;

    public bool IsBusy => State is UpdateState.Checking or UpdateState.Downloading or UpdateState.Installing;

    public bool CanCheck => !IsBusy;

    /// <summary>Skipping mid-download would be ignored: the download still installs when it finishes.</summary>
    public bool CanSkip => !IsBusy;

    public bool ShowProgress => State == UpdateState.Downloading;

    public bool ShowBannerButtons => BannerOffersUpdate;

    /// <summary>Whether the install button does something right now (a download page counts).</summary>
    public bool CanInstall => State is UpdateState.Available or UpdateState.ReadyToInstall;

    public string InstallButtonText => !CanSelfUpdate
        ? "Open download page"
        : State == UpdateState.ReadyToInstall ? "Restart and update" : "Update now";

    /// <summary>Loads settings and reports the outcome of an update the previous session started.</summary>
    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        await _localSettingsService.Load(this);
        _initialized = true;

        if (!string.IsNullOrEmpty(PendingVersion))
        {
            var target = PendingVersion;
            PendingVersion = "";

            if (ReleaseUpdater.ParseVersion(CurrentVersion) is { } now && ReleaseUpdater.ParseVersion(target) is { } wanted && now >= wanted)
            {
                _logger.LogInformation("Updated to 518 Edition v{version}.", CurrentVersion);
                ShowMessage(UpdateBannerKind.Success, $"Updated to v{CurrentVersion}",
                    "The update installed. Your settings, modules and calibration carried over.");
                StatusText = $"Updated to v{CurrentVersion}.";
                State = UpdateState.UpToDate;
            }
            else
            {
                _logger.LogWarning("The update to v{target} did not install; still on v{version}. See {log}.",
                    target, CurrentVersion, ReleaseUpdater.LogPath);
                ShowMessage(UpdateBannerKind.Error, $"The update to v{target} didn't install",
                    $"Still on v{CurrentVersion}. What happened is in {ReleaseUpdater.LogPath}.");
            }
        }

        ReleaseUpdater.CleanWorkDirectory();
    }

    /// <summary>The once-per-start check, after startup has settled. Quiet unless there's news.</summary>
    public async Task StartupCheckAsync()
    {
        if (!CheckOnStartup)
        {
            return;
        }

        // Fire-and-forget from startup: nothing may escape as an unobserved task exception,
        // which the crash reporter would file as a crash
        try
        {
            await Task.Delay(StartupDelay);
            if (State == UpdateState.Idle)
            {
                await CheckAsync(userInitiated: false);
            }
        }
        catch (Exception e)
        {
            _logger.LogWarning("Startup update check failed: {message}", e.Message);
        }
    }

    /// <summary>
    /// Asks GitHub for the newest release. A check the user started always shows its result;
    /// the startup check shows a banner only for a newer version they haven't skipped.
    /// </summary>
    public async Task CheckAsync(bool userInitiated)
    {
        if (IsBusy || State == UpdateState.ReadyToInstall)
        {
            return;
        }

        State = UpdateState.Checking;
        StatusText = "Checking GitHub for a newer release...";

        ReleaseInfo release;
        try
        {
            release = await Task.Run(() => ReleaseUpdater.FetchLatestAsync());
        }
        catch (UpdateException e)
        {
            _logger.LogInformation("Update check failed: {message} {inner}", e.Message, e.InnerException?.Message);
            State = UpdateState.Failed;
            StatusText = e.Message;
            if (userInitiated)
            {
                ShowMessage(UpdateBannerKind.Error, "Couldn't check for updates", e.Message);
            }

            return;
        }

        Latest = release;
        var checkedAt = DateTime.Now.ToString("HH:mm");

        if (!ReleaseUpdater.IsNewer(release.Version, CurrentVersion))
        {
            State = UpdateState.UpToDate;
            StatusText = $"You have the newest version (checked {checkedAt}).";
            if (userInitiated)
            {
                ShowMessage(UpdateBannerKind.Success, "You're up to date", $"v{CurrentVersion} is the newest release.");
            }

            return;
        }

        State = UpdateState.Available;
        StatusText = $"v{release.Version} is available (you have v{CurrentVersion}).";
        _logger.LogInformation("518 Edition v{latest} is available (running v{current}).", release.Version, CurrentVersion);

        if (CanSelfUpdate && release.Zip == null)
        {
            // CI attaches the files a few minutes after the release appears
            StatusText += " Its download isn't attached yet, try again in a few minutes.";
        }

        if (userInitiated || !string.Equals(SkippedVersion, release.Version, StringComparison.OrdinalIgnoreCase))
        {
            ShowBanner = false;
            BannerKind = UpdateBannerKind.Update;
            BannerTitle = $"VRCFaceTracking 518 Edition v{release.Version} is available";
            BannerMessage = CanSelfUpdate
                ? $"You have v{CurrentVersion}. Updating restarts the app, which takes a few seconds."
                : $"You have v{CurrentVersion}. {SelfUpdateBlocker}";
            BannerOffersUpdate = true;
            ShowBanner = true;
        }
    }

    /// <summary>
    /// Downloads, verifies and unpacks the newer release. For an install that can't replace
    /// itself this returns false and the UI opens the release page instead.
    /// </summary>
    public async Task<bool> DownloadAsync()
    {
        if (!CanSelfUpdate || Latest == null || State != UpdateState.Available)
        {
            return State == UpdateState.ReadyToInstall;
        }

        if (Latest.Zip is not { } zip)
        {
            // The release appears before CI has attached its files; ask GitHub again first,
            // the files may well be there by now
            State = UpdateState.Idle;
            await CheckAsync(userInitiated: false);
            if (Latest?.Zip is not { } fresh || State != UpdateState.Available)
            {
                ShowMessage(UpdateBannerKind.Error, "The download isn't ready yet",
                    "GitHub is still attaching the files to this release. Try again in a few minutes.", offerUpdate: true);
                return false;
            }

            zip = fresh;
        }

        var version = Latest.Version;
        State = UpdateState.Downloading;
        DownloadProgress = 0;
        StatusText = $"Downloading v{version}...";
        BannerMessage = StatusText;

        // Created here, on the UI thread, so reports land back on it
        var progress = new System.Progress<double>(p =>
        {
            DownloadProgress = p;
            StatusText = $"Downloading v{version}... {p:0}%";
        });

        _downloadCts = new CancellationTokenSource();
        try
        {
            var ct = _downloadCts.Token;
            _stagedAppDir = await Task.Run(async () =>
            {
                ReleaseUpdater.CleanWorkDirectory(keepVersion: version);
                var file = await ReleaseUpdater.DownloadAsync(zip, version, progress, ct);
                return ReleaseUpdater.Stage(file, version);
            }, ct);
        }
        catch (Exception e) when (e is UpdateException or OperationCanceledException)
        {
            var message = e is UpdateException ? e.Message : "Download cancelled.";
            _logger.LogWarning("Update download failed: {message} {inner}", message, e.InnerException?.Message);
            State = UpdateState.Available;
            StatusText = message;
            ShowMessage(UpdateBannerKind.Error, "The update didn't download", message, offerUpdate: true);
            return false;
        }
        finally
        {
            _downloadCts.Dispose();
            _downloadCts = null;
        }

        _logger.LogInformation("Update v{version} downloaded, verified and unpacked to {dir}.", version, _stagedAppDir);
        State = UpdateState.ReadyToInstall;
        StatusText = $"v{version} is downloaded and ready to install.";
        BannerKind = UpdateBannerKind.Update;
        BannerMessage = "Downloaded. Restarting installs it; tracking is back a few seconds later.";
        return true;
    }

    /// <summary>
    /// Starts the swap helper for the downloaded version. On success the caller must shut
    /// the app down cleanly (watchdog off first, exit code 0) right away; the helper waits
    /// for that, installs, and starts the new version. Returns false if nothing was started.
    /// </summary>
    public async Task<bool> BeginInstallAsync()
    {
        if (State != UpdateState.ReadyToInstall || _stagedAppDir == null || Latest == null)
        {
            return false;
        }

        State = UpdateState.Installing;
        StatusText = $"Installing v{Latest.Version}...";

        try
        {
            // Saved before the helper exists, so even a helper that dies leaves the next start knowing
            PendingVersion = Latest.Version;
            await _localSettingsService.Save(this);
            ReleaseUpdater.StartSwapHelper(_stagedAppDir, ReleaseUpdater.InstallDirectory, Environment.ProcessId);
            _logger.LogInformation("Update helper started for v{version}; closing for the install.", Latest.Version);
            return true;
        }
        catch (UpdateException e)
        {
            _logger.LogError("Could not start the update helper: {message} {inner}", e.Message, e.InnerException?.Message);
            PendingVersion = "";
            State = UpdateState.ReadyToInstall;
            StatusText = e.Message;
            ShowMessage(UpdateBannerKind.Error, "The update couldn't start", e.Message, offerUpdate: true);
            return false;
        }
    }

    /// <summary>Hides the banner and keeps quiet about this version on later starts.</summary>
    public void SkipLatest()
    {
        if (!CanSkip)
        {
            return;
        }

        if (Latest != null)
        {
            SkippedVersion = Latest.Version;
        }

        ShowBanner = false;
    }

    public void DismissBanner() => ShowBanner = false;

    private void ShowMessage(UpdateBannerKind kind, string title, string message, bool offerUpdate = false)
    {
        ShowBanner = false;
        BannerKind = kind;
        BannerTitle = title;
        BannerMessage = message;
        BannerOffersUpdate = offerUpdate;
        ShowBanner = true;
    }
}
