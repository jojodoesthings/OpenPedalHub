using System.Diagnostics;
using System.IO;

namespace OpenPedalHub;

/// <summary>
/// Hides the real T-Pedals from games while this app is feeding the virtual controller, using HidHide's
/// own command-line tool. Hiding is a Windows-side filter: nothing is sent to or changed on the pedals,
/// and unhiding puts everything back.
/// </summary>
public sealed class PedalHider
{
    const string Cli = @"C:\Program Files\Nefarius Software Solutions\HidHide\x64\HidHideCLI.exe";

    string? _hiddenPath;

    public bool Available => File.Exists(Cli);
    public bool Hidden => _hiddenPath is not null;

    /// <summary>Picks up a hide left behind by a previous run that did not close cleanly.</summary>
    public void Adopt(string instancePath)
    {
        if (Available && IsListed(instancePath)) _hiddenPath = instancePath;
    }

    public bool Hide(string instancePath)
    {
        if (!Available || string.IsNullOrEmpty(instancePath)) return false;
        // A different pedal set from the one currently hidden (the set was swapped): put the old one back first.
        if (_hiddenPath is not null && !string.Equals(_hiddenPath, instancePath, StringComparison.OrdinalIgnoreCase)) Unhide();
        if (Hidden) { StartWatchdog(instancePath); return true; }   // e.g. a hide adopted from an earlier run
        // This app must stay allowed to see the pedals, or it would hide them from itself.
        string self = Environment.ProcessPath ?? "";
        Run($"--app-reg \"{self}\" --dev-hide \"{instancePath}\" --cloak-on");
        if (!IsListed(instancePath)) return false;
        _hiddenPath = instancePath;
        StartWatchdog(instancePath);
        return true;
    }

    /// <summary>Devices that already have a helper waiting to unhide them. One helper per device ever hidden.</summary>
    readonly HashSet<string> _watched = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A small background helper that waits for this app to end, however it ends (crash, End Task, sign-out),
    /// and then unhides the pedals. Without it a forced close would leave them hidden from games.
    /// </summary>
    void StartWatchdog(string instancePath)
    {
        if (!_watched.Add(instancePath)) return;
        string script =
            $"Wait-Process -Id {Environment.ProcessId} -ErrorAction SilentlyContinue; " +
            $"$c = '{Cli}'; & $c --dev-unhide '{instancePath}' | Out-Null; " +
            "if (-not ((& $c --dev-list) -match 'dev-hide')) { & $c --cloak-off | Out-Null }";
        try
        {
            Process.Start(new ProcessStartInfo("powershell.exe",
                $"-NoProfile -NonInteractive -WindowStyle Hidden -Command \"{script}\"")
            { UseShellExecute = false, CreateNoWindow = true })?.Dispose();
        }
        catch { _watched.Remove(instancePath); /* the normal close path still unhides */ }
    }

    public void Unhide()
    {
        if (_hiddenPath is null) return;
        Run($"--dev-unhide \"{_hiddenPath}\"");
        _hiddenPath = null;
        // Switch hiding off altogether only if nothing else is hidden, so other HidHide setups are left alone.
        if (!Run("--dev-list").Contains("--dev-hide", StringComparison.OrdinalIgnoreCase)) Run("--cloak-off");
    }

    static bool IsListed(string instancePath) =>
        Run("--dev-list").Contains(instancePath, StringComparison.OrdinalIgnoreCase);

    static string Run(string arguments)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Cli, arguments)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            })!;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return output;
        }
        catch { return ""; }
    }
}
