using System.ComponentModel;

namespace VRCFaceTracking;

// Internal version of ModuleMetadata, done to not break compat
public class ModuleMetadataInternal : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler PropertyChanged;

    public ModuleMetadata.ActiveChange OnActiveChange;

    public List<Stream> StaticImages { get; set; }
    public string Name { get; set; }
    private bool _active;

    public bool Active
    {
        get => _active;
        set
        {
            _active = value;
            OnActiveChange?.Invoke(value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Active)));
        }
    }

    //Temporary for the menu display
    private bool _usingEye;
    private bool _usingExpression;


    public bool UsingEye
    {
        get => _usingEye;
        set
        {
            _usingEye = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UsingEye)));
        }
    }

    public bool UsingExpression
    {
        get => _usingExpression;
        set
        {
            _usingExpression = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UsingExpression)));
        }
    }

    private bool _isDormant;
    private string _statusNote = string.Empty;

    /// <summary>
    /// Installed but deliberately not started this session, because source detection found
    /// nothing for it. Shown greyed out rather than hidden, so it's obvious the module is
    /// present and simply idle rather than missing.
    /// </summary>
    public bool IsDormant
    {
        get => _isDormant;
        set
        {
            _isDormant = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDormant)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRunning)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CardOpacity)));
        }
    }

    /// <summary>
    /// Dll file name this card was built from, for dormant cards only.
    ///
    /// Names cannot be used to pair a dormant card with its running module: the dormant one
    /// is named from the module manifest ("ETVR Tracking Module") while the module reports
    /// something else at runtime ("ETVR Eye Tracking module"). Matching on those produced a
    /// module listed as in-use and not-in-use simultaneously.
    /// </summary>
    public string SourceDll { get; set; }

    /// <summary>Why this module is dormant, shown on its card.</summary>
    public string StatusNote
    {
        get => _statusNote;
        set
        {
            _statusNote = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusNote)));
        }
    }

    public bool IsRunning => !_isDormant;

    /// <summary>Dimmed when dormant. Bound straight to the card's Opacity.</summary>
    public double CardOpacity => _isDormant ? 0.45 : 1.0;
}