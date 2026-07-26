using VRCFaceTracking.Core.Contracts.Services;
using VRCFaceTracking.Core.Services;
using VRCFaceTracking.Helpers;

namespace VRCFaceTracking;

public sealed partial class MainWindow : WindowEx
{
    public MainWindow()
    {
        InitializeComponent();
        
        AppWindow.Closing += async (window, args) =>
        {
            args.Cancel = true;
            await App.GetService<IMainService>().Teardown();

            // Reaching here means a real user-initiated shutdown, so clear the run
            // marker - otherwise the next start would report this as a hard crash.
            try
            {
                App.GetService<CrashReportService>().MarkCleanExit();
            }
            catch (Exception)
            {
                // Never block closing over crash bookkeeping
            }

            Close();
        };

        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets/WindowIcon.ico"));
        Content = null;
        Title = "AppDisplayName".GetLocalized();
    }
}
