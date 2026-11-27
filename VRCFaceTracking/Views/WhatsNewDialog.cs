using Microsoft.Extensions.Logging;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using VRCFaceTracking.Contracts.Services;
using VRCFaceTracking.Core;
using VRCFaceTracking.Core.Contracts.Services;
using VRCFaceTracking.Core.Services.Updates;
using VRCFaceTracking.Services;
using VRCFaceTracking.ViewModels;

namespace VRCFaceTracking.Views;

/// <summary>
/// The "What's new" window (FORK-NOTES.md, "What's new"): once at the first launch after an
/// update, and from Settings, Updates. One release per page, newest first, with Back / Next
/// when there are several; a change that has a place in the app gets "Take me there", which
/// closes the window and opens it. The content and the rule for when it shows are Core's
/// <see cref="WhatsNew"/>; this only draws it.
/// </summary>
public static class WhatsNewDialog
{
    private static bool _launchHandled;
    private static bool _open;

    private static ILogger Logger => App.GetService<ILoggerFactory>().CreateLogger("WhatsNew");

    /// <summary>At launch, once per process: the notes this install hasn't had yet, if any.</summary>
    public static async Task ShowOnLaunchAsync(XamlRoot root)
    {
        if (_launchHandled)
        {
            return;
        }

        _launchHandled = true;
        try
        {
            var settings = App.GetService<ILocalSettingsService>();
            var seen = await settings.ReadSettingAsync(WhatsNew.SeenSettingKey, "");
            var firstInstall = settings is LocalSettingsService local && !local.HadSettingsFile;
            var (show, markSeen) = WhatsNew.OnLaunch(seen, ForkVersion.Number, firstInstall);
            if (markSeen != null)
            {
                // Before showing: a window closed by quitting the app still counts as shown
                await settings.SaveSettingAsync(WhatsNew.SeenSettingKey, markSeen);
            }

            if (show.Count > 0)
            {
                Logger.LogInformation("What's new: showing the notes for {versions} (last seen: {seen}).",
                    string.Join(", ", show.Select(r => r.Version)), string.IsNullOrEmpty(seen) ? "none" : seen);
                await ShowAsync(root, show);
            }
        }
        catch (Exception e)
        {
            Logger.LogWarning("What's new: could not show the notes: {message}", e.Message);
        }
    }

    /// <summary>From Settings: every release this build has notes for.</summary>
    public static async Task ShowAllAsync(XamlRoot root)
    {
        try
        {
            await ShowAsync(root, WhatsNew.UpTo(ForkVersion.Number));
        }
        catch (Exception e)
        {
            Logger.LogWarning("What's new: could not show the notes: {message}", e.Message);
        }
    }

    /// <summary>Whether a "Take me there" target is in the app here.</summary>
    public static bool IsAvailable(string target) => target switch
    {
        WhatsNew.TargetUpdates => true,
        WhatsNew.TargetDreamAirEyes => ShellPage.DreamAirEyesShown,
        _ => false,
    };

    private static async Task ShowAsync(XamlRoot root, IReadOnlyList<WhatsNewRelease> releases)
    {
        var pages = releases
            .Select(r => new WhatsNewRelease(r.Version,
                r.Items.Where(i => !i.OnlyWithTarget || IsAvailable(i.Target)).ToList()))
            .Where(r => r.Items.Count > 0)
            .ToList();
        if (pages.Count == 0 || _open)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "What's new",
            CloseButtonText = "Got it",
            DefaultButton = ContentDialogButton.Close,
        };
        // WinUI's default is 548 px with 24 px padding, narrower than the cards and their buttons
        dialog.Resources["ContentDialogMaxWidth"] = 680.0;

        var index = 0;
        void Render() => dialog.Content = BuildPage(dialog, pages, index, i =>
        {
            index = i;
            Render();
        });

        Render();
        _open = true;
        try
        {
            await dialog.ShowAsync();
        }
        finally
        {
            _open = false;
        }
    }

    private static UIElement BuildPage(ContentDialog dialog, IReadOnlyList<WhatsNewRelease> pages, int index, Action<int> goTo)
    {
        var release = pages[index];
        var root = new StackPanel { Spacing = 10, Width = 600 };
        root.Children.Add(new TextBlock
        {
            Text = $"Version {release.Version}",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("Cat518Green"),
        });

        var items = new StackPanel { Spacing = 8 };
        foreach (var item in release.Items)
        {
            items.Children.Add(BuildItem(dialog, item));
        }

        root.Children.Add(new ScrollViewer
        {
            Content = items,
            // Five changes of two or three lines fit on a 1080p screen; more scroll
            MaxHeight = 560,
            // Room for the scrollbar, which would otherwise sit over the cards' buttons
            Padding = new Thickness(0, 0, 14, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });

        if (pages.Count > 1)
        {
            var nav = new Grid();
            nav.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            nav.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            nav.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var back = new Button { Content = "Back", IsEnabled = index > 0 };
            back.Click += (_, _) => goTo(index - 1);
            var next = new Button { Content = "Next", IsEnabled = index < pages.Count - 1 };
            next.Click += (_, _) => goTo(index + 1);
            var where = new TextBlock
            {
                Text = $"{index + 1} of {pages.Count}",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brush("Neon518TextDimColor"),
            };
            Grid.SetColumn(where, 1);
            Grid.SetColumn(next, 2);
            nav.Children.Add(back);
            nav.Children.Add(where);
            nav.Children.Add(next);
            root.Children.Add(nav);
        }

        return root;
    }

    private static UIElement BuildItem(ContentDialog dialog, WhatsNewItem item)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock
        {
            Text = item.Title,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.WrapWholeWords,
        });
        text.Children.Add(new TextBlock
        {
            Text = item.Text,
            TextWrapping = TextWrapping.WrapWholeWords,
            Foreground = Brush("Neon518TextDimColor"),
        });
        grid.Children.Add(text);

        if (!string.IsNullOrEmpty(item.Target) && IsAvailable(item.Target))
        {
            var go = new Button { Content = "Take me there", VerticalAlignment = VerticalAlignment.Center };
            go.Click += (_, _) =>
            {
                dialog.Hide();
                TakeMeThere(item.Target);
            };
            Grid.SetColumn(go, 1);
            grid.Children.Add(go);
        }

        return new Border
        {
            Child = grid,
            Padding = new Thickness(12, 10, 12, 10),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = Brush("Cat518GreenEdge"),
            Background = Brush("Cat518GreenTint"),
        };
    }

    private static void TakeMeThere(string target)
    {
        var navigation = App.GetService<INavigationService>();
        switch (target)
        {
            case WhatsNew.TargetUpdates:
                // Settings itself scrolls to the Updates card and lights it up (OnNavigatedTo).
                // Already there with the same parameter, the frame doesn't navigate again.
                if (!navigation.NavigateTo(typeof(SettingsViewModel).FullName!, WhatsNew.TargetUpdates)
                    && navigation.Frame?.Content is SettingsPage settings)
                {
                    settings.RevealUpdates();
                }

                break;
            case WhatsNew.TargetDreamAirEyes:
                navigation.NavigateTo(typeof(DreamAirEyesViewModel).FullName!);
                break;
        }
    }

    // NeonTheme.xaml's keys: brushes (Cat518*) and colours (Neon518*Color) both work
    private static Brush? Brush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value)
            ? value switch
            {
                Brush brush => brush,
                Windows.UI.Color color => new SolidColorBrush(color),
                _ => null,
            }
            : null;
}
