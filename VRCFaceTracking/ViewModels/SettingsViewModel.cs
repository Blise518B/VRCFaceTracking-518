using System.Windows.Input;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Microsoft.UI.Xaml;

using VRCFaceTracking.Contracts.Services;
using VRCFaceTracking.Core.Services;
using VRCFaceTracking.Models;
using VRCFaceTracking.Services;

namespace VRCFaceTracking.ViewModels;

public partial class SettingsViewModel : ObservableRecipient
{
    private readonly IThemeSelectorService _themeSelectorService;
    [ObservableProperty] private ElementTheme _elementTheme;
    [ObservableProperty] private List<GithubContributor> _contributors;
    
    public ICommand SwitchThemeCommand
    {
        get;
    }

    private GithubService GithubService
    {
        get;
        set;
    }
    
    private OpenVRService OpenVRService
    {
        get;
    }
    
    public bool AutoStart
    {
        get => OpenVRService.AutoStart;
        set
        {
            OpenVRService.AutoStart = value;
            OnPropertyChanged();
        }
    }

    public bool IsOpenVREnabled => OpenVRService.IsInitialized;

    public AutoRestartService AutoRestart
    {
        get;
    }

    public CrashReportService CrashReports
    {
        get;
    }

    /// <summary>Newest-first list of crash report filenames, for the settings page.</summary>
    public List<string> CrashReportNames =>
        CrashReports.RecentReports().Select(f => $"{f.LastWriteTime:yyyy-MM-dd HH:mm}   {f.Name}").ToList();

    public bool HasCrashReports => CrashReports.RecentReports().Any();

    private async void LoadContributors()
    {
        Contributors = await GithubService.GetContributors("benaclejames/VRCFaceTracking");
    }

    public SettingsViewModel(IThemeSelectorService themeSelectorService, GithubService githubService, OpenVRService openVRService, AutoRestartService autoRestartService, CrashReportService crashReportService)
    {
        _themeSelectorService = themeSelectorService;
        GithubService = githubService;
        OpenVRService = openVRService;
        AutoRestart = autoRestartService;
        CrashReports = crashReportService;

        _elementTheme = _themeSelectorService.Theme;

        SwitchThemeCommand = new RelayCommand<ElementTheme>(
            async (param) =>
            {
                if (ElementTheme != param)
                {
                    ElementTheme = param;
                    await _themeSelectorService.SetThemeAsync(param);
                }
            });

        OpenVRService.InitIfNotAlready();
        LoadContributors();
    }
}
