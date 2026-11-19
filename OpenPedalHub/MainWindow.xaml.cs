using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace OpenPedalHub;

public partial class MainWindow : Window
{
    static Color Rgb(int hex) => Color.FromRgb((byte)(hex >> 16), (byte)(hex >> 8), (byte)hex);

    // Blue, red and green, like the face buttons on a gamepad; green throttle matches the app's accent.
    readonly PedalViewModel _clutch = new("clutch", "Clutch", "Left pedal", Rgb(0x4FAEF5), Rgb(0x1E72CF));
    readonly PedalViewModel _brake = new("brake", "Brake", "Middle pedal", Rgb(0xF4634F), Rgb(0xC6281C));
    readonly PedalViewModel _throttle = new("throttle", "Throttle", "Right pedal", Rgb(0x8AD03A), Rgb(0x3F8F0E));
    readonly PedalViewModel[] _pedals;
    readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary>Read-only pedal listener. See PedalReader for why it cannot write to the device.</summary>
    public PedalReader Reader { get; } = new();
    /// <summary>Feeds shaped values to vJoy. Separate from the pedals: see VJoyOutput.</summary>
    public VJoyOutput Output { get; } = new();
    /// <summary>Hides the real pedals from games only while this app is sending to the virtual controller.</summary>
    public PedalHider Hider { get; } = new();

    // Named profiles. Edits to the curves belong to the active one and are saved automatically.
    List<ProfileSettings> _profiles = [];
    int _active;

    /// <summary>The known pedal set that is plugged in and driving the app, if any. See PedalSets.</summary>
    PedalSetDefinition? _set;
    /// <summary>
    /// Which model ("VID:PID") the saved axis, reverse and calibration settings belong to. When a different
    /// model is plugged in, those are taken afresh from its definition instead.
    /// </summary>
    string? _hardwareFor;

    // Curves glide to their new values instead of jumping.
    record Glide(PedalViewModel Pedal, double Low0, double High0, double Gamma0, double Low1, double High1, double Gamma1);
    readonly List<Glide> _glides = [];
    double _glideStart;
    const double GlideSeconds = 0.45;
    bool _ready;

    string _savedJson = "";
    readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    public MainWindow()
    {
        _pedals = [_clutch, _brake, _throttle];
        var saved = Settings.Load();
        Theme.Apply(saved?.Theme == "Dark" || App.ForceDark);

        InitializeComponent();
        PedalList.ItemsSource = _pedals;
        CalList.ItemsSource = _pedals;
        _clutch.Input = 0; _brake.Input = 35; _throttle.Input = 70;
        PedalSets.Load();

        if (saved is not null)
        {
            // Settings saved before other pedal sets were supported can only be for the original one.
            _hardwareFor = saved.Device ?? (saved.Pedals.Count > 0 ? "044F:B678" : null);
            foreach (var p in _pedals)
                if (saved.Pedals.TryGetValue(p.Key, out var s))
                {
                    p.Invert = s.Invert;
                    p.AxisUsage = s.Axis;
                    if (s.CalFull - s.CalRest >= 0.05) { p.CalRest = Math.Clamp(s.CalRest, 0, 1); p.CalFull = Math.Clamp(s.CalFull, 0, 1); }
                }
            OutputToggle.IsChecked = saved.SendToGame;
            _profiles = saved.Profiles.Where(x => x is not null).ToList();
            _active = saved.Active;
            if (_profiles.Count == 0 && saved.Pedals.Count > 0)
            {
                // Settings from before profiles existed: keep that setup as the first profile.
                _profiles.Add(new ProfileSettings
                {
                    Name = "My setup",
                    Curves = saved.Pedals.ToDictionary(kv => kv.Key, kv => new CurveSettings { Low = kv.Value.Low, High = kv.Value.High, Gamma = kv.Value.Gamma }),
                });
                _profiles.AddRange(Settings.Starters());
                _active = 0;
            }
        }
        if (_profiles.Count == 0) { _profiles = Settings.Starters(); _active = 1; }
        _active = Math.Clamp(_active, 0, _profiles.Count - 1);
        LoadActive(glide: false);
        ShowProfileName(0);

        (Theme.IsDark ? ThemeDark : ThemeDefault).IsChecked = true;
        TrayToggle.IsChecked = saved?.MinimizeToTray ?? true;
        OverlayToggle.IsChecked = saved?.ShowOverlay ?? false;
        OverlayLockToggle.IsChecked = saved?.OverlayLocked ?? false;
        _overlayX = saved?.OverlayX;
        _overlayY = saved?.OverlayY;
        StartupToggle.IsChecked = Autostart.Enabled;
        // Leave _savedJson empty when profiles were just created from an old file, so the new layout gets written.
        if (saved is { Profiles.Count: > 0 }) _savedJson = Snapshot();
        _ready = true;

        _saveTimer.Tick += (_, _) => SaveIfChanged();
        _saveTimer.Start();
        Closing += (_, _) => Cleanup();
    }

