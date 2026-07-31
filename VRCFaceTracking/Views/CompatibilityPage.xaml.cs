using Microsoft.UI.Xaml.Controls;
using VRCFaceTracking.ViewModels;

namespace VRCFaceTracking.Views;

public sealed partial class CompatibilityPage : Page
{
    public CompatibilityViewModel ViewModel { get; }

    public CompatibilityPage()
    {
        ViewModel = App.GetService<CompatibilityViewModel>();
        InitializeComponent();

        Loaded += (_, _) => ViewModel.Start();
        Unloaded += (_, _) => ViewModel.Stop();
    }
}
