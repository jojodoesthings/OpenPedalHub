using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenPedalHub;

/// <summary>Where one pedal sits on a pedal set.</summary>
public class AxisDefinition
{
    /// <summary>HID usage of the axis (0x30 X ... 0x37 Dial), as a plain number.</summary>
    public int Usage { get; set; }
    /// <summary>For people reading the file; not used for matching.</summary>
    public string? AxisName { get; set; }
    /// <summary>True if the reading goes down as the pedal is pressed.</summary>
    public bool Reversed { get; set; }
    /// <summary>Measured travel, as fractions of the axis range after reversal. Null = use the full range.</summary>
    public double? Rest { get; set; }
    public double? Full { get; set; }
}

/// <summary>
/// One model of pedal set and how to read it. This is both the app's database entry and the report file a
/// user sends in: a report that looks right can be dropped into the "pedalsets" folder unchanged.
/// </summary>
public class PedalSetDefinition
{
    public string Name { get; set; } = "";
    /// <summary>USB vendor and product id as four hex digits each, e.g. "044F" and "B678". Together they identify the model.</summary>
    public string Vid { get; set; } = "";
    public string Pid { get; set; } = "";
    /// <summary>"clutch", "brake", "throttle". A set without a clutch simply has no "clutch" entry.</summary>
    public Dictionary<string, AxisDefinition> Axes { get; set; } = [];
    /// <summary>
    /// Hide the real device from games while the app feeds the virtual pedals. Must be false for pedals that
    /// plug into a wheel base and share its USB device, or the wheel would be hidden too.
    /// </summary>
    public bool HideFromGames { get; set; } = true;
    /// <summary>Extra detail captured by the set-up screen, for whoever reviews the report. Ignored by the app.</summary>
    public ReportInfo? Report { get; set; }

    [JsonIgnore] public string Id => $"{Vid}:{Pid}".ToUpperInvariant();
    /// <summary>Where it was loaded from, for the status line.</summary>
    [JsonIgnore] public string Source { get; set; } = "built in";
}

public class ReportInfo
{
    public string Created { get; set; } = "";
    public string App { get; set; } = "";
    public string Windows { get; set; } = "";
    /// <summary>Every game controller that was plugged in at the time, so a reviewer can see the whole rig.</summary>
    public List<ReportController> Controllers { get; set; } = [];
    public List<string> Notes { get; set; } = [];
}

public class ReportController
{
    public string Name { get; set; } = "";
    public string Vid { get; set; } = "";
    public string Pid { get; set; } = "";
    public List<ReportAxis> Axes { get; set; } = [];
}

public class ReportAxis
{
    public int Usage { get; set; }
    public string Name { get; set; } = "";
    public int Bits { get; set; }
    public long Min { get; set; }
    public long Max { get; set; }
}

/// <summary>
/// The known pedal sets. Loaded, in order of priority, from:
///   1. this user's own sets   (%AppData%\OpenPedalHub\pedalsets\*.json, written by the set-up screen)
///   2. sets shipped with the app (the "pedalsets" folder next to the program)
///   3. the one built in below
/// </summary>
public static class PedalSets
{
    public static string UserFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenPedalHub", "pedalsets");
    public static string ShippedFolder { get; } = Path.Combine(AppContext.BaseDirectory, "pedalsets");
    /// <summary>Copies of reports, somewhere easy to find and attach to a message.</summary>
    public static string ReportsFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "OpenPedalHub", "reports");

    static readonly JsonSerializerOptions Options = new()
        { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, PropertyNameCaseInsensitive = true };

    static List<PedalSetDefinition> _all = [];

    public static IReadOnlyList<PedalSetDefinition> All => _all;

    public static void Load()
    {
        var all = new List<PedalSetDefinition>();
        all.AddRange(ReadFolder(UserFolder, "your own set-up"));
        all.AddRange(ReadFolder(ShippedFolder, "shipped with the app"));
        all.Add(BuiltIn());
        // First one wins for each model.
        _all = all.GroupBy(d => d.Id).Select(g => g.First()).ToList();
    }

    public static PedalSetDefinition? Find(ushort vid, ushort pid) => _all.FirstOrDefault(d => d.Id == $"{vid:X4}:{pid:X4}");

    static IEnumerable<PedalSetDefinition> ReadFolder(string folder, string source)
    {
        if (!Directory.Exists(folder)) yield break;
        foreach (string file in Directory.GetFiles(folder, "*.json").OrderBy(f => f))
        {
            PedalSetDefinition? def = null;
            try { def = JsonSerializer.Deserialize<PedalSetDefinition>(File.ReadAllText(file), Options); }
            catch { /* a broken file must not stop the others loading */ }
            if (def is null || def.Vid.Length != 4 || def.Pid.Length != 4 || def.Axes.Count == 0) continue;
            def.Source = source;
            yield return def;
        }
    }

    /// <summary>Saves a set for this user (so it is detected from now on) and a copy in Documents to share. Returns the copy's path.</summary>
    public static string ToJson(PedalSetDefinition def) => JsonSerializer.Serialize(def, Options);

    public static string SaveUser(PedalSetDefinition def)
    {
        string json = ToJson(def);
        string file = SafeName($"{def.Name}-{def.Vid}-{def.Pid}") + ".json";
        Directory.CreateDirectory(UserFolder);
        File.WriteAllText(Path.Combine(UserFolder, SafeName($"{def.Vid}-{def.Pid}") + ".json"), json);
        Directory.CreateDirectory(ReportsFolder);
        string copy = Path.Combine(ReportsFolder, file);
        File.WriteAllText(copy, json);
        Load();
        return copy;
    }

    static string SafeName(string s) =>
        new string(s.Select(c => char.IsLetterOrDigit(c) || c == '-' ? char.ToLowerInvariant(c) : '-').ToArray()).Trim('-');

    /// <summary>The set the app was first written for, so it works even if the pedalsets folder is missing.</summary>
    static PedalSetDefinition BuiltIn() => new()
    {
        Name = "Thrustmaster T-Pedals (T3PM via USB adapter)",
        Vid = "044F", Pid = "B678",
        Axes = new()
        {
            ["clutch"] = new() { Usage = 0x36, AxisName = "Slider", Reversed = true },
            ["brake"] = new() { Usage = 0x32, AxisName = "Z", Reversed = true },
            ["throttle"] = new() { Usage = 0x35, AxisName = "Rz", Reversed = true },
        },
        HideFromGames = true,
    };
}
