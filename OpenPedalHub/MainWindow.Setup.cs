using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace OpenPedalHub;

// The "Set up a pedal set" screen.
//
// It asks for one pedal at a time. While a pedal is pressed and released it watches every axis of every game
// controller and picks the one that moved most. From that it learns which device the pedals are, which axis
// each pedal is on, which way the axis runs, and where the pedal rests and bottoms out.
//
// The result is saved so this pedal set is recognised from then on, and written as a small report file that
// can be shared so the set can be added to the app for everyone.
//
// PRIVACY: the report holds facts about hardware MODELS only: USB vendor/product ids, the model name Windows
// shows, axis layouts and measured ranges. It deliberately leaves out anything that identifies this particular
// PC or unit: no serial numbers, no device instance paths, no computer or user name, no file paths.
public partial class MainWindow
{
    static readonly string[] SetupRoles = ["clutch", "brake", "throttle"];

    sealed class Seen { public double Min, Max, Last; }
    sealed record Found(PedalReader.Device Device, int Usage, double Rest, double Full);

    readonly Dictionary<(IntPtr device, int usage), Seen> _seen = [];
    readonly Dictionary<string, Found> _found = [];
    int _setupStep;          // 0 intro, 1..3 one pedal each, 4 name and save, 5 finished
    bool _setupWatching;
    PedalSetDefinition? _setupResult;

    void SetupPedals_Click(object sender, RoutedEventArgs e)
    {
        SettingsPopup.IsOpen = false;
        SweepToggle.IsChecked = false;
        _found.Clear();
        _setupResult = null;
        SetupFolder.Visibility = Visibility.Collapsed;
        SetupNameRow.Visibility = Visibility.Collapsed;
        SetupOverlay.Visibility = Visibility.Visible;
        ShowSetupStep(0);
    }

    void ShowSetupStep(int step)
    {
        _setupStep = step;
        _seen.Clear();
        SetupMeter.Value = 0;
        SetupSkip.Visibility = step is >= 1 and <= 3 ? Visibility.Visible : Visibility.Collapsed;
        SetupLiveBox.Visibility = step is >= 1 and <= 3 ? Visibility.Visible : Visibility.Collapsed;
        SetupCancel.Content = "Cancel";
        SetupNext.IsEnabled = true;

        if (step == 0)
        {
            var names = Reader.Devices.Select(d => d.Name).ToList();
            SetupStep.Text = "BEFORE YOU START";
            SetupText.Text = names.Count == 0
                ? "No game controllers are plugged in. Connect the pedals and open this again."
                : "Take your feet off the pedals. You will be asked to press one pedal at a time, all the way down, and release it.\n\n"
                  + "Controllers found: " + string.Join(", ", names) + ".";
            SetupNext.Content = "Start";
            SetupNext.IsEnabled = names.Count > 0;
        }
        else if (step <= 3)
        {
            string role = SetupRoles[step - 1];
            SetupStep.Text = $"STEP {step} OF 3";
            SetupText.Text = $"Press the {role.ToUpperInvariant()} all the way down, then let it go. Keep your feet off the other pedals."
                + (role == "clutch" ? "\n\nNo clutch pedal? Click Skip." : "");
            SetupLive.Text = "Waiting for a pedal to move";
            SetupNext.Content = "Next";
        }
        StartSetupWatching(step is >= 1 and <= 3);
    }

    void StartSetupWatching(bool on)
    {
        if (on == _setupWatching) return;
        _setupWatching = on;
        if (on) CompositionTarget.Rendering += Setup_Tick; else CompositionTarget.Rendering -= Setup_Tick;
    }

    void Setup_Tick(object? sender, EventArgs e)
    {
        foreach (var d in Reader.Devices)
            foreach (var (usage, value) in d.Values)
            {
                if (!_seen.TryGetValue((d.Handle, usage), out var s)) _seen[(d.Handle, usage)] = s = new Seen { Min = value, Max = value };
                s.Min = Math.Min(s.Min, value);
                s.Max = Math.Max(s.Max, value);
                s.Last = value;
            }
        var best = BestMoved();
        if (best is null) { SetupMeter.Value = 0; return; }
        var (device, usage2, seen) = best.Value;
        double span = seen.Max - seen.Min;
        SetupMeter.Value = span * 100;
        SetupLive.Text = $"{device.Name}  ·  axis {PedalReader.AxisName(usage2)}  ·  moved {span * 100:0}% of its range";
    }

