using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;

namespace VRCFaceTracking.Core.Params.Data.Mutation;

public enum MutationPropertyType
{
    CheckBox,
    Slider,
    TextBox
}

public interface IMutationComponent
{
    public string Name { get; }
}

public class MutationProperty : IMutationComponent, INotifyPropertyChanged
{
    private object _value;
    private readonly Action<object> _updateField;

    public MutationProperty(string name, 
                            object value, 
                            MutationPropertyType type, 
                            Action<object> updateField, 
                            float min, 
                            float max)
    {
        Name = name;
        _value = value;
        Type = type;
        _updateField = updateField;
        Min = min;
        Max = max;
    }

    public float Min { get; }
    public float Max { get; }

    public object Value
    {
        get => _value;
        set
        {
            if (_value != value)
            {
                _value = value;
                OnPropertyChanged(nameof(Value));
                _updateField?.Invoke(_value);
            }
        }
    }

    public string Name { get; }
    public MutationPropertyType Type { get; set; }

    public event PropertyChangedEventHandler PropertyChanged;

    protected virtual void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public class MutationRangeProperty : IMutationComponent, INotifyPropertyChanged
{
    private float _item1;
    private float _item2;
    private readonly Action<(float, float)> _updateField;

    public MutationRangeProperty(string name, 
                                 float item1, 
                                 float item2, 
                                 Action<(float, float)> updateField, 
                                 float min, 
                                 float max)
    {
        Name = name;
        _item1 = item1;
        _item2 = item2;
        _updateField = updateField;
        Min = min;
        Max = max;
    }

    public float Min { get; }
    public float Max { get; }

    public float Item1
    {
        get => _item1;
        set
        {
            if (_item1 != value)
            {
                _item1 = value;
                OnPropertyChanged(nameof(Item1));
                _updateField?.Invoke((_item1, _item2));
            }
        }
    }

    public float Item2
    {
        get => _item2;
        set
        {
            if (_item2 != value)
            {
                _item2 = value;
                OnPropertyChanged(nameof(Item2));
                _updateField?.Invoke((_item1, _item2));
            }
        }
    }

    public string Name { get; }

    public event PropertyChangedEventHandler PropertyChanged;

    protected virtual void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>
/// A single per-expression calibration row: enable toggle, floor/ceiling range and a
/// sensitivity curve. Wraps a <see cref="ShapeRangeConfig"/> so UI edits land directly
/// in the owning mutation's saved configuration.
/// </summary>
public class MutationExpressionRow : IMutationComponent, INotifyPropertyChanged
{
    private readonly ShapeRangeConfig _config;

    public MutationExpressionRow(string name, ShapeRangeConfig config)
    {
        Name = name;
        _config = config;
    }

    public string Name { get; }

    public bool Enabled
    {
        get => _config.Enabled;
        set
        {
            if (_config.Enabled != value)
            {
                _config.Enabled = value;
                OnPropertyChanged(nameof(Enabled));
            }
        }
    }

    public float Floor
    {
        get => _config.Floor;
        set
        {
            if (_config.Floor != value)
            {
                _config.Floor = value;
                OnPropertyChanged(nameof(Floor));
                OnPropertyChanged(nameof(FloorText));
            }
        }
    }

    public float Ceil
    {
        get => _config.Ceil;
        set
        {
            if (_config.Ceil != value)
            {
                _config.Ceil = value;
                OnPropertyChanged(nameof(Ceil));
                OnPropertyChanged(nameof(CeilText));
            }
        }
    }

    public float Curve
    {
        get => _config.Curve;
        set
        {
            if (_config.Curve != value)
            {
                _config.Curve = value;
                OnPropertyChanged(nameof(Curve));
                OnPropertyChanged(nameof(CurveText));
            }
        }
    }

    public string FloorText => _config.Floor.ToString("0.00");
    public string CeilText => _config.Ceil.ToString("0.00");
    public string CurveText => _config.Curve.ToString("0.00");

    /// <summary>
    /// Called by the owning mutation after captures/resets change the underlying config
    /// outside of the UI, so bound controls refresh.
    /// </summary>
    public void NotifyAllChanged()
    {
        OnPropertyChanged(nameof(Enabled));
        OnPropertyChanged(nameof(Floor));
        OnPropertyChanged(nameof(Ceil));
        OnPropertyChanged(nameof(Curve));
        OnPropertyChanged(nameof(FloorText));
        OnPropertyChanged(nameof(CeilText));
        OnPropertyChanged(nameof(CurveText));
    }

    public event PropertyChangedEventHandler PropertyChanged;

    protected virtual void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>
/// Calibration settings for a single unified expression shape.
/// </summary>
public class ShapeRangeConfig
{
    /// <summary> Whether remapping is applied to this shape at all. </summary>
    public bool Enabled = true;
    /// <summary> Raw tracking value that maps to 0 (values below are clamped). </summary>
    public float Floor = 0f;
    /// <summary> Raw tracking value that maps to 1 (values above are clamped). </summary>
    public float Ceil = 1f;
    /// <summary> Response curve exponent. 1 = linear, &lt;1 = more sensitive at the low end, &gt;1 = less sensitive at the low end. </summary>
    public float Curve = 1f;
}

public class MutationAction : IMutationComponent, ICommand
{
    public string Name { get; }
    private readonly Action _execute;

    public MutationAction(string name, Action execute)
    {
        Name = name;
        _execute = execute;
    }

    public event EventHandler CanExecuteChanged;

    public bool CanExecute(object parameter) => true; // Adjust logic as needed

    public void Execute(object parameter) => Task.Run(() => _execute());
}
