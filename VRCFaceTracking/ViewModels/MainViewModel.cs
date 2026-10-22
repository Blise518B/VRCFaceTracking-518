using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using VRCFaceTracking.Core.Contracts;
using VRCFaceTracking.Core.Contracts.Services;
using VRCFaceTracking.Core.OSC;
using VRCFaceTracking.Core.Services;

namespace VRCFaceTracking.ViewModels;

public partial class MainViewModel : ObservableRecipient
{
    public ILibManager LibManager { get; }
    public OscQueryService ParameterOutputService { get; }
    public OscRecvService OscRecvService { get; }
    public OscSendService OscSendService { get; }
    public IOscTarget OscTarget { get; }

    private int _messagesRecvd;
    [ObservableProperty] private int _messagesInPerSec;

    private int _messagesSent;
    [ObservableProperty] private int _messagesOutPerSec;

    [ObservableProperty] private bool _noModulesInstalled;
    
    [ObservableProperty] private bool _oscWasDisabled;

    private DispatcherTimer msgCounterTimer;

    public MainViewModel(
        ILibManager libManager,
        OscQueryService parameterOutputService,
        IModuleDataService moduleDataService,
        IOscTarget oscTarget,
        OscRecvService oscRecvService,
        OscSendService oscSendService
        )
    {
        //Services
        LibManager = libManager;
        ParameterOutputService = parameterOutputService;
        OscTarget = oscTarget;
        OscRecvService = oscRecvService;
        OscSendService = oscSendService;
        
        // Modules
        var installedNewModules = moduleDataService.GetInstalledModules();
        var installedLegacyModules = moduleDataService.GetLegacyModules().Count();
        NoModulesInstalled = !installedNewModules.Any() && installedLegacyModules == 0;
        
        // Message Timer - counting runs only while the Home page shows (Start/Stop)
        msgCounterTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        msgCounterTimer.Tick += (_, _) =>
        {
            MessagesInPerSec = _messagesRecvd;
            _messagesRecvd = 0;

            MessagesOutPerSec = _messagesSent;
            _messagesSent = 0;
        };
    }

    /// <summary>
    /// Called by MainPage on Loaded. This view model is transient - a new one per visit to
    /// Home - and it used to subscribe and start its timer in the constructor, with only a
    /// finalizer to undo it. The singleton services' handlers and the running timer kept
    /// every one of them alive, so each visit added a permanent timer and another handler
    /// on the OSC send path.
    /// </summary>
    public void Start()
    {
        OscRecvService.OnMessageReceived -= MessageReceived;
        OscRecvService.OnMessageReceived += MessageReceived;
        OscSendService.OnMessagesDispatched -= MessageDispatched;
        OscSendService.OnMessagesDispatched += MessageDispatched;
        msgCounterTimer.Start();
    }

    /// <summary>Called by MainPage on Unloaded; see <see cref="Start"/>.</summary>
    public void Stop()
    {
        OscRecvService.OnMessageReceived -= MessageReceived;
        OscSendService.OnMessagesDispatched -= MessageDispatched;
        msgCounterTimer.Stop();
    }

    private void MessageReceived(OscMessage msg) => _messagesRecvd++;
    private void MessageDispatched(int msgCount) => _messagesSent += msgCount;
}
