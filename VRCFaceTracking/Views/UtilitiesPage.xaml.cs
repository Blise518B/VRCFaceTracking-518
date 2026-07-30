using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VRCFaceTracking.ViewModels;

namespace VRCFaceTracking.Views;

public sealed partial class UtilitiesPage : Page
{
    public UtilitiesViewModel ViewModel { get; }

    public UtilitiesPage()
    {
        ViewModel = App.GetService<UtilitiesViewModel>();
        InitializeComponent();

        Loaded += (_, _) =>
        {
            // Commands may have changed on the Sources page since last visit.
            ViewModel.Rebuild();
            ViewModel.Start();
        };
        Unloaded += (_, _) => ViewModel.Stop();
    }

    private void LaunchAll_OnClick(object sender, RoutedEventArgs e) => ViewModel.LaunchAllMissing();
}