    /// <summary>Puts everything back: saves settings, releases the virtual pedals, unhides the real ones. Safe to call twice.</summary>
    public void Cleanup()
    {
        SaveIfChanged();
        Output.Stop();
        Hider.Unhide();
        _tray?.Dispose();
        _tray = null;
        _overlay?.Close();
        _overlay = null;
    }

    // ---- Game overlay ----

    OverlayWindow? _overlay;
    double? _overlayX, _overlayY;

    /// <summary>Raised when the active profile or its name changes, so the overlay can show it.</summary>
    public event Action? ProfileChanged;
    public string ActiveProfileName => _profiles.Count > 0 ? _profiles[_active].Name : "";

    void Overlay_Changed(object sender, RoutedEventArgs e)
    {
        if (_ready) UpdateOverlay();
    }

    void UpdateOverlay()
    {
        bool want = OverlayToggle.IsChecked == true && !App.CheckMode;
        if (want && _overlay is null)
        {
            _overlay = new OverlayWindow(this, _pedals);
            // Last position if it is still on a screen, otherwise top centre of the main screen.
            double left = _overlayX ?? (SystemParameters.PrimaryScreenWidth - 720) / 2, top = _overlayY ?? 6;
            double minX = SystemParameters.VirtualScreenLeft, maxX = minX + SystemParameters.VirtualScreenWidth - 120;
            double minY = SystemParameters.VirtualScreenTop, maxY = minY + SystemParameters.VirtualScreenHeight - 60;
            _overlay.Left = Math.Clamp(left, minX, Math.Max(minX, maxX));
            _overlay.Top = Math.Clamp(top, minY, Math.Max(minY, maxY));
            var overlay = _overlay;
            overlay.Moved += () => { _overlayX = overlay.Left; _overlayY = overlay.Top; };
            overlay.Show();
        }
        else if (!want && _overlay is not null)
        {
            _overlay.Close();
            _overlay = null;
        }
        if (_overlay is not null) _overlay.Locked = OverlayLockToggle.IsChecked == true;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Reader.ConnectionChanged += OnConnectionChanged;
        Reader.Report += SendOutput;
        Reader.Attach(this);
        OnConnectionChanged();   // pick the pedal set from whatever is already plugged in
        if (!App.CheckMode) _tray = new TrayIcon(ShowFromTray, Close);
        // With no pedals plugged in, show the demo so the window is not static.
        if (!Reader.Connected) SweepToggle.IsChecked = true;
        UpdateMode();
        UpdateOverlay();
        // First run on a PC without vJoy: show what is needed instead of failing quietly.
        // (Not when started silently with Windows; the status line and Settings still point here.)
        if (!App.CheckMode && !App.StartedInTray && OutputToggle.IsChecked == true && !ConnectionReady) ShowConnection();
    }

    // ---- Settings file ----

    string Snapshot()
    {
        StoreActive();
        return new Settings
        {
            Pedals = _pedals.ToDictionary(p => p.Key, p => new PedalSettings
            {
                Low = Math.Round(p.Low, 2), High = Math.Round(p.High, 2), Gamma = Math.Round(p.Gamma, 2),
                Invert = p.Invert, Axis = p.AxisUsage, CalRest = Math.Round(p.CalRest, 4), CalFull = Math.Round(p.CalFull, 4),
            }),
            SendToGame = OutputToggle.IsChecked == true,
            Device = _hardwareFor,
            Profiles = _profiles,
            Active = _active,
            Theme = Theme.IsDark ? "Dark" : "Default",
            MinimizeToTray = TrayToggle.IsChecked == true,
            ShowOverlay = OverlayToggle.IsChecked == true,
            OverlayLocked = OverlayLockToggle.IsChecked == true,
            OverlayX = _overlayX is { } x ? Math.Round(x) : null,
            OverlayY = _overlayY is { } y ? Math.Round(y) : null,
        }.ToJson();
    }

