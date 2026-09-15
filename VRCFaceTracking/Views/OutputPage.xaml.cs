using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using System.Globalization;
using Windows.ApplicationModel.DataTransfer;
using Microsoft.UI.Xaml.Controls;

using VRCFaceTracking.ViewModels;
using Windows.Storage.Pickers;
using Windows.Storage.Provider;
using Windows.Storage;
using VRCFaceTracking.Services;

namespace VRCFaceTracking.Views;

public sealed partial class OutputPage : Page
{
    public OutputViewModel ViewModel
    {
        get;
    }

    public ObservableCollection<LogLine> FilteredLog => OutputPageLogger.FilteredLogs;
    public ObservableCollection<LogLine> AllLog => OutputPageLogger.AllLogs;

    // One timer for the page, restarted per burst. The old handler created a new timer for
    // every log line - and was never unsubscribed, so the static log kept the page alive.
    private readonly DispatcherTimer _scrollTimer = new() { Interval = TimeSpan.FromMilliseconds(1) };

    public OutputPage()
    {
        ViewModel = App.GetService<OutputViewModel>();
        InitializeComponent();

        // We scroll after a small delay because windows: scrolling straight away lands a
        // line short, before the new line has been laid out.
        _scrollTimer.Tick += (_, _) =>
        {
            _scrollTimer.Stop();
            ScrollToBottom();
        };

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        FilteredLog.CollectionChanged -= OnLogChanged;
        FilteredLog.CollectionChanged += OnLogChanged;
        LogRepeater.ItemsSource = FilteredLog;
        _scrollTimer.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // The log collections are static. Anything of ours still attached to them - this
        // handler, or the repeater's own collection subscription - roots the whole page.
        FilteredLog.CollectionChanged -= OnLogChanged;
        LogRepeater.ItemsSource = null;
        _scrollTimer.Stop();
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if ( !_scrollTimer.IsEnabled )
        {
            _scrollTimer.Start();
        }
    }

    private void ScrollToBottom() => LogScroller.ChangeView(null, LogScroller.ScrollableHeight, null);

    // One pass instead of Aggregate's string-per-line, which copied the whole log again for
    // every line: quadratic, and hundreds of megabytes of garbage on a long session's log.
    private string AllLogText() => string.Join("\n", AllLog) + "\n";

    private async void SaveToFile_OnClick(object sender, RoutedEventArgs e)
    {
        // Create a file picker
        FileSavePicker savePicker = new Windows.Storage.Pickers.FileSavePicker();

        // Retrieve the window handle (HWND) of the current WinUI 3 window.
        var window = App.MainWindow;
        var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(window);

        // Initialize the file picker with the window handle (HWND).
        WinRT.Interop.InitializeWithWindow.Initialize(savePicker, hWnd);

        // Set options for your file picker
        savePicker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        savePicker.FileTypeChoices.Add("Plain Text", new List<string>() { ".txt" });
        DateTime now = DateTime.Now;
        string format = CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern + "-" + CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern;
        var dateFormatted = now.ToString(format);
        savePicker.SuggestedFileName = $"VRCFaceTracking.txt-{dateFormatted}";

        // Open the picker for the user to pick a file
        StorageFile file = await savePicker.PickSaveFileAsync();
        if (file != null)
        {
            CachedFileManager.DeferUpdates(file);

            // write to file
            var logString = AllLogText();
            await FileIO.AppendTextAsync(file, logString);

            FileUpdateStatus status = await CachedFileManager.CompleteUpdatesAsync(file);
            if (status == FileUpdateStatus.Complete)
            {
                SaveStatus.Text = "File " + file.Name + " was saved.";
            }
            else if (status == FileUpdateStatus.CompleteAndRenamed)
            {
                SaveStatus.Text = "File " + file.Name + " was renamed and saved.";
            }
            else
            {
                SaveStatus.Text = "File " + file.Name + " couldn't be saved.";
            }
        }
        else
        {
            SaveStatus.Text = "Operation cancelled.";
        }
    }

    private void CopyToClipboard_OnClick(object sender, RoutedEventArgs e)
    {
        var logString = AllLogText();
        var package = new DataPackage();
        package.SetText(logString);
        Clipboard.SetContent(package);
        SaveStatus.Text = "Copied to clipboard.";
    }
}
