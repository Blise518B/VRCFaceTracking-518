using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VRCFaceTracking.ViewModels;

namespace VRCFaceTracking.Views;

public sealed partial class SourcesPage : Page
{
    public SourcesViewModel ViewModel { get; }

    public SourcesPage()
    {
        ViewModel = App.GetService<SourcesViewModel>();
        InitializeComponent();

        Loaded += (_, _) => ViewModel.Refresh();
    }

    public Visibility NoModulesVisibility =>
        ViewModel.Modules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void Rescan_OnClick(object sender, RoutedEventArgs e) => ViewModel.Refresh();

    private void RestartTracking_OnClick(object sender, RoutedEventArgs e) => ViewModel.RestartTracking();
}
