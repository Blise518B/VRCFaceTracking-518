using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VRCFaceTracking.Services;

namespace VRCFaceTracking.Helpers;

/// <summary>
/// Colours Output page lines by log level through template choice alone, so a line is one
/// TextBlock with no behaviours, triggers or converters attached. Both overloads are
/// overridden: ItemsRepeater asks without a container, list controls ask with one.
/// </summary>
public class LogLineTemplateSelector : DataTemplateSelector
{
    public DataTemplate PlainTemplate { get; set; } = null!;
    public DataTemplate WarningTemplate { get; set; } = null!;
    public DataTemplate ErrorTemplate { get; set; } = null!;
    public DataTemplate CriticalTemplate { get; set; } = null!;

    protected override DataTemplate SelectTemplateCore(object item) =>
        item is LogLine line
            ? line.Level switch
            {
                LogLevel.Warning => WarningTemplate,
                LogLevel.Error => ErrorTemplate,
                LogLevel.Critical => CriticalTemplate,
                _ => PlainTemplate,
            }
            : PlainTemplate;

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container) =>
        SelectTemplateCore(item);
}
