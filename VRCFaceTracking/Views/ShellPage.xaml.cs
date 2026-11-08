using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

using VRCFaceTracking.Contracts.Services;
using VRCFaceTracking.Core.Services.Updates;
using VRCFaceTracking.Helpers;
using VRCFaceTracking.ViewModels;

using Windows.System;

namespace VRCFaceTracking.Views;

// TODO: Update NavigationViewItem titles and icons in ShellPage.xaml.
public sealed partial class ShellPage : Page
{
    public ShellViewModel ViewModel
    {
        get;
    }

    /// <summary>Drives the update banner above every page.</summary>
    public UpdateService Updates
    {
        get;
    }

    public InfoBarSeverity SeverityFor(UpdateBannerKind kind) => kind switch
    {
        UpdateBannerKind.Success => InfoBarSeverity.Success,
        UpdateBannerKind.Error => InfoBarSeverity.Error,
        _ => InfoBarSeverity.Informational,
    };

    private async void UpdateNow_Click(object sender, RoutedEventArgs e) => await UpdateInstaller.UpdateNowAsync();

    private async void WhatsNew_Click(object sender, RoutedEventArgs e) =>
        await Launcher.LaunchUriAsync(new Uri(Updates.Latest?.PageUrl ?? Updates.ReleasesPage));

    private void SkipVersion_Click(object sender, RoutedEventArgs e) => Updates.SkipLatest();

    private void UpdateBanner_CloseButtonClick(InfoBar sender, object args) => Updates.DismissBanner();

    public ShellPage(ShellViewModel viewModel)
    {
        ViewModel = viewModel;
        Updates = App.GetService<UpdateService>();
        InitializeComponent();

        ViewModel.NavigationService.Frame = NavigationFrame;
        ViewModel.NavigationViewService.Initialize(NavigationViewControl);

        // TODO: Set the title bar icon by updating /Assets/WindowIcon.ico.
        // A custom title bar is required for full window theme and Mica support.
        // https://docs.microsoft.com/windows/apps/develop/title-bar?tabs=winui3#full-customization
        App.MainWindow.ExtendsContentIntoTitleBar = true;
        App.MainWindow.SetTitleBar(AppTitleBar);
        App.MainWindow.Activated += MainWindow_Activated;
        AppTitleBarText.Text = "AppDisplayName".GetLocalized();
    }

    private void OnLoaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        KeyboardAccelerators.Add(BuildKeyboardAccelerator(VirtualKey.Left, VirtualKeyModifiers.Menu));
        KeyboardAccelerators.Add(BuildKeyboardAccelerator(VirtualKey.GoBack));

        // Nothing to switch to in a build without the console exe alongside it
        LiteConsoleNavItem.Visibility = ModeSwitcher.LiteAvailable
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>
    /// The Lite Console footer entry runs an action instead of navigating, so it's handled
    /// here; NavigationViewService ignores it (no NavigateTo attached property).
    /// </summary>
    private async void NavigationViewControl_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (!ReferenceEquals(args.InvokedItemContainer, LiteConsoleNavItem))
        {
            return;
        }

        // Keep the real page highlighted rather than leaving an action looking "selected"
        sender.SelectedItem = ViewModel.Selected;
        await ModeSwitcher.SwitchToLiteAsync();
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        var resource = args.WindowActivationState == WindowActivationState.Deactivated ? "WindowCaptionForegroundDisabled" : "WindowCaptionForeground";

        AppTitleBarText.Foreground = (SolidColorBrush)App.Current.Resources[resource];
    }

    private void NavigationViewControl_DisplayModeChanged(NavigationView sender, NavigationViewDisplayModeChangedEventArgs args)
    {
        AppTitleBar.Margin = new Thickness()
        {
            Left = sender.CompactPaneLength * (sender.DisplayMode == NavigationViewDisplayMode.Minimal ? 2 : 1),
            Top = AppTitleBar.Margin.Top,
            Right = AppTitleBar.Margin.Right,
            Bottom = AppTitleBar.Margin.Bottom
        };
    }

    private static KeyboardAccelerator BuildKeyboardAccelerator(VirtualKey key, VirtualKeyModifiers? modifiers = null)
    {
        var keyboardAccelerator = new KeyboardAccelerator() { Key = key };

        if (modifiers.HasValue)
        {
            keyboardAccelerator.Modifiers = modifiers.Value;
        }

        keyboardAccelerator.Invoked += OnKeyboardAcceleratorInvoked;

        return keyboardAccelerator;
    }

    private static void OnKeyboardAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        var navigationService = App.GetService<INavigationService>();

        var result = navigationService.GoBack();

        args.Handled = result;
    }
}
