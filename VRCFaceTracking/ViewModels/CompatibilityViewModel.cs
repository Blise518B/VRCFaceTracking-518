using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using VRCFaceTracking.Core.Contracts.Services;
using VRCFaceTracking.Core.Params.Data;
using VRCFaceTracking.Core.Params.Data.Mutation;

namespace VRCFaceTracking.ViewModels;

/// <summary>
/// Friendly front-end for the Tracking Compatibility mutation: pick which
/// ecosystem your avatar was tuned for, run whatever hardware you like.
/// </summary>
public partial class CompatibilityViewModel : ObservableRecipient
{
    private readonly UnifiedTrackingMutator _mutator;
    private readonly ILibManager _libManager;
    private readonly DispatcherQueue? _dispatcher;
    private DispatcherQueueTimer? _timer;

    private TrackingCompatibility? _mutation;
    private bool _loading;

    [ObservableProperty] private int _profileIndex;
    [ObservableProperty] private string _sourceLine = "-";
    [ObservableProperty] private string _statusLine = "-";
    [ObservableProperty] private bool _mutationFound;
    [ObservableProperty] private bool _mutatorMasterOff;

    [ObservableProperty] private double _squintStart = 0.55;
    [ObservableProperty] private double _squintFull = 0.15;
    [ObservableProperty] private double _opennessCoupling = 0.35;
    [ObservableProperty] private double _cheekFromSmile = 0.5;
    [ObservableProperty] private bool _qpConstantPupil = true;
    [ObservableProperty] private bool _wideFromLid;
    [ObservableProperty] private double _wideStart = 0.9;

    public CompatibilityViewModel()
    {
        _mutator = App.GetService<UnifiedTrackingMutator>();
        _libManager = App.GetService<ILibManager>();
        _dispatcher = DispatcherQueue.GetForCurrentThread();
    }

    public void Start()
    {
        if (_dispatcher is null || _timer is not null)
        {
            return;
        }

        _timer = _dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Tick();
    }

    public void Stop()
    {
        _timer?.Stop();
        _timer = null;
    }

    private void Tick()
    {
        if (_mutation is null)
        {
            // Mutations load asynchronously at startup; latch on when ready.
            _mutation = _mutator._mutations.OfType<TrackingCompatibility>().FirstOrDefault();
            if (_mutation is not null)
            {
                PullFromMutation();
            }
        }

        MutationFound = _mutation is not null;
        MutatorMasterOff = !_mutator.Enabled;

        var eyeOwner = _libManager.LoadedModulesMetadata.FirstOrDefault(m => m.UsingEye)?.Name;
        var exprOwner = _libManager.LoadedModulesMetadata.FirstOrDefault(m => m.UsingExpression)?.Name;
        SourceLine = $"Detected hardware:  eyes = {eyeOwner ?? "none"} ({TrackingCompatibility.DialectOf(eyeOwner)})   |   " +
                     $"face = {exprOwner ?? "none"} ({TrackingCompatibility.DialectOf(exprOwner)})";

        StatusLine = !MutationFound ? "Waiting for the mutation system to finish loading..."
            : MutatorMasterOff ? "Tracking Mutations master switch is OFF - no translation (or curves) can run."
            : ProfileIndex switch
            {
                TrackingCompatibility.ProfileQuestPro => "Translating to the Quest Pro dialect - QP-tuned avatars behave as on a Quest Pro.",
                TrackingCompatibility.ProfileVive => "Translating to the Vive dialect - SRanipal-tuned avatars behave as on a Vive.",
                _ => "Passthrough - avatars see the raw native dialect of whatever hardware is running.",
            };
    }

    private void PullFromMutation()
    {
        _loading = true;
        ProfileIndex = Math.Clamp(_mutation!.profile, 0, 2);
        SquintStart = _mutation.squintStart;
        SquintFull = _mutation.squintFull;
        OpennessCoupling = _mutation.opennessCoupling;
        CheekFromSmile = _mutation.cheekFromSmile;
        QpConstantPupil = _mutation.qpConstantPupil;
        WideFromLid = _mutation.wideFromLid;
        WideStart = _mutation.wideStart;
        _loading = false;
    }

    private void Push()
    {
        if (_loading || _mutation is null)
        {
            return;
        }

        _mutation.profile = ProfileIndex;
        _mutation.squintStart = (float)SquintStart;
        _mutation.squintFull = (float)SquintFull;
        _mutation.opennessCoupling = (float)OpennessCoupling;
        _mutation.cheekFromSmile = (float)CheekFromSmile;
        _mutation.qpConstantPupil = QpConstantPupil;
        _mutation.wideFromLid = WideFromLid;
        _mutation.wideStart = (float)WideStart;
        _mutation.IsActive = ProfileIndex != TrackingCompatibility.ProfileOff;

        if (_mutation.IsActive && !_mutator.Enabled)
        {
            // A translation the user just switched on must not silently do
            // nothing because the master toggle was off.
            _mutator.Enabled = true;
        }

        _ = _mutation.Save();
        Tick();
    }

    partial void OnProfileIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsQuestPro));
        OnPropertyChanged(nameof(KnobsVisible));
        Push();
    }
    partial void OnSquintStartChanged(double value) => Push();
    partial void OnSquintFullChanged(double value) => Push();
    partial void OnOpennessCouplingChanged(double value) => Push();
    partial void OnCheekFromSmileChanged(double value) => Push();
    partial void OnQpConstantPupilChanged(bool value) => Push();
    partial void OnWideFromLidChanged(bool value) => Push();
    partial void OnWideStartChanged(double value) => Push();

    public bool IsQuestPro => ProfileIndex == TrackingCompatibility.ProfileQuestPro;
    public bool KnobsVisible => ProfileIndex != TrackingCompatibility.ProfileOff;
}
