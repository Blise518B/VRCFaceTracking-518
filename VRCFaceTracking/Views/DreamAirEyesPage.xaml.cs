using Microsoft.UI.Xaml.Controls;
using VRCFaceTracking.ViewModels;

namespace VRCFaceTracking.Views;

public sealed partial class DreamAirEyesPage : Page
{
    public DreamAirEyesViewModel ViewModel { get; }

    public DreamAirEyesPage()
    {
        ViewModel = App.GetService<DreamAirEyesViewModel>();
        InitializeComponent();

        // The 4 Hz refresh only runs while the page is actually on screen.
        Loaded += (_, _) => ViewModel.Start();
        Unloaded += (_, _) => ViewModel.Stop();
    }
}
