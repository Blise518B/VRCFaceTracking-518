using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using VRCFaceTracking.Controls;
using VRCFaceTracking.Core.Params.Data.Mutation.Curves;
using VRCFaceTracking.ViewModels;

namespace VRCFaceTracking.Views;

public sealed partial class CurvesPage : Page
{
    private const int ColumnsPerRow = 2;
    private const double EditorHeight = 210;

    private sealed class OpenCard
    {
        public required CurveChannelItem Item { get; init; }
        public required CurveEditorControl Editor { get; init; }
        public required TextBlock LiveText { get; init; }
        public required ToggleSwitch EnabledToggle { get; init; }
    }

    /// <summary>
    /// Theme brush lookup that can't crash the page: falls back to a literal colour if the
    /// resource key isn't present in the active theme dictionary.
    /// </summary>
    private static Brush ThemeBrush(string key, byte r, byte g, byte b, byte a = 255)
    {
        try
        {
            if (Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush)
            {
                return brush;
            }
        }
        catch (Exception)
        {
            // Fall through to the literal colour
        }

        return new SolidColorBrush(Windows.UI.Color.FromArgb(a, r, g, b));
    }

    private readonly List<OpenCard> _openCards = new();
    private readonly DispatcherTimer _liveTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private bool _suppressToggleEvent;

    public CurvesViewModel ViewModel { get; }

    public CurvesPage()
    {
        ViewModel = App.GetService<CurvesViewModel>();
        InitializeComponent();

        _liveTimer.Tick += LiveTimer_Tick;
        _saveTimer.Tick += SaveTimer_Tick;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // The view model is shared across navigations, so (re)subscribe idempotently here
        // rather than in the constructor - otherwise stale pages keep receiving events.
        foreach (var item in ViewModel.AllChannels)
        {
            item.PropertyChanged -= ChannelItem_PropertyChanged;
            item.PropertyChanged += ChannelItem_PropertyChanged;
        }

        var mutationReady = ViewModel.Mutation != null;
        MutationMissingBar.IsOpen = !mutationReady;

        _suppressToggleEvent = true;
        MasterToggle.IsOn = ViewModel.CurvesEnabled;
        _suppressToggleEvent = false;
        MasterToggle.IsEnabled = mutationReady;

        ViewModel.RefreshModifiedMarkers();
        SyncProfileCombo();
        RebuildCards();
        _liveTimer.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _liveTimer.Stop();

        foreach (var item in ViewModel.AllChannels)
        {
            item.PropertyChanged -= ChannelItem_PropertyChanged;
        }

        // Don't lose edits made in the last few hundred milliseconds
        if (_saveTimer.IsEnabled)
        {
            _saveTimer.Stop();
            _ = ViewModel.SaveAsync();
        }
    }

