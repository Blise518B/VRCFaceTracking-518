using System.Collections.Specialized;
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

        // The wire tap only runs while this page is on screen. The Rows handler is attached
        // for the same span: Rows lives in a singleton, and an inline += in the constructor
        // kept every VrcOutputPage ever opened alive (and re-binding) for the session.
        _onRowsChanged = (_, _) => Bindings.Update();
        Loaded += (_, _) =>
        {
            ViewModel.Rows.CollectionChanged -= _onRowsChanged;
            ViewModel.Rows.CollectionChanged += _onRowsChanged;
            ViewModel.Start();
        };
        Unloaded += (_, _) =>
        {
            ViewModel.Rows.CollectionChanged -= _onRowsChanged;
            ViewModel.Stop();
        };
    }

    private readonly NotifyCollectionChangedEventHandler _onRowsChanged;

    public Visibility NoRowsVisibility =>
        ViewModel.Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void Clear_OnClick(object sender, RoutedEventArgs e) => ViewModel.ClearSeen();
}
