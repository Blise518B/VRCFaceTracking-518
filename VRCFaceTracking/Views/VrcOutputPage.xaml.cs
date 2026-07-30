using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VRCFaceTracking.ViewModels;

namespace VRCFaceTracking.Views;

public sealed partial class VrcOutputPage : Page
{
    public VrcOutputViewModel ViewModel { get; }

    public VrcOutputPage()
    {
        ViewModel = App.GetService<VrcOutputViewModel>();
        InitializeComponent();

        // The wire tap only runs while this page is on screen.
        Loaded += (_, _) => ViewModel.Start();
        Unloaded += (_, _) => ViewModel.Stop();

        ViewModel.Rows.CollectionChanged += (_, _) => Bindings.Update();
    }

    public Visibility NoRowsVisibility =>
        ViewModel.Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void Clear_OnClick(object sender, RoutedEventArgs e) => ViewModel.ClearSeen();
}
