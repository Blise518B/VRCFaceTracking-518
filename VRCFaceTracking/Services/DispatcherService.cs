using Microsoft.UI.Dispatching;
using VRCFaceTracking.Core.Contracts.Services;

namespace VRCFaceTracking.Services;

// Simple service to invoke actions on the UI thread from the Core project.
public class DispatcherService : IDispatcherService
{
    /// <summary>
    /// The UI thread's queue, taken on the UI thread first thing in App.OnLaunched. Callers
    /// run on background threads (the OSC Router client, avatar changes from OSC), and the
    /// first read of App.MainWindow creates the window: done from a background thread that
    /// fails with RPC_E_WRONG_THREAD and leaves the app without a window.
    /// </summary>
    public static DispatcherQueue? UiQueue { get; set; }

    public void Run(Action action) => (UiQueue ?? App.MainWindow.DispatcherQueue)?.TryEnqueue(action.Invoke);
}
