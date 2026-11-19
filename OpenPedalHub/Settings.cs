using System.IO;
using System.Text.Json;

namespace OpenPedalHub;

/// <summary>One pedal's response curve.</summary>
public class CurveSettings
{
    public double Low { get; set; }
    public double High { get; set; }
    public double Gamma { get; set; } = 1;
    /// <summary>Custom curve points as [x, y] pairs, or null to use gamma.</summary>
    public List<double[]>? Points { get; set; }
}

/// <summary>A named set of curves, one per pedal ("clutch", "brake", "throttle").</summary>
public class ProfileSettings
{
    public string Name { get; set; } = "";
    public Dictionary<string, CurveSettings> Curves { get; set; } = [];
}

/// <summary>
/// Facts about the hardware, shared by every profile. (Low/High/Gamma here are from before profiles
/// existed; they are still read once to carry an old setup over into a profile.)
/// </summary>
public class PedalSettings : CurveSettings
{
    public bool Invert { get; set; }
    /// <summary>Which pedal axis feeds this card (HID usage number). 0 = not chosen yet.</summary>
    public int Axis { get; set; }
    /// <summary>Calibration: where the pedal rests and bottoms out, as fractions of the axis range.</summary>
    public double CalRest { get; set; }
    public double CalFull { get; set; } = 1;
}

/// <summary>Everything the app remembers, kept in a small file in the user's AppData folder. Nothing is stored on the pedals.</summary>
public class Settings
{
    public Dictionary<string, PedalSettings> Pedals { get; set; } = [];
    /// <summary>Which model of pedal set ("VID:PID") the settings in Pedals belong to.</summary>
    public string? Device { get; set; }
    /// <summary>Whether shaped output is sent to the virtual controller.</summary>
    public bool SendToGame { get; set; } = true;
    public List<ProfileSettings> Profiles { get; set; } = [];
    /// <summary>Index of the profile in use.</summary>
    public int Active { get; set; }
    /// <summary>"Default" or "Dark".</summary>
    public string Theme { get; set; } = "Default";
    /// <summary>Minimising hides the window in the notification area instead of the taskbar.</summary>
    public bool MinimizeToTray { get; set; } = true;
    /// <summary>Show the always-on-top game overlay.</summary>
    public bool ShowOverlay { get; set; }
    /// <summary>Hide the overlay's drag handle so it stays where it is.</summary>
    public bool OverlayLocked { get; set; }
    /// <summary>Where the overlay was last placed; null = top centre of the main screen.</summary>
    public double? OverlayX { get; set; }
    public double? OverlayY { get; set; }

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenPedalHub", "settings.json");

    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>
    /// The app used to be called "T-Pedals Tuner". On first run under the new name, copy its settings across
    /// (the old folder is left in place as a backup).
    /// </summary>
    static void CarryOverFromOldName()
    {
        try
        {
            string oldDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TPedals" + "Tuner");
            string newDir = Path.GetDirectoryName(FilePath)!;
            if (File.Exists(FilePath) || !Directory.Exists(oldDir)) return;
            Directory.CreateDirectory(newDir);
            foreach (string file in Directory.GetFiles(oldDir, "*.json"))
                File.Copy(file, Path.Combine(newDir, Path.GetFileName(file)), overwrite: false);
        }
        catch { /* worst case the app starts with default settings */ }
    }

    public static Settings? Load()
    {
        CarryOverFromOldName();
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) : null; }
        catch { return null; }   // unreadable file: start from defaults rather than fail to open
    }

    public static void Save(string json)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, json);
        }
        catch { /* settings are a convenience; never crash the app over them */ }
    }

    /// <summary>Starter profiles for a first run. They are ordinary profiles: rename, change or delete them.</summary>
    public static List<ProfileSettings> Starters() =>
    [
        Make("Linear", (0, 0, 1), (0, 0, 1), (0, 0, 1)),
        Make("GT3", (5, 10, 1), (3, 8, 1.8), (2, 3, 1.2)),
        Make("Rally", (5, 15, 0.8), (4, 12, 1.4), (3, 3, 1.6)),
    ];

    static ProfileSettings Make(string name, (double l, double h, double g) clutch, (double l, double h, double g) brake, (double l, double h, double g) throttle) => new()
    {
        Name = name,
        Curves = new()
        {
            ["clutch"] = new() { Low = clutch.l, High = clutch.h, Gamma = clutch.g },
            ["brake"] = new() { Low = brake.l, High = brake.h, Gamma = brake.g },
            ["throttle"] = new() { Low = throttle.l, High = throttle.h, Gamma = throttle.g },
        },
    };
}
