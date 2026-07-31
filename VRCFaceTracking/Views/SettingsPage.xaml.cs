using System.ComponentModel;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using VRCFaceTracking.ViewModels;
using Windows.System;
using Microsoft.UI.Xaml.Media.Imaging;
using VRCFaceTracking.Core.Contracts;
using VRCFaceTracking.Core.Contracts.Services;
using VRCFaceTracking.Core.Params.Data;
using VRCFaceTracking.Core.Services;
using VRCFaceTracking.Helpers;
using VrcftImage = VRCFaceTracking.Core.Types.Image;

namespace VRCFaceTracking.Views;

public sealed partial class SettingsPage : Page, INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public bool HasNoRestartEvents => ViewModel.AutoRestart.RecentEvents.Count == 0;

    private void ResetRestartStats_OnClick(object sender, RoutedEventArgs e) => ViewModel.AutoRestart.ResetStats();

    private bool _launchModeReady;

    /// <summary>
    /// SteamVR launches one binary (the lite exe), which hands over to the full app when this
    /// is set to Full. Writing the preference is all that's needed - no manifest changes.
    /// </summary>
    private void LaunchModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_launchModeReady)
        {
            return;
        }

        LaunchMode.Write(LaunchModeCombo.SelectedIndex == 1 ? AppLaunchMode.Lite : AppLaunchMode.Full);
    }

    /// <summary>Same live hand-off the navigation pane's Lite Console entry performs.</summary>
    private async void SwitchToLite_OnClick(object sender, RoutedEventArgs e)
    {
        if (!ModeSwitcher.LiteAvailable)
        {
            SwitchToLiteButton.Content = "Lite exe not found";
            return;
        }

        SwitchToLiteButton.IsEnabled = false;
        SwitchToLiteButton.Content = "Switching...";
        await ModeSwitcher.SwitchToLiteAsync();
    }

    public string LastCrashText =>
        ViewModel.CrashReports.LastCrashSummary ?? "Nothing recorded in this session.";

    public Visibility NoCrashReportsVisibility =>
        ViewModel.HasCrashReports ? Visibility.Collapsed : Visibility.Visible;

    private async void OpenCrashFolder_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(CrashReportService.CrashDirectory);
            await Launcher.LaunchFolderPathAsync(CrashReportService.CrashDirectory);
        }
        catch (Exception)
        {
            // Explorer refusing to open is not worth crashing the settings page over
        }
    }

    public string Version
    {
        get
        {
            var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
            return version != null ? $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}" : "Unknown";
        }
    }

    public SettingsViewModel ViewModel
    {
        get;
    }
    
    public IOscTarget OscTarget
    {
        get;
    }
    
    public RiskySettingsViewModel RiskySettingsViewModel
    {
        get;
    }

    public WriteableBitmap UpperImageSource => _upperImageStream;
    public WriteableBitmap LowerImageSource => _lowerImageStream;

    private WriteableBitmap _upperImageStream, _lowerImageStream;
    private Stream _upperStream, _lowerStream;
    
    public SettingsPage()
    {
        ViewModel = App.GetService<SettingsViewModel>();
        RiskySettingsViewModel = App.GetService<RiskySettingsViewModel>();
        OscTarget = App.GetService<IOscTarget>();

        // Initialize hardware debug streams for upper and lower face tracking
        InitializeHardwareDebugStream(UnifiedTracking.EyeImageData, ref _upperImageStream, ref _upperStream);
        InitializeHardwareDebugStream(UnifiedTracking.LipImageData, ref _lowerImageStream, ref _lowerStream);

        ViewModel.AutoRestart.RecentEvents.CollectionChanged += (_, _) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasNoRestartEvents)));

        Loaded += OnPageLoaded;

        // Subscribe only while the page is showing, with a stored delegate so the
        // unsubscribe actually matches. The old inline `+=` in the constructor meant
        // every visit to Settings added another permanent 100Hz handler (and kept the
        // dead page instance alive through the static event) for the rest of the session.
        _onUnifiedDataUpdated = _ => DispatcherQueue?.TryEnqueue(OnTrackingDataUpdated);
        Loaded += (_, _) => UnifiedTracking.OnUnifiedDataUpdated += _onUnifiedDataUpdated;
        Unloaded += (_, _) => UnifiedTracking.OnUnifiedDataUpdated -= _onUnifiedDataUpdated;
        InitializeComponent();
    }

    private readonly Action<UnifiedTrackingData> _onUnifiedDataUpdated;

    private void InitializeHardwareDebugStream(VrcftImage image, ref WriteableBitmap bitmap, ref Stream targetStream)
    {
        var imageSize = image.ImageSize;

        if ( imageSize is { x: > 0, y: > 0 } )
        {
            bitmap = new WriteableBitmap(imageSize.x, imageSize.y);
            targetStream = bitmap.PixelBuffer.AsStream();
        }
    }

    private async void OnTrackingDataUpdated()
    {
        // Handle eye tracking

        var upperData = UnifiedTracking.EyeImageData.ImageData;
        if ( upperData != null )
        {
            // Handle device connected
            if ( _upperStream == null )
            {
                InitializeHardwareDebugStream(UnifiedTracking.EyeImageData, ref _upperImageStream, ref _upperStream);
            }
            // Handle device is valid and is providing data
            if ( _upperStream.CanWrite )
            {
                _upperStream.Position = 0;
                await _upperStream.WriteAsync(upperData, 0, upperData.Length);

                _upperImageStream.Invalidate();
            }
        }
        else
        {
            // Handle device getting unplugged / destroyed / disabled
            // Device is connected
            if ( _upperStream != null || _upperImageStream != null )
            {
                await _upperStream.DisposeAsync();
                _upperImageStream = null;
                _upperStream = null;
            }
        }

        // Handle lip tracking

        var lowerData = UnifiedTracking.LipImageData.ImageData;
        if ( lowerData != null )
        {
            // Handle device connected
            if ( _lowerStream == null )
            {
                InitializeHardwareDebugStream(UnifiedTracking.LipImageData, ref _lowerImageStream, ref _lowerStream);
            }
            // Handle device is valid and is providing data
            if ( _lowerStream.CanWrite )
            {
                _lowerStream.Position = 0;
                await _lowerStream.WriteAsync(lowerData, 0, lowerData.Length);

                _lowerImageStream.Invalidate();
            }
        }
        else
        {
            // Handle device getting unplugged / destroyed / disabled
            // Device is connected
            if ( _lowerStream != null || _lowerImageStream != null )
            {
                await _lowerStream.DisposeAsync();
                _lowerImageStream = null;
                _lowerStream = null;
            }
        }

        if ( _lowerStream == null || _upperStream == null )
        {
            HardwareDebugSeparator.Visibility = Visibility.Collapsed;
        }
        else
        {
            HardwareDebugSeparator.Visibility = Visibility.Visible;
        }
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        var currentTheme = ViewModel.ElementTheme;
        switch (currentTheme)
        {
            case ElementTheme.Light:
                themeMode.SelectedIndex = 0; break;
            case ElementTheme.Dark:
                themeMode.SelectedIndex = 1; break;
            case ElementTheme.Default:
                themeMode.SelectedIndex = 2; break;
        }

        // Set before arming the handler, so restoring the saved value isn't treated as an edit
        LaunchModeCombo.SelectedIndex = LaunchMode.Read() == AppLaunchMode.Lite ? 1 : 0;
        _launchModeReady = true;
    }

    private async void bugRequestCard_Click(object sender, RoutedEventArgs e)
    => await Launcher.LaunchUriAsync(new Uri("https://github.com/benaclejames/VRCFaceTracking/issues/new/choose"));
    
    private async void privacyPolicyCard_Click(object sender, RoutedEventArgs e) 
     => await Launcher.LaunchUriAsync(new Uri("https://github.com/benaclejames/VRCFaceTracking/blob/master/PRIVACY.md"));

    private void themeMode_SelectionChanged(object sender, RoutedEventArgs e)
    {
        var selectedTheme = ((ComboBoxItem)themeMode.SelectedItem)?.Tag?.ToString();

        if (selectedTheme != null)
        {
            var themeEnum = App.GetEnum<ElementTheme>(selectedTheme);
            if (themeEnum == ElementTheme.Default)
            {
                themeEnum = Application.Current.RequestedTheme == ApplicationTheme.Dark ? ElementTheme.Dark : ElementTheme.Light;
            }

            ViewModel.SwitchThemeCommand.Execute(themeEnum);
        }
    }

    private void AcceptToggle_OnToggled(object sender, RoutedEventArgs e)
    {
        RiskySettingsViewModel.Enabled = dangerAcceptToggle.IsOn;
        if (RiskySettingsViewModel.Enabled)
        {
            // Enable cards
            allParamsRelevant.IsEnabled = true;
            resetVRCFT.IsEnabled = true;
            resetAvatarConfig.IsEnabled = true;
            forceReInit.IsEnabled = true;

            // Enable toggles/buttons
            allParamsRelevantToggle.IsEnabled = true;
            resetVRCFTButton.IsEnabled = true;
            resetVRCAvatarConf.IsEnabled = true;
            forceReInitButton.IsEnabled = true;
        }
        else
        {
            // Disable cards
            allParamsRelevant.IsEnabled = false;
            resetVRCFT.IsEnabled = false;
            resetAvatarConfig.IsEnabled = false;
            forceReInit.IsEnabled = false;

            // Disable toggles/buttons and set them to off
            allParamsRelevantToggle.IsEnabled = false;
            allParamsRelevantToggle.IsOn = false;
            resetVRCFTButton.IsEnabled = false;
            resetVRCAvatarConf.IsEnabled = false;
            forceReInitButton.IsEnabled = false;
        }
    }

    private void forceReInitButton_OnClick(object sender, RoutedEventArgs e) => RiskySettingsViewModel.ForceReInit();

    private void resetVRCFTButton_OnClick(object sender, RoutedEventArgs e) => RiskySettingsViewModel.ResetVRCFT();

    private void resetVRCAvatarConf_OnClick(object sender, RoutedEventArgs e) => RiskySettingsViewModel.ResetAvatarOscManifests();
}
