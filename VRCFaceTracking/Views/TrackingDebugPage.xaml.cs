using Microsoft.UI.Xaml.Controls;
using VRCFaceTracking.ViewModels;

namespace VRCFaceTracking.Views;

public sealed partial class TrackingDebugPage : Page
{
    public TrackingDebugViewModel ViewModel { get; }

    public TrackingDebugPage()
    {
        ViewModel = App.GetService<TrackingDebugViewModel>();
        InitializeComponent();

        // The 10 Hz refresh only runs while the page is actually on screen.
        Loaded += (_, _) => ViewModel.Start();
        Unloaded += (_, _) => ViewModel.Stop();
    }
}