    /// <summary>Developer aid for screenshots: put the window into a state worth capturing. Returns what to capture.</summary>
    public FrameworkElement DebugShow(string what)
    {
        if (what == "custom") _brake.Custom = true;
        else if (what == "calibration") Calibrate_Click(this, new RoutedEventArgs());
        else if (what == "connection") ShowConnection();
        else if (what == "setup") { SetupPedals_Click(this, new RoutedEventArgs()); ShowSetupStep(2); }
        else if (what == "settings") { SettingsPopup.IsOpen = true; return (FrameworkElement)SettingsPopup.Child; }
        else if (what == "overlay")
        {
            var overlay = new OverlayWindow(this, _pedals) { Left = 40, Top = 40 };
            _brake.OverlayOpen = _throttle.OverlayOpen = true;
            overlay.Show();
            return (FrameworkElement)overlay.Content;
        }
        return (FrameworkElement)Content;
    }

    void SaveIfChanged()
    {
        if (App.CheckMode) return;       // screenshot / probe runs must not alter the user's settings
        if (_glides.Count > 0) return;   // wait for the animation to settle
        string json = Snapshot();
        if (json == _savedJson) return;
        _savedJson = json;
        Settings.Save(json);
    }

    // ---- Settings menu ----

    void Settings_Click(object sender, RoutedEventArgs e) => SettingsPopup.IsOpen = true;