    private void ChannelItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CurveChannelItem.IsOpen))
        {
            RebuildCards();
        }
    }

    private void MasterToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressToggleEvent)
        {
            return;
        }

        ViewModel.CurvesEnabled = MasterToggle.IsOn;
        ScheduleSave();
    }

    private bool _suppressProfileEvent;

    /// <summary>
    /// Repopulates the profile picker without the selection change looping back in as a
    /// user-initiated switch.
    /// </summary>
    private void SyncProfileCombo()
    {
        _suppressProfileEvent = true;
        try
        {
            ViewModel.RefreshProfiles();
            ProfileCombo.SelectedItem = ViewModel.Mutation?.EffectiveProfileName;
        }
        finally
        {
            _suppressProfileEvent = false;
        }
    }

    private void ProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressProfileEvent)
        {
            return;
        }

        if (ViewModel.SelectProfile(ProfileCombo.SelectedItem as string))
        {
            // Open editors point at the old profile's curve objects, so re-attach them
            RebuildCards();
            ScheduleSave();
        }
    }

    private void NewProfile_OnClick(object sender, RoutedEventArgs e)
    {
        ViewModel.AddProfileFromActiveSource();
        SyncProfileCombo();
        RebuildCards();
        ScheduleSave();
    }

    private void DeleteProfile_OnClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.DeleteActiveProfile())
        {
            SyncProfileCombo();
            RebuildCards();
            ScheduleSave();
        }
    }

    private void AutoSwitch_Changed(object sender, RoutedEventArgs e)
    {
        SyncProfileCombo();
        ScheduleSave();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) =>
        ViewModel.Filter(SearchBox.Text);

    private void OpenBothEyes_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in ViewModel.AllChannels)
        {
            if (item.Key is "Eye.Openness.Left" or "Eye.Openness.Right")
            {
                item.IsOpen = true;
            }
        }
    }

    private void CloseAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in ViewModel.AllChannels)
        {
            item.IsOpen = false;
        }
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private async void SaveTimer_Tick(object? sender, object e)
    {
        _saveTimer.Stop();
        await ViewModel.SaveAsync();
    }

    private void LiveTimer_Tick(object? sender, object e)
    {
        var mutation = ViewModel.Mutation;
        if (mutation == null)
        {
            return;
        }

        foreach (var card in _openCards)
        {
            var raw = mutation.GetLiveInput(card.Item.Key);
            card.Editor.SetLiveValue(raw);

            var curve = mutation.GetOrCreateCurve(card.Item.Key);
            var normalized = card.Item.Channel.Bipolar ? (raw + 1f) / 2f : raw;
            normalized = Math.Clamp(float.IsNaN(normalized) ? 0f : normalized, 0f, 1f);
            var mapped = curve.Enabled ? curve.Evaluate(normalized) : normalized;

            if (card.Item.Channel.Bipolar)
            {
                card.LiveText.Text = $"in {raw:0.00}   ->   out {mapped * 2f - 1f:0.00}";
            }
            else
            {
                card.LiveText.Text = $"in {normalized * 100:0}%   ->   out {mapped * 100:0}%";
            }
        }
    }

    private void RebuildCards()
    {
        _openCards.Clear();
        CardsHost.Children.Clear();

        var openItems = ViewModel.AllChannels.Where(i => i.IsOpen).ToList();
        EmptyHint.Visibility = openItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var mutation = ViewModel.Mutation;
        if (mutation == null)
        {
            return;
        }

        for (var i = 0; i < openItems.Count; i += ColumnsPerRow)
        {
            var row = new Grid();
            for (var c = 0; c < ColumnsPerRow; c++)
            {
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }
            row.ColumnSpacing = 10;

            for (var c = 0; c < ColumnsPerRow && i + c < openItems.Count; c++)
            {
                var card = BuildCard(openItems[i + c], mutation.GetOrCreateCurve(openItems[i + c].Key));
                Grid.SetColumn(card, c);
                row.Children.Add(card);
            }

            CardsHost.Children.Add(row);
        }
    }

    private FrameworkElement BuildCard(CurveChannelItem item, ResponseCurve curve)
    {
        var editor = new CurveEditorControl { Height = EditorHeight };
        editor.Attach(curve, item.Channel.Bipolar);

        var liveText = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            // Cyan for live numbers, matching how the GPU dashboard colours its values
            Foreground = ThemeBrush("Cat518Cyan", 0x56, 0xd9, 0xf2),
            Margin = new Thickness(0, 4, 0, 4)
        };

        var enabledToggle = new ToggleSwitch
        {
            IsOn = curve.Enabled,
            OnContent = "On",
            OffContent = "Off",
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center
        };

        var card = new OpenCard
        {
            Item = item,
            Editor = editor,
            LiveText = liveText,
            EnabledToggle = enabledToggle
        };

        enabledToggle.Toggled += (_, _) =>
        {
            curve.Enabled = enabledToggle.IsOn;
            ViewModel.RefreshModifiedMarkers();
            ScheduleSave();
        };

        editor.CurveEdited += (_, _) =>
        {
            // Drawing a curve implies wanting it applied - saves a confusing "why is nothing
            // happening" moment when the per-channel switch is still off.
            if (!curve.Enabled)
            {
                curve.Enabled = true;
                enabledToggle.IsOn = true;
            }
            ViewModel.RefreshModifiedMarkers();
            ScheduleSave();
        };

        var resetButton = new Button { Content = "Reset", Margin = new Thickness(6, 0, 0, 0) };
        resetButton.Click += (_, _) =>
        {
            curve.Reset();
            editor.Attach(curve, item.Channel.Bipolar);
            ViewModel.RefreshModifiedMarkers();
            ScheduleSave();
        };

        var closeButton = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 12 },
            Margin = new Thickness(6, 0, 0, 0)
        };
        ToolTipService.SetToolTip(closeButton, "Close this editor");
        closeButton.Click += (_, _) => item.IsOpen = false;

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleKey = item.Category switch
        {
            CurveChannels.CategoryEye => "Cat518Green",
            CurveChannels.CategoryGaze => "Cat518Cyan",
            CurveChannels.CategoryHead => "Cat518Blue",
            _ => "Cat518Purple",
        };

        var title = new TextBlock
        {
            Text = item.Name,
            Foreground = ThemeBrush(titleKey, 230, 239, 232),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(title, 0);
        Grid.SetColumn(enabledToggle, 1);
        Grid.SetColumn(resetButton, 2);
        Grid.SetColumn(closeButton, 3);
        header.Children.Add(title);
        header.Children.Add(enabledToggle);
        header.Children.Add(resetButton);
        header.Children.Add(closeButton);

        var content = new StackPanel();
        content.Children.Add(header);
        content.Children.Add(liveText);
        content.Children.Add(editor);

        _openCards.Add(card);

        // Colour the card by channel category, the same way the relay colour-codes its groups
        var (tintKey, edgeKey) = item.Category switch
        {
            CurveChannels.CategoryEye => ("Cat518GreenTint", "Cat518GreenEdge"),
            CurveChannels.CategoryGaze => ("Cat518CyanTint", "Cat518CyanEdge"),
            CurveChannels.CategoryHead => ("Cat518BlueTint", "Cat518BlueEdge"),
            _ => ("Cat518PurpleTint", "Cat518PurpleEdge"),
        };

        return new Border
        {
            Background = ThemeBrush(tintKey, 128, 128, 128, 12),
            BorderBrush = ThemeBrush(edgeKey, 128, 128, 128, 40),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12),
            Child = content
        };
    }
}
