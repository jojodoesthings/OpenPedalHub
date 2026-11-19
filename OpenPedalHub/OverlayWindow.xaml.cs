using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace OpenPedalHub;

/// <summary>
/// The in-game overlay: a slim bar that stays on top of other windows, with a tab per pedal that opens a small
/// tuning panel. It shows the same pedals as the main window, so a change in either place is a change in both.
/// It never takes keyboard focus, so the game underneath keeps receiving your keys.
/// </summary>
public partial class OverlayWindow : Window
{
    readonly MainWindow _main;

    /// <summary>Raised after the overlay has been dragged to a new position.</summary>
    public event Action? Moved;

    public OverlayWindow(MainWindow main, IEnumerable<PedalViewModel> pedals)
    {
        _main = main;
        InitializeComponent();
        var list = pedals.ToList();
        Tabs.ItemsSource = list;
        Panels.ItemsSource = list;
        _main.ProfileChanged += ShowProfile;
        Closed += (_, _) => _main.ProfileChanged -= ShowProfile;
        ShowProfile();
    }

    /// <summary>Locked hides the drag handle, so the bar cannot be moved by accident.</summary>
    public bool Locked { set => Grip.Visibility = value ? Visibility.Collapsed : Visibility.Visible; }

    void ShowProfile() => ProfileText.Text = _main.ActiveProfileName;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Never become the active window (the game keeps focus) and stay out of Alt+Tab.
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
    }

    void Prev_Click(object sender, RoutedEventArgs e) => _main.SwitchProfile(-1);
    void Next_Click(object sender, RoutedEventArgs e) => _main.SwitchProfile(+1);
    void OpenApp_Click(object sender, RoutedEventArgs e) => _main.ShowFromTray();

    void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is PedalViewModel p) _main.ResetCurve(p);
    }

    // ---- Dragging by the handle ----
    // Done by hand rather than with DragMove, which expects the window to be the active one.

    bool _dragging;
    Point _grab;

    void Grip_Down(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        _grab = e.GetPosition(this);
        Grip.CaptureMouse();
    }

    void Grip_Move(object sender, MouseEventArgs e)
    {
        if (!_dragging || PresentationSource.FromVisual(this)?.CompositionTarget is not { } target) return;
        // Screen pixels to WPF units, so it tracks the pointer correctly on scaled displays.
        Point at = target.TransformFromDevice.Transform(PointToScreen(e.GetPosition(this)));
        Left = at.X - _grab.X;
        Top = at.Y - _grab.Y;
    }

    void Grip_Up(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        Grip.ReleaseMouseCapture();
        Moved?.Invoke();
    }

    const int GWL_EXSTYLE = -20;
    const long WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
}