    /// <summary>The axis that has moved most during this step, ignoring axes already given to another pedal.</summary>
    (PedalReader.Device device, int usage, Seen seen)? BestMoved()
    {
        (PedalReader.Device, int, Seen)? best = null;
        double bestSpan = 0.02;   // ignore sensor jitter
        foreach (var ((handle, usage), seen) in _seen)
        {
            var device = Reader.Devices.FirstOrDefault(d => d.Handle == handle);
            if (device is null || _found.Values.Any(f => f.Device == device && f.Usage == usage)) continue;
            double span = seen.Max - seen.Min;
            if (span > bestSpan) { bestSpan = span; best = (device, usage, seen); }
        }
        return best;
    }

    void SetupNext_Click(object sender, RoutedEventArgs e)
    {
        if (_setupStep == 0) { ShowSetupStep(1); return; }
        if (_setupStep is >= 1 and <= 3)
        {
            var best = BestMoved();
            if (best is null || best.Value.seen.Max - best.Value.seen.Min < 0.25)
            {
                SetupLive.Text = "That pedal did not move far enough to measure. Press it all the way down and release, or click Skip.";
                return;
            }
            var (device, usage, seen) = best.Value;
            // It rests wherever it ended up; "fully pressed" is the reading farthest from that.
            double rest = seen.Last, full = Math.Abs(seen.Max - rest) >= Math.Abs(seen.Min - rest) ? seen.Max : seen.Min;
            _found[SetupRoles[_setupStep - 1]] = new Found(device, usage, rest, full);
            AdvanceSetup();
            return;
        }
        if (_setupStep == 4) { SaveSetup(); return; }
        SetupOverlay.Visibility = Visibility.Collapsed;   // step 5: "Done"
    }

    void SetupSkip_Click(object sender, RoutedEventArgs e) => AdvanceSetup();

    void AdvanceSetup()
    {
        if (_setupStep < 3) { ShowSetupStep(_setupStep + 1); return; }
        StartSetupWatching(false);
        _setupStep = 4;
        SetupSkip.Visibility = SetupLiveBox.Visibility = Visibility.Collapsed;

        // The pedal set is the device the throttle (or failing that the brake) is on.
        var main = (_found.GetValueOrDefault("throttle") ?? _found.GetValueOrDefault("brake"))?.Device;
        if (main is null)
        {
            SetupStep.Text = "NOTHING MEASURED";
            SetupText.Text = "No brake or throttle was detected, so there is nothing to save. You can close this and try again.";
            SetupNext.Content = "Done";
            SetupCancel.Content = "Close";
            _setupStep = 5;
            return;
        }

        var def = BuildDefinition(main, _found);
        _setupResult = def;
        ShowSetupSummary(def);
    }

    /// <summary>
    /// Turns what was measured into a pedal-set definition plus its report details. Everything written to a
    /// report file comes from here, so this is the place to check when asking "what does a report contain?".
    /// </summary>
    PedalSetDefinition BuildDefinition(PedalReader.Device main, IReadOnlyDictionary<string, Found> found)
    {
        var notes = new List<string>();
        var def = new PedalSetDefinition
        {
            Name = main.Name, Vid = $"{main.Vid:X4}", Pid = $"{main.Pid:X4}",
        };
        foreach (string role in SetupRoles)
        {
            if (!found.TryGetValue(role, out var f)) { notes.Add($"{role}: skipped or not present"); continue; }
            if (f.Device != main) { notes.Add($"{role}: moved an axis on a different controller ({f.Device.Name}), so it was left out"); continue; }
            bool reversed = f.Full < f.Rest;
            double rest = reversed ? 1 - f.Rest : f.Rest, full = reversed ? 1 - f.Full : f.Full;
            const double margin = 0.015;   // so rest reliably reads 0% and a full press reliably reaches 100%
            def.Axes[role] = new AxisDefinition
            {
                Usage = f.Usage, AxisName = PedalReader.AxisName(f.Usage), Reversed = reversed,
                Rest = Math.Round(Math.Clamp(rest + margin, 0, 0.9), 4),
                Full = Math.Round(Math.Clamp(full - margin, rest + margin + 0.05, 1), 4),
            };
        }
        // Only hide a device from games when every one of its axes turned out to be a pedal. If it has other
        // axes (a steering axis, say) it is a wheel base with the pedals plugged in, and hiding it would hide
        // the wheel. When unsure this errs towards not hiding, which is the harmless mistake.
        def.HideFromGames = main.Axes.Count == def.Axes.Count;
        if (!def.HideFromGames) notes.Add($"device has {main.Axes.Count} axes but only {def.Axes.Count} were pedals: treated as shared with other controls, so it is not hidden from games");
        def.Report = new ReportInfo
        {
            Created = DateTime.UtcNow.ToString("yyyy-MM-dd"),
            App = "OpenPedalHub",
            Windows = $"Windows build {Environment.OSVersion.Version.Build}",
            Controllers = Reader.Devices.Select(d => new ReportController
            {
                Name = d.Name, Vid = $"{d.Vid:X4}", Pid = $"{d.Pid:X4}",
                Axes = d.Axes.Select(a => new ReportAxis { Usage = a.Code, Name = a.Name, Bits = a.Bits, Min = a.Min, Max = a.Max }).ToList(),
            }).ToList(),
            Notes = notes,
        };
        return def;
    }