    void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        Theme.Apply(ThemeDark.IsChecked == true);
        foreach (var p in _pedals) p.Refresh();
        UpdateMode();   // the status dot takes its colour from the theme
    }

    void Tray_Changed(object sender, RoutedEventArgs e) { }   // read when minimising; saved with the rest

    void Startup_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        try { Autostart.Enabled = StartupToggle.IsChecked == true; }
        catch { /* registry refused: show the real state again below */ }
        bool actual = Autostart.Enabled;
        if (StartupToggle.IsChecked != actual) StartupToggle.IsChecked = actual;
    }

    // ---- Notification area (tray) ----

    TrayIcon? _tray;

    /// <summary>With the setting on, minimising sends the window to the tray; the app keeps working either way.</summary>
    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState != WindowState.Minimized || _tray is null || TrayToggle.IsChecked != true) return;
        Hide();
        _tray.HintOnce();
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    // ---- Output to the virtual controller ----

    void Output_Changed(object sender, RoutedEventArgs e)
    {
        if (_ready && StatusText is not null) UpdateMode();
    }

    // Runs on every pedal report, so the game gets values at the pedals' own rate rather than the screen's.
    void SendOutput()
    {
        if (!Output.Active) return;
        bool live = Reader.Connected && SweepToggle.IsChecked != true;
        Output.Send(live ? Shaped(_clutch) : 0, live ? Shaped(_brake) : 0, live ? Shaped(_throttle) : 0);
    }

    double Shaped(PedalViewModel p)
    {
        if (p.AxisUsage == 0 || !Reader.TryGet(p.AxisUsage, out double raw)) return 0;
        return PedalCurve.Shape(p.Travel(raw), p.Low, p.High, p.Gamma, p.Points);
    }

    // ---- Live pedal input (read-only) ----

    void OnConnectionChanged()
    {
        // Automatic detection: the first plugged-in controller that matches a known pedal set drives the app.
        var known = Reader.Devices.Where(d => PedalSets.Find(d.Vid, d.Pid) is not null).ToList();
        // (App.PreferDevice is a developer aid for testing with a particular controller.)
        var device = known.FirstOrDefault(d => d.Id == App.PreferDevice) ?? known.FirstOrDefault();
        Reader.Active = device;
        _set = device is null ? null : PedalSets.Find(device.Vid, device.Pid);

        if (device is not null && _set is not null)
        {
            if (_hardwareFor != device.Id)
            {
                // A model the saved settings were not made for: take axis, direction and range from its definition.
                foreach (var p in _pedals)
                {
                    var a = _set.Axes.GetValueOrDefault(p.Key);
                    p.AxisUsage = a?.Usage ?? 0;
                    p.Invert = a?.Reversed ?? false;
                    bool measured = a is { Rest: not null, Full: not null } && a.Full - a.Rest >= 0.05;
                    p.CalRest = measured ? a!.Rest!.Value : 0;
                    p.CalFull = measured ? a!.Full!.Value : 1;
                }
                _hardwareFor = device.Id;
            }
            foreach (var p in _pedals)
                p.AxisName = device.Axes.FirstOrDefault(x => x.Code == p.AxisUsage)?.Name ?? "none";
        }
        UpdateMode();
    }

    void UpdateMode()
    {
        bool sweep = SweepToggle.IsChecked == true;
        bool live = Reader.Connected && !sweep;
        foreach (var p in _pedals)
        {
            p.IsLive = live && p.AxisUsage != 0;
            p.Manual = !sweep && !p.IsLive;
        }
        CompositionTarget.Rendering -= Sweep_Tick;
        CompositionTarget.Rendering -= Live_Tick;
        if (sweep) CompositionTarget.Rendering += Sweep_Tick;
        else if (live) CompositionTarget.Rendering += Live_Tick;

        if (OutputToggle.IsChecked == true) Output.Start(); else Output.Stop();
        SendOutput();   // pushes "released" straight away when live input pauses

        // Games should see exactly one set of pedals: the virtual ones while we feed them, otherwise the real ones.
        if (Reader.Connected && !Hider.Hidden) Hider.Adopt(Reader.InstancePath);
        // A set that shares its USB device with a wheel base is never hidden, or the wheel would vanish too.
        if (Output.Active && Reader.Connected && (_set?.HideFromGames ?? false)) Hider.Hide(Reader.InstancePath);
        else Hider.Unhide();

        StatusDot.Fill = Theme.Brush(Reader.Connected ? "Brand" : "Muted");
        string name = _set?.Name ?? "Pedals";
        string pedals = !Reader.Connected
            ? (Reader.Devices.Count > 0
                ? "No known pedal set found  ·  use Settings, Set up a pedal set"
                : "No pedals plugged in  ·  showing simulated input")
            : sweep ? $"{name}  ·  demo sweep is on, live input paused"
            : $"{name}  ·  listening (read-only)";
        string output = Output.Active ? $"sending to game through {Output.Status}" + (Hider.Hidden ? ", real pedals hidden from games" : "")
            : OutputToggle.IsChecked == true ? $"not sending to game: {Output.Status}  ·  see Settings, Game connection check"
            : "not sending to game";
        StatusText.Text = pedals;
        OutputText.Text = char.ToUpper(output[0]) + output[1..];
        if (_tray is not null)
            _tray.Tooltip = "OpenPedalHub: " + (Output.Active ? "sending to game" : Reader.Connected ? "not sending to game" : "pedals not found");

        // The ring on the title orb breathes while real pedal input is reaching the game.
        bool pulsing = Output.Active && live;
        if (pulsing != _pulsing)
        {
            _pulsing = pulsing;
            OrbGlow.BeginAnimation(OpacityProperty, pulsing
                ? new DoubleAnimation(0.15, 0.95, TimeSpan.FromSeconds(1.1))
                    { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } }
                : null);
        }
    }

    bool _pulsing;

    // Reports can arrive far faster than the screen refreshes, so the UI samples the latest value once per frame.
    void Live_Tick(object? sender, EventArgs e)
    {
        foreach (var p in _pedals)
        {
            if (!p.IsLive || !Reader.TryGet(p.AxisUsage, out double raw)) continue;
            p.Input = Math.Round(p.Travel(raw) * 100, 1);
        }
    }

    void CycleAxis_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not PedalViewModel p || Reader.Axes.Count == 0) return;
        int i = Reader.Axes.ToList().FindIndex(a => a.Code == p.AxisUsage);
        var next = Reader.Axes[(i + 1) % Reader.Axes.Count];
        // Swap with whichever card already had that axis, so no two cards share one.
        if (_pedals.FirstOrDefault(o => o != p && o.AxisUsage == next.Code) is { } other)
        {
            other.AxisUsage = p.AxisUsage;
            other.AxisName = p.AxisName;
        }
        p.AxisUsage = next.Code;
        p.AxisName = next.Name;
    }

    // ---- Calibration ----
    // One pass: press every pedal fully, let them all go, click Finish. For each pedal the resting value is
    // wherever it sits at the end, and "fully pressed" is the reading that got farthest from that.

    double[] _calMin = [], _calMax = [], _calLast = [];
    bool _calibrating;

    void Calibrate_Click(object sender, RoutedEventArgs e)
    {
        SettingsPopup.IsOpen = false;
        SweepToggle.IsChecked = false;
        _calMin = _pedals.Select(_ => double.NaN).ToArray();
        _calMax = _pedals.Select(_ => double.NaN).ToArray();
        _calLast = _pedals.Select(_ => double.NaN).ToArray();
        foreach (var p in _pedals) { p.CalMeter = 0; p.CalNote = Reader.Connected ? "waiting for a press" : ""; }
        CalFinish.Content = "Finish";
        CalFinish.IsEnabled = CalClear.IsEnabled = Reader.Connected;
        CalCancel.Content = "Cancel";
        CalText.Text = Reader.Connected
            ? "Press each pedal all the way down, one at a time, then take your feet off all three and click Finish."
            : "The pedals are not connected, so there is nothing to measure. Plug them in and try again.";
        CalOverlay.Visibility = Visibility.Visible;
        _calibrating = Reader.Connected;
        if (_calibrating) CompositionTarget.Rendering += Cal_Tick;
    }

    void Cal_Tick(object? sender, EventArgs e)
    {
        for (int i = 0; i < _pedals.Length; i++)
        {
            var p = _pedals[i];
            if (p.AxisUsage == 0 || !Reader.TryGet(p.AxisUsage, out double raw)) continue;
            _calLast[i] = raw;
            _calMin[i] = double.IsNaN(_calMin[i]) ? raw : Math.Min(_calMin[i], raw);
            _calMax[i] = double.IsNaN(_calMax[i]) ? raw : Math.Max(_calMax[i], raw);
            double span = _calMax[i] - _calMin[i];
            p.CalMeter = Math.Round(span * 100, 1);
            p.CalNote = span >= 0.25 ? $"measured {span * 100:0}% of the axis" : "waiting for a press";
        }
    }

    void EndCalibrationSampling()
    {
        if (!_calibrating) return;
        _calibrating = false;
        CompositionTarget.Rendering -= Cal_Tick;
    }

    void CalFinish_Click(object sender, RoutedEventArgs e)
    {
        if (!_calibrating) { CalOverlay.Visibility = Visibility.Collapsed; return; }   // second click, on "Done"
        EndCalibrationSampling();
        int done = 0;
        for (int i = 0; i < _pedals.Length; i++)
        {
            var p = _pedals[i];
            double span = _calMax[i] - _calMin[i];
            if (double.IsNaN(span) || span < 0.25) { p.CalNote = "not pressed: left unchanged"; continue; }
            double rest = _calLast[i];
            double full = Math.Abs(_calMax[i] - rest) >= Math.Abs(_calMin[i] - rest) ? _calMax[i] : _calMin[i];
            // The pedal is "reversed" if pressing it makes the reading go down.
            bool invert = full < rest;
            double r = invert ? 1 - rest : rest, f = invert ? 1 - full : full;
            // A small margin at each end, so rest reliably reads 0% and full press reliably reaches 100%.
            const double margin = 0.015;
            p.Invert = invert;
            p.CalRest = Math.Clamp(r + margin, 0, 0.9);
            p.CalFull = Math.Clamp(f - margin, p.CalRest + 0.05, 1);
            p.CalNote = "calibrated" + (invert ? " (reversed axis)" : "");
            done++;
        }
        CalText.Text = done == _pedals.Length ? "All three pedals are calibrated."
            : done == 0 ? "No pedal was pressed far enough to measure, so nothing was changed."
            : $"{done} of {_pedals.Length} pedals calibrated. Any pedal that was not pressed keeps its previous setting.";
        CalFinish.Content = "Done";
        CalCancel.Content = "Close";
        CalClear.IsEnabled = false;
    }

    void CalCancel_Click(object sender, RoutedEventArgs e)
    {
        EndCalibrationSampling();
        CalOverlay.Visibility = Visibility.Collapsed;
    }

    void CalClear_Click(object sender, RoutedEventArgs e)
    {
        EndCalibrationSampling();
        foreach (var p in _pedals) { p.CalRest = 0; p.CalFull = 1; p.CalNote = "using the full axis range"; p.CalMeter = 0; }
        CalText.Text = "Calibration cleared. Each pedal now uses its axis's full range again.";
        CalFinish.Content = "Done";
        CalCancel.Content = "Close";
        CalClear.IsEnabled = false;
    }

    // ---- Curves, profiles and resets ----

    void GlideTo(PedalViewModel p, double low, double high, double gamma)
    {
        if (!_ready) { p.Apply(low, high, gamma); return; }
        _glides.RemoveAll(g => g.Pedal == p);
        if (_glides.Count == 0)
        {
            _glideStart = _clock.Elapsed.TotalSeconds;
            CompositionTarget.Rendering -= Glide_Tick;
            CompositionTarget.Rendering += Glide_Tick;
        }
        _glides.Add(new Glide(p, p.Low, p.High, p.Gamma, low, high, gamma));
    }

    void Glide_Tick(object? sender, EventArgs e)
    {
        double k = Math.Clamp((_clock.Elapsed.TotalSeconds - _glideStart) / GlideSeconds, 0, 1);
        double ease = 1 - Math.Pow(1 - k, 3);
        foreach (var g in _glides)
            g.Pedal.Apply(g.Low0 + (g.Low1 - g.Low0) * ease, g.High0 + (g.High1 - g.High0) * ease,
                          g.Gamma0 + (g.Gamma1 - g.Gamma0) * ease);
        if (k < 1) return;
        _glides.Clear();
        CompositionTarget.Rendering -= Glide_Tick;
    }

    /// <summary>Copies what is on screen into the active profile. Skipped mid-transition, when the curves are between two profiles.</summary>
    void StoreActive()
    {
        if (_glides.Count > 0 || _profiles.Count == 0) return;
        foreach (var p in _pedals)
            _profiles[_active].Curves[p.Key] = new CurveSettings
            {
                Low = Math.Round(p.Low, 1), High = Math.Round(p.High, 1), Gamma = Math.Round(p.Gamma, 2),
                Points = p.Points?.Select(pt => new[] { Math.Round(pt.X, 4), Math.Round(pt.Y, 4) }).ToList(),
            };
    }

    /// <summary>Puts the active profile's curves on screen.</summary>
    void LoadActive(bool glide)
    {
        _glides.Clear();
        foreach (var p in _pedals)
        {
            var c = _profiles[_active].Curves.GetValueOrDefault(p.Key) ?? new CurveSettings();
            double low = Math.Clamp(c.Low, 0, 40), high = Math.Clamp(c.High, 0, 40), gamma = Math.Clamp(c.Gamma, 0.3, 3);
            // Custom points switch at once; the dead zones and gamma glide.
            p.Points = c.Points?.Where(a => a is { Length: 2 }).Take(PedalCurve.MaxPoints)
                .Select(a => new Point(Math.Clamp(a[0], 0, 1), Math.Clamp(a[1], 0, 1))).OrderBy(pt => pt.X).ToArray();
            if (glide) GlideTo(p, low, high, gamma); else p.Apply(low, high, gamma);
        }
    }

    /// <param name="direction">+1 = next (to the right), -1 = back (to the left); wraps around at the ends.</param>
    public void SwitchProfile(int direction)
    {
        if (_profiles.Count < 2) return;
        CommitRename();
        StoreActive();
        _active = (_active + direction + _profiles.Count) % _profiles.Count;
        LoadActive(glide: true);
        ShowProfileName(direction);
        UiSounds.Play(direction > 0 ? "next" : "back");
    }

    void NextProfile_Click(object sender, RoutedEventArgs e) => SwitchProfile(+1);
    void PrevProfile_Click(object sender, RoutedEventArgs e) => SwitchProfile(-1);

    // Left / Right arrow keys switch profile, unless a slider or text box is using them.
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key is not (Key.Left or Key.Right)) return;
        if (Keyboard.FocusedElement is TextBox or Slider or Thumb) return;
        if (Splash.Visibility == Visibility.Visible || CalOverlay.Visibility == Visibility.Visible || SetupOverlay.Visibility == Visibility.Visible
            || ConnOverlay.Visibility == Visibility.Visible) return;
        SwitchProfile(e.Key == Key.Right ? +1 : -1);
        e.Handled = true;
    }

    /// <summary>Updates the selector. With a direction, the old name slides out one side and the new one in from the other.</summary>
    void ShowProfileName(int direction)
    {
        ProfileCount.Text = $"{_active + 1} / {_profiles.Count}";
        DeleteProfile.IsEnabled = _profiles.Count > 1;
        ResetDeleteButton();
        ProfileChanged?.Invoke();
        if (direction == 0 || !_ready) { ProfileName.Text = _profiles[_active].Name; return; }

        const double travel = 70;
        var leave = new DoubleAnimation(0, -direction * travel, TimeSpan.FromMilliseconds(110));
        leave.Completed += (_, _) =>
        {
            ProfileName.Text = _profiles[_active].Name;
            ProfileSlide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(direction * travel, 0, TimeSpan.FromMilliseconds(220))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            ProfileName.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        };
        ProfileSlide.BeginAnimation(TranslateTransform.XProperty, leave);
        ProfileName.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(110)));
    }

    void NewProfile_Click(object sender, RoutedEventArgs e)
    {
        CommitRename();
        StoreActive();
        // A copy of the current profile, so a new one starts from something sensible.
        string name = "New profile";
        for (int n = 2; _profiles.Any(x => x.Name == name); n++) name = $"New profile {n}";
        var source = _profiles[_active];
        _profiles.Insert(_active + 1, new ProfileSettings
        {
            Name = name,
            Curves = source.Curves.ToDictionary(kv => kv.Key, kv => new CurveSettings
                { Low = kv.Value.Low, High = kv.Value.High, Gamma = kv.Value.Gamma, Points = kv.Value.Points?.Select(a => (double[])a.Clone()).ToList() }),
        });
        _active++;
        ShowProfileName(+1);
        BeginRename();
    }

    void RenameProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileEdit.Visibility == Visibility.Visible) CommitRename(); else BeginRename();
    }

    void BeginRename()
    {
        ProfileEdit.Text = _profiles[_active].Name;
        ProfileEdit.Visibility = Visibility.Visible;
        ProfileName.Visibility = Visibility.Hidden;
        ProfileEdit.Focus();
        ProfileEdit.SelectAll();
    }

    void CommitRename()
    {
        if (ProfileEdit.Visibility != Visibility.Visible) return;
        string name = ProfileEdit.Text.Trim();
        if (name.Length > 0) _profiles[_active].Name = name;
        EndRename();
    }

    void EndRename()
    {
        ProfileEdit.Visibility = Visibility.Collapsed;
        ProfileName.Visibility = Visibility.Visible;
        ProfileName.Text = _profiles[_active].Name;
        ProfileChanged?.Invoke();
    }

    void ProfileEdit_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) CommitRename();
        else if (e.Key == Key.Escape) EndRename();
        else return;
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    void ProfileEdit_LostFocus(object sender, RoutedEventArgs e) => CommitRename();

    // Delete asks once more on the button itself: the first click arms it, the second deletes.
    bool _deleteArmed;
    readonly DispatcherTimer _deleteTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_profiles.Count < 2) return;
        if (!_deleteArmed)
        {
            _deleteArmed = true;
            DeleteProfile.Content = "Really delete?";
            _deleteTimer.Tick -= DeleteTimer_Tick;
            _deleteTimer.Tick += DeleteTimer_Tick;
            _deleteTimer.Start();
            return;
        }
        CommitRename();
        _glides.Clear();
        _profiles.RemoveAt(_active);
        _active = Math.Min(_active, _profiles.Count - 1);
        LoadActive(glide: true);
        ShowProfileName(-1);
    }

    void DeleteTimer_Tick(object? sender, EventArgs e) => ResetDeleteButton();

    void ResetDeleteButton()
    {
        _deleteTimer.Stop();
        _deleteArmed = false;
        DeleteProfile.Content = "Delete";
    }

    /// <summary>Returns one pedal's curve to a straight line. Calibration, axis and reverse settings are kept.</summary>
    public void ResetCurve(PedalViewModel p)
    {
        p.Points = null;
        GlideTo(p, 0, 0, 1);
    }

    void ResetAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var p in _pedals) ResetCurve(p);
    }

    void ResetPedal_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is PedalViewModel p) ResetCurve(p);
    }

    // Enter commits a typed value; Escape puts the old one back.
    void Num_KeyDown(object sender, KeyEventArgs e)
    {
        var box = (TextBox)sender;
        var binding = box.GetBindingExpression(TextBox.TextProperty);
        if (e.Key == Key.Enter) binding?.UpdateSource();
        else if (e.Key == Key.Escape) binding?.UpdateTarget();
        else return;
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    // ---- Motion and sound ----

    // Keeps the band of light that sweeps across a card header inside the header's rounded top corners.
    void SweepHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var host = (FrameworkElement)sender;
        double w = host.ActualWidth, h = host.ActualHeight;
        // Rounded at the top only: a taller rounded rectangle, cut off flat at the header's bottom edge.
        host.Clip = new CombinedGeometry(GeometryCombineMode.Intersect,
            new RectangleGeometry(new Rect(0, 0, w, h + 40), 17, 17), new RectangleGeometry(new Rect(0, 0, w, h)));
    }

    // Points each card's reflection at the bottom strip of that card, whatever size the card currently is.
    void CardBody_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var body = (FrameworkElement)sender;
        if (body.Parent is not Panel root) return;
        foreach (var mirror in root.Children.OfType<System.Windows.Shapes.Rectangle>())
            if (mirror.Fill is VisualBrush brush)
                brush.Viewbox = new Rect(0, Math.Max(0, body.ActualHeight - mirror.Height), body.ActualWidth, mirror.Height);
    }

    // Cards rise and fade in one after another when the window opens.
    void PedalList_Loaded(object sender, RoutedEventArgs e)
    {
        if (!App.ShowIntro) { PlayCardEntrance(0); return; }

        // Startup animation first; the cards make their entrance as it fades away.
        Splash.Visibility = Visibility.Visible;
        Splash.Finished += () =>
        {
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(450));
            fade.Completed += (_, _) => Splash.Visibility = Visibility.Collapsed;
            Splash.BeginAnimation(OpacityProperty, fade);
            PlayCardEntrance(150);
        };
        foreach (var card in Cards()) card.Opacity = 0;

        // Optional startup sound: a startup.wav next to the app. Without one, the animation plays silently.
        string wav = System.IO.Path.Combine(AppContext.BaseDirectory, "startup.wav");
        if (!System.IO.File.Exists(wav)) { Splash.Start(); return; }

        // Timed to this sound: its swell covers the ribbons and its second peak (3.25 s) lands on the pulse of light.
        Splash.Scale = 3.25 / 2.05;
        var sound = _introSound = new MediaPlayer { Volume = 0.85 };
        bool begun = false;
        void Begin(bool withSound)
        {
            if (begun) return;
            begun = true;
            if (withSound) sound.Play();
            Splash.Start();
        }
        sound.MediaOpened += (_, _) => Begin(true);
        sound.MediaFailed += (_, _) => Begin(false);
        sound.MediaEnded += (_, _) => sound.Close();
        sound.Open(new Uri(wav));
        // Never leave the splash waiting on audio that will not load.
        var giveUp = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        giveUp.Tick += (_, _) => { giveUp.Stop(); Begin(false); };
        giveUp.Start();
    }

    MediaPlayer? _introSound;

    // Click to skip: cut the sound too. If the animation ends on its own, the sound is left to ring out.
    void Splash_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _introSound?.Stop();
        Splash.Finish();
    }

    IEnumerable<FrameworkElement> Cards() => Enumerable.Range(0, _pedals.Length)
        .Select(i => PedalList.ItemContainerGenerator.ContainerFromIndex(i)).OfType<FrameworkElement>();

    void PlayCardEntrance(int startDelayMs)
    {
        int i = -1;
        foreach (var card in Cards())
        {
            i++;
            var shift = new TranslateTransform(0, 30);
            card.RenderTransform = shift;
            card.Opacity = 0;
            var delay = TimeSpan.FromMilliseconds(startDelayMs + 110 * i);
            card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(420)) { BeginTime = delay });
            shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(30, 0, TimeSpan.FromMilliseconds(520))
                { BeginTime = delay, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
    }

    // Demo sweep: moves simulated pedals like a lap would, so the curves can be watched in motion.
    void Sweep_Changed(object sender, RoutedEventArgs e)
    {
        if (_ready && IsInitialized && StatusText is not null) UpdateMode();
    }

    void Sweep_Tick(object? sender, EventArgs e)
    {
        double t = _clock.Elapsed.TotalSeconds;
        double wave = Math.Sin(t * 1.3);
        _throttle.Input = Math.Round(Math.Clamp(0.55 + 0.65 * wave, 0, 1) * 100, 1);
        _brake.Input = Math.Round(Math.Clamp(-0.15 - 0.95 * wave, 0, 1) * 100, 1);
        _clutch.Input = Math.Round(Math.Clamp(1.8 * Math.Sin(t * 0.65 + 1) - 0.9, 0, 1) * 100, 1);
    }

    void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
