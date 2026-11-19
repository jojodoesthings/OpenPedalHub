using System.ComponentModel;
using System.Windows;
using System.Windows.Media;

namespace OpenPedalHub;

public class PedalViewModel : INotifyPropertyChanged
{
    double _low, _high, _gamma = 1, _input, _calRest, _calFull = 1, _calMeter;
    bool _invert, _manual = true, _live;
    int _axisUsage;
    string _axisName = "", _calNote = "";
    Point[]? _points;

    public PedalViewModel(string key, string name, string subtitle, Color accent, Color accent2)
    {
        Key = key; Name = name; Subtitle = subtitle;
        AccentColor = accent;
        Accent2Color = accent2;
        Accent = Frozen(new SolidColorBrush(accent));
        Accent2 = Frozen(new SolidColorBrush(accent2));
        AccentGradient = Frozen(new LinearGradientBrush(accent, accent2, 0));
        HeaderBrush = Frozen(new LinearGradientBrush(accent, accent2, 90));
    }

    /// <summary>Top-to-bottom version of the pedal's colours, for headers and tabs.</summary>
    public LinearGradientBrush HeaderBrush { get; }

    bool _overlayOpen;
    /// <summary>Whether this pedal's tuning panel is open in the game overlay.</summary>
    public bool OverlayOpen { get => _overlayOpen; set { if (_overlayOpen != value) { _overlayOpen = value; Raise(nameof(OverlayOpen)); } } }

    static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    public string Key { get; }
    public string Name { get; }
    public string Subtitle { get; }
    public Color AccentColor { get; }
    public Color Accent2Color { get; }
    public SolidColorBrush Accent { get; }
    public SolidColorBrush Accent2 { get; }
    public LinearGradientBrush AccentGradient { get; }
    /// <summary>Accent for small text: the deeper shade on light panels, the brighter one on dark.</summary>
    public SolidColorBrush AccentText => Theme.IsDark ? Accent : Accent2;

    // ---- The curve (belongs to the active profile) ----

    // Dead zones are kept to 0.1 % and gamma to 0.01, whether dragged or typed.
    public double Low { get => _low; set { _low = Math.Clamp(Math.Round(value, 1), 0, 40); Raise(); } }
    public double High { get => _high; set { _high = Math.Clamp(Math.Round(value, 1), 0, 40); Raise(); } }
    public double Gamma { get => _gamma; set { _gamma = Math.Clamp(Math.Round(value, 2), 0.3, 3); Raise(); } }

    /// <summary>Custom curve points, or null to use gamma.</summary>
    public Point[]? Points { get => _points; set { _points = value is { Length: > 0 } ? value : null; Raise(); } }

    /// <summary>Switching custom on starts from the current gamma shape, so the curve does not jump.</summary>
    public bool Custom
    {
        get => _points is not null;
        set
        {
            if (value == Custom) return;
            Points = value ? new[] { 0.25, 0.5, 0.75 }.Select(t => new Point(t, Math.Pow(t, Gamma))).ToArray() : null;
        }
    }
    public bool UsesGamma => !Custom;

    public string CurveHint =>
        Custom ? "Drag the points  ·  double-click to add  ·  right-click to remove"
        : Gamma > 1.005 ? "Gentler at the start of travel, steeper near the end"
        : Gamma < 0.995 ? "Sharper at the start of travel, flatter near the end"
        : "Linear: output follows the pedal directly";

    // ---- The hardware (shared by all profiles) ----

    /// <summary>On if the axis reads full when the pedal is released.</summary>
    public bool Invert { get => _invert; set { _invert = value; Raise(); } }
    /// <summary>HID usage of the pedal axis feeding this card. 0 = none.</summary>
    public int AxisUsage { get => _axisUsage; set { _axisUsage = value; Raise(); } }
    public string AxisName { get => _axisName; set { _axisName = value; Raise(); } }
    /// <summary>Calibration: where the pedal really rests and really bottoms out, as fractions of the axis range.</summary>
    public double CalRest { get => _calRest; set { _calRest = value; Raise(); } }
    public double CalFull { get => _calFull; set { _calFull = value; Raise(); } }
    public bool Calibrated => CalRest > 0.0005 || CalFull < 0.9995;

    /// <summary>Raw axis reading (0..1) to pedal travel (0 = at rest, 1 = fully pressed).</summary>
    public double Travel(double raw)
    {
        double v = Invert ? 1 - raw : raw;
        return Math.Clamp((v - CalRest) / Math.Max(0.05, CalFull - CalRest), 0, 1);
    }

    // ---- Live state ----

    /// <summary>Pedal travel in percent, 0 = at rest. Fed from the device when live, otherwise simulated.</summary>
    public double Input
    {
        get => _input;
        set
        {
            if (_input == value) return;
            _input = value;
            // Changes many times a second: refresh only what depends on it, so typing in a box is not disturbed.
            Raise(nameof(Input)); Raise(nameof(Output)); Raise(nameof(OutputText));
        }
    }

    /// <summary>True while the user drives the simulated pedal by hand.</summary>
    public bool Manual { get => _manual; set { _manual = value; Raise(); } }
    /// <summary>True while this card follows the real pedal.</summary>
    public bool IsLive { get => _live; set { _live = value; Raise(); } }
    public string AxisText => IsLive ? $"Axis {AxisName}  ·  change" : "Not hardware";

    public double Output => PedalCurve.Shape(Input / 100, Low, High, Gamma, Points) * 100;
    public string OutputText => $"{Output:0.0}%";

    // ---- Calibration screen ----

    public double CalMeter { get => _calMeter; set { if (_calMeter != value) { _calMeter = value; Raise(nameof(CalMeter)); } } }
    public string CalNote { get => _calNote; set { if (_calNote != value) { _calNote = value; Raise(nameof(CalNote)); } } }

    /// <summary>
    /// Sets the curve directly, without rounding, so animated transitions stay smooth.
    /// Deliberately leaves the hardware settings alone: profiles and resets must not change them.
    /// </summary>
    public void Apply(double low, double high, double gamma)
    {
        _low = low; _high = high; _gamma = gamma;
        Raise();
    }

    /// <summary>Re-reads everything, e.g. after the theme changed.</summary>
    public void Refresh() => Raise();

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>With no name, refreshes everything bound to this pedal.</summary>
    void Raise(string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
