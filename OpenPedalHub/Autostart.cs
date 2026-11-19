using Microsoft.Win32;

namespace OpenPedalHub;

/// <summary>
/// "Start with Windows", using the per-user Run key (the same place Windows' own Startup Apps list shows).
/// It only affects this user account and needs no administrator rights.
/// </summary>
public static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "OpenPedalHub";

    /// <summary>If "Start with Windows" was on under the app's old name, move it to the new name and program path.</summary>
    public static void CarryOverFromOldName()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            const string oldName = "TPedals" + "Tuner";
            if (key.GetValue(oldName) is null) return;
            key.DeleteValue(oldName, throwOnMissingValue: false);
            Enabled = true;
        }
        catch { /* leave it off; the switch in Settings shows the real state */ }
    }

    public static bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            // "--tray" starts quietly in the notification area: no window, no startup animation or sound.
            if (value) key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --tray");
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
