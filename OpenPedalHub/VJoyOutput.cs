using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace OpenPedalHub;

public enum VJoyState
{
    /// <summary>vJoy is not on this PC.</summary>
    NotInstalled,
    /// <summary>Installed, but its driver is not active (often: restart needed, or the install did not complete).</summary>
    DriverNotRunning,
    /// <summary>vJoy works, but virtual controller 1 does not exist or lacks the three pedal axes.</summary>
    DeviceNotSetUp,
    /// <summary>Another program is feeding virtual controller 1.</summary>
    Busy,
    Ready,
}

/// <summary>
/// Sends the shaped pedal values to a vJoy virtual controller, which games see as an ordinary pedal device.
/// This talks only to the virtual device. It has no connection to the real pedals.
/// </summary>
public sealed class VJoyOutput
{
    const string Dll = "vJoyInterface.dll";
    const uint Device = 1;
    // Virtual axes: clutch = Rx, brake = Ry, throttle = Rz.
    const uint AxisClutch = 0x33, AxisBrake = 0x34, AxisThrottle = 0x35;

    public const string DownloadPage = "https://github.com/BrunnerInnovation/vJoy/releases";

    int _max = 32767;

    public bool Active { get; private set; }
    /// <summary>Plain-language state for the status line.</summary>
    public string Status { get; private set; } = "off";
    public long Sent { get; private set; }

    /// <summary>Where vJoy is installed, or null. Found from Windows' list of installed programs, then the usual folder.</summary>
    public static string? InstallFolder
    {
        get
        {
            foreach (string root in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
            {
                try
                {
                    using var list = Registry.LocalMachine.OpenSubKey(root);
                    foreach (string name in list?.GetSubKeyNames() ?? [])
                    {
                        using var k = list!.OpenSubKey(name);
                        if (k?.GetValue("DisplayName") is string display && display.StartsWith("vJoy", StringComparison.OrdinalIgnoreCase)
                            && k.GetValue("InstallLocation") is string where && Directory.Exists(where))
                            return where.TrimEnd('\\');
                    }
                }
                catch { /* try the next place */ }
            }
            string usual = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "vJoy");
            return Directory.Exists(usual) ? usual : null;
        }
    }

    static string? DllPath => InstallFolder is { } f && File.Exists(Path.Combine(f, "x64", Dll)) ? Path.Combine(f, "x64", Dll) : null;
    static string? ConfigTool => InstallFolder is { } f && File.Exists(Path.Combine(f, "x64", "vJoyConfig.exe")) ? Path.Combine(f, "x64", "vJoyConfig.exe") : null;

    static VJoyOutput()
    {
        NativeLibrary.SetDllImportResolver(typeof(VJoyOutput).Assembly, (name, _, _) =>
            name == Dll && DllPath is { } path && NativeLibrary.TryLoad(path, out IntPtr h) ? h : IntPtr.Zero);
    }

    /// <summary>What state vJoy is in on this PC, without taking the virtual controller.</summary>
    public static VJoyState Probe()
    {
        try
        {
            if (DllPath is null) return VJoyState.NotInstalled;
            if (!vJoyEnabled()) return VJoyState.DriverNotRunning;
            int state = GetVJDStatus(Device);   // 0 owned by us, 1 free, 2 busy, 3 missing
            if (state == 3 || !GetVJDAxisExist(Device, AxisClutch) || !GetVJDAxisExist(Device, AxisBrake) || !GetVJDAxisExist(Device, AxisThrottle))
                return VJoyState.DeviceNotSetUp;
            return state == 2 ? VJoyState.Busy : VJoyState.Ready;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return VJoyState.NotInstalled;
        }
    }

    public static string Describe(VJoyState state) => state switch
    {
        VJoyState.NotInstalled => "vJoy is not installed",
        VJoyState.DriverNotRunning => "vJoy is installed but its driver is not running",
        VJoyState.DeviceNotSetUp => "the virtual pedals are not set up yet",
        VJoyState.Busy => "vJoy controller 1 is in use by another program",
        _ => "vJoy device 1",
    };

    public bool Start()
    {
        if (Active) return true;
        var state = Probe();
        Status = Describe(state);
        if (state != VJoyState.Ready) return false;
        try
        {
            if (GetVJDStatus(Device) != 0 && !AcquireVJD(Device)) { Status = "could not take vJoy controller 1"; return false; }
            if (GetVJDAxisMax(Device, AxisThrottle, out int max) && max > 0) _max = max;
            Active = true;
            Send(0, 0, 0);
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            Status = Describe(VJoyState.NotInstalled);
            return false;
        }
    }

    /// <summary>Values are 0..1: 0 = pedal at rest, 1 = fully pressed.</summary>
    public void Send(double clutch, double brake, double throttle)
    {
        if (!Active) return;
        SetAxis(Scale(clutch), Device, AxisClutch);
        SetAxis(Scale(brake), Device, AxisBrake);
        SetAxis(Scale(throttle), Device, AxisThrottle);
        Sent++;
    }

    public void Stop()
    {
        if (!Active) return;
        Send(0, 0, 0);          // leave the virtual pedals released
        Active = false;
        RelinquishVJD(Device);
        Status = "off";
    }

    /// <summary>
    /// Creates virtual controller 1 with the three pedal axes, using vJoy's own configuration tool. Windows asks
    /// for administrator approval. This replaces any existing layout of vJoy controller 1.
    /// Returns false if the tool is missing or approval was declined.
    /// </summary>
    public static bool ConfigureDevice()
    {
        if (ConfigTool is not { } tool) return false;
        try
        {
            // One elevated command line, so there is a single approval prompt for both steps.
            using var p = Process.Start(new ProcessStartInfo("cmd.exe",
                $"/c \"\"{tool}\" enable on && \"{tool}\" {Device} -f -a rx ry rz -b 1\"")
                { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
            if (p is null) return false;
            p.WaitForExit(30000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }   // approval declined
    }

    int Scale(double v) => (int)Math.Round(Math.Clamp(v, 0, 1) * _max);

    [DllImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool vJoyEnabled();
    [DllImport(Dll)] static extern int GetVJDStatus(uint id);
    [DllImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetVJDAxisExist(uint id, uint axis);
    [DllImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetVJDAxisMax(uint id, uint axis, out int max);
    [DllImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool AcquireVJD(uint id);
    [DllImport(Dll)] static extern void RelinquishVJD(uint id);
    [DllImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetAxis(int value, uint id, uint axis);
}