    /// <summary>Developer aid: the report that would be written for the active pedal set, using made-up measurements.</summary>
    public string DebugSampleReport()
    {
        if (Reader.Active is not { } main || _set is null) return "no known pedal set connected";
        var found = _set.Axes.ToDictionary(kv => kv.Key, kv => new Found(main, kv.Value.Usage, kv.Value.Reversed ? 0.99 : 0.01, kv.Value.Reversed ? 0.02 : 0.98));
        return PedalSets.ToJson(BuildDefinition(main, found));
    }

    void ShowSetupSummary(PedalSetDefinition def)
    {
        SetupStep.Text = "CHECK AND SAVE";
        SetupText.Text = string.Join("\n", SetupRoles.Select(r => def.Axes.TryGetValue(r, out var a)
            ? $"{char.ToUpper(r[0]) + r[1..]}: axis {a.AxisName}{(a.Reversed ? ", reversed" : "")}"
            : $"{char.ToUpper(r[0]) + r[1..]}: not set"))
            + (def.HideFromGames ? "" : "\n\nThis controller has other axes besides the pedals (for example a wheel base with pedals plugged in), so it will not be hidden from games.");
        SetupName.Text = def.Name;
        SetupNameRow.Visibility = Visibility.Visible;
        SetupNext.Content = "Save and use";
    }

    void SaveSetup()
    {
        if (_setupResult is not { } def) return;
        if (SetupName.Text.Trim() is { Length: > 0 } name) def.Name = name;
        string copy;
        try { copy = PedalSets.SaveUser(def); }
        catch (Exception ex)
        {
            SetupStep.Text = "COULD NOT SAVE";
            SetupText.Text = "The pedal set could not be written to disk: " + ex.Message;
            return;
        }
        // Start using it now.
        _hardwareFor = null;
        OnConnectionChanged();

        _setupReportFile = copy;
        SetupStep.Text = "SAVED";
        SetupText.Text = "This pedal set is now recognised automatically on this PC.\n\n"
            + "A report file was saved in your Documents folder, under OpenPedalHub\\reports. Send that file to have this set added for everyone. "
            + "It contains only hardware model details: no serial numbers and nothing that identifies you or this PC."
            + (def.HideFromGames
                ? "\n\nIf a game still shows the real pedals as well as the virtual ones, unplug the pedals and plug them back in once. "
                  + "Hiding only starts working on a device after it has been reconnected."
                : "");
        SetupNameRow.Visibility = Visibility.Collapsed;
        SetupFolder.Visibility = Visibility.Visible;
        SetupNext.Content = "Done";
        SetupCancel.Content = "Close";
        _setupStep = 5;
    }

    string _setupReportFile = "";

    void SetupFolder_Click(object sender, RoutedEventArgs e)
    {
        // Opens File Explorer with the report selected.
        try { Process.Start("explorer.exe", $"/select,\"{_setupReportFile}\""); }
        catch { /* nothing to do: the path is described on screen */ }
    }

    void SetupCancel_Click(object sender, RoutedEventArgs e)
    {
        StartSetupWatching(false);
        SetupOverlay.Visibility = Visibility.Collapsed;
    }
}
