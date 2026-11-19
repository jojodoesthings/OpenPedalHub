using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace OpenPedalHub;

/// <summary>
/// Passive, read-only listener for game controllers.
///
/// It uses the Windows Raw Input API: Windows hands this window a copy of each position report a controller
/// sends, exactly as it does for a game. This class never opens a device, and contains no call that can write
/// to one (no output reports, no feature reports, no firmware or calibration access).
///
/// It keeps track of every joystick-type controller that is plugged in. One of them can be chosen as the
/// "active" pedal set that drives the app; the others are only watched so that the set-up screen can work
/// out which device and axis a pedal is on.
/// </summary>
public sealed class PedalReader
{
    /// <summary>
    /// One axis of a controller. Code is what the rest of the app uses to refer to it: for the usual
    /// "Generic Desktop" axes (X, Y, Z, Rx, Ry, Rz, Slider, Dial) it is simply the HID usage number; for
    /// axes on another HID page (some pedals report as Accelerator / Brake / Clutch on the Simulation
    /// page) it is page * 65536 + usage.
    /// </summary>
    public sealed record Axis(int Code, ushort Page, ushort Usage, string Name, int Bits, long Min, long Max);

    public sealed class Device
    {
        public IntPtr Handle { get; init; }
        public string Path { get; init; } = "";
        public ushort Vid { get; init; }
        public ushort Pid { get; init; }
        public string Name { get; init; } = "";
        public List<Axis> Axes { get; } = [];
        /// <summary>Latest position of each axis, 0..1, keyed by axis code. Empty until the axis first reports.</summary>
        public Dictionary<int, double> Values { get; } = [];
        public long Reports { get; set; }
        internal IntPtr Preparsed;

        /// <summary>Vendor and product id, e.g. "044F:B678": what identifies a model of pedal set.</summary>
        public string Id => $"{Vid:X4}:{Pid:X4}";

        /// <summary>
        /// Windows device instance path. It identifies this particular unit on this PC, so it is used only
        /// locally (to hide the device from games) and must never be written to a report or log.
        /// </summary>
        public string InstancePath
        {
            get
            {
                // \\?\HID#VID_xxxx&PID_xxxx#<instance>#{guid}  ->  HID\VID_xxxx&PID_xxxx\<instance>
                string s = Path.StartsWith(@"\\?\") ? Path[4..] : Path;
                int guid = s.LastIndexOf("#{", StringComparison.Ordinal);
                if (guid >= 0) s = s[..guid];
                return s.Replace('#', '\\');
            }
        }
    }

    readonly Dictionary<IntPtr, Device> _devices = [];
    IntPtr _buffer;
    uint _bufferSize;

    /// <summary>Every joystick-type controller currently plugged in (our own virtual controller excluded).</summary>
    public IReadOnlyCollection<Device> Devices => _devices.Values;

    /// <summary>The pedal set that drives the app, chosen by the app from Devices; null if none.</summary>
    public Device? Active { get; set; }

    public bool Connected => Active is not null;
    public IReadOnlyList<Axis> Axes => Active?.Axes ?? (IReadOnlyList<Axis>)[];
    public string InstancePath => Active?.InstancePath ?? "";

    /// <summary>Raised when a controller is plugged in or removed.</summary>
    public event Action? ConnectionChanged;
    /// <summary>Raised after each batch of position reports from the active pedal set, at its own report rate.</summary>
    public event Action? Report;

    /// <summary>Latest position of an axis on the active pedal set, 0..1. False until that axis has reported.</summary>
    public bool TryGet(int code, out double value)
    {
        value = 0;
        return Active is { } d && d.Values.TryGetValue(code, out value);
    }

    public void Attach(Window window)
    {
        // Looked up by window handle, which also works while the window is hidden (started in the tray).
        var source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)!;
        source.AddHook(WndProc);
        // Joystick, gamepad and multi-axis controllers on the Generic Desktop page.
        var wanted = WantedUsages.Select(u => new RAWINPUTDEVICE
        {
            usUsagePage = 1, usUsage = u, dwFlags = RIDEV_INPUTSINK | RIDEV_DEVNOTIFY, hwndTarget = source.Handle,
        }).ToArray();
        RegisterRawInputDevices(wanted, (uint)wanted.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());

        uint count = 0, size = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
        GetRawInputDeviceList(null, ref count, size);
        if (count == 0) return;
        var list = new RAWINPUTDEVICELIST[count];
        if (GetRawInputDeviceList(list, ref count, size) == uint.MaxValue) return;
        foreach (var d in list)
            if (d.dwType == RIM_TYPEHID) TryAdd(d.hDevice);
    }

    public string Describe()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"controllers seen: {_devices.Count}");
        foreach (var d in _devices.Values)
        {
            sb.AppendLine($"{(d == Active ? "ACTIVE " : "       ")}{d.Id}  \"{d.Name}\"  reports: {d.Reports}");
            foreach (var a in d.Axes)
                sb.AppendLine($"         axis {a.Name,-11} page 0x{a.Page:X2} usage 0x{a.Usage:X2}  {a.Bits} bits  range {a.Min}..{a.Max}  value {(d.Values.TryGetValue(a.Code, out var v) ? v.ToString("0.000") : "none yet")}");
        }
        return sb.ToString();
    }

    static readonly ushort[] WantedUsages = [0x04, 0x05, 0x08];

    bool TryAdd(IntPtr handle)
    {
        if (_devices.ContainsKey(handle)) return false;

        // What kind of device is it, and whose?
        const int infoSize = 32;
        IntPtr info = Marshal.AllocHGlobal(infoSize);
        ushort vid, pid, page, usage;
        try
        {
            Marshal.WriteInt32(info, infoSize);   // cbSize
            uint size = infoSize;
            if (GetRawInputDeviceInfoW(handle, RIDI_DEVICEINFO, info, ref size) == uint.MaxValue) return false;
            if (Marshal.ReadInt32(info, 4) != RIM_TYPEHID) return false;
            vid = (ushort)Marshal.ReadInt32(info, 8);
            pid = (ushort)Marshal.ReadInt32(info, 12);
            page = (ushort)Marshal.ReadInt16(info, 20);
            usage = (ushort)Marshal.ReadInt16(info, 22);
        }
        finally { Marshal.FreeHGlobal(info); }
        if (page != 1 || !WantedUsages.Contains(usage)) return false;
        if (vid == 0x1234 && pid == 0xBEAD) return false;   // vJoy: our own output, which would echo every pedal press

        // The parsed report descriptor: tells us where each axis sits inside a report.
        uint ppSize = 0;
        GetRawInputDeviceInfoW(handle, RIDI_PREPARSEDDATA, IntPtr.Zero, ref ppSize);
        if (ppSize == 0) return false;
        IntPtr preparsed = Marshal.AllocHGlobal((int)ppSize);
        if (GetRawInputDeviceInfoW(handle, RIDI_PREPARSEDDATA, preparsed, ref ppSize) == uint.MaxValue
            || HidP_GetCaps(preparsed, out HIDP_CAPS caps) != HIDP_STATUS_SUCCESS)
        {
            Marshal.FreeHGlobal(preparsed);
            return false;
        }

        var device = new Device { Handle = handle, Path = PathOf(handle), Vid = vid, Pid = pid, Name = FriendlyName(vid, pid), Preparsed = preparsed };
        ushort n = caps.NumberInputValueCaps;
        if (n > 0)
        {
            var valueCaps = new HIDP_VALUE_CAPS[n];
            HidP_GetValueCaps(HIDP_INPUT, valueCaps, ref n, preparsed);
            foreach (var c in valueCaps.Take(n))
            {
                ushort last = c.IsRange != 0 ? c.UsageMax : c.UsageMin;
                for (ushort u = c.UsageMin; u <= last && u >= c.UsageMin; u++)
                {
                    if (!IsAxis(c.UsagePage, u)) continue;
                    int code = CodeOf(c.UsagePage, u);
                    if (device.Axes.Any(a => a.Code == code)) continue;
                    long min = c.LogicalMin, max = c.LogicalMax;
                    if (max <= min) { min = 0; max = (1L << c.BitSize) - 1; }   // descriptor quirk: treat as unsigned
                    device.Axes.Add(new Axis(code, c.UsagePage, u, AxisName(code), c.BitSize, min, max));
                }
            }
        }
        device.Axes.Sort((a, b) => a.Code.CompareTo(b.Code));
        _devices[handle] = device;
        return true;
    }

    void Remove(IntPtr handle)
    {
        if (!_devices.Remove(handle, out var device)) return;
        if (device.Preparsed != IntPtr.Zero) Marshal.FreeHGlobal(device.Preparsed);
        if (Active == device) Active = null;
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_INPUT) OnInput(lParam);
        else if (msg == WM_INPUT_DEVICE_CHANGE)
        {
            bool changed = wParam == 1 ? TryAdd(lParam) : _devices.ContainsKey(lParam);   // 1 = plugged in, 2 = removed
            if (wParam == 2) Remove(lParam);
            if (changed) ConnectionChanged?.Invoke();
        }
        return IntPtr.Zero;
    }

    void OnInput(IntPtr rawInput)
    {
        if (_devices.Count == 0) return;
        uint header = (uint)Marshal.SizeOf<RAWINPUTHEADER>(), size = 0;
        GetRawInputData(rawInput, RID_INPUT, IntPtr.Zero, ref size, header);
        if (size == 0) return;
        if (size > _bufferSize)
        {
            if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
            _buffer = Marshal.AllocHGlobal((int)size);
            _bufferSize = size;
        }
        if (GetRawInputData(rawInput, RID_INPUT, _buffer, ref size, header) != size) return;
        var head = Marshal.PtrToStructure<RAWINPUTHEADER>(_buffer);
        if (head.dwType != RIM_TYPEHID || !_devices.TryGetValue(head.hDevice, out var device)) return;

        int reportSize = Marshal.ReadInt32(_buffer, (int)header);
        int reportCount = Marshal.ReadInt32(_buffer, (int)header + 4);
        for (int i = 0; i < reportCount; i++)
        {
            IntPtr report = _buffer + (int)header + 8 + i * reportSize;
            foreach (var a in device.Axes)
            {
                if (HidP_GetUsageValue(HIDP_INPUT, a.Page, 0, a.Usage, out uint bits, device.Preparsed, report, (uint)reportSize) != HIDP_STATUS_SUCCESS) continue;
                long raw = bits;
                if (a.Min < 0 && a.Bits < 32 && (raw & (1L << (a.Bits - 1))) != 0) raw -= 1L << a.Bits;   // sign-extend
                device.Values[a.Code] = Math.Clamp((raw - a.Min) / (double)(a.Max - a.Min), 0, 1);
            }
            device.Reports++;
        }
        if (device == Active) Report?.Invoke();
    }

    static string PathOf(IntPtr device)
    {
        uint chars = 0;
        GetRawInputDeviceInfoW(device, RIDI_DEVICENAME, IntPtr.Zero, ref chars);
        if (chars == 0) return "";
        IntPtr buf = Marshal.AllocHGlobal((int)chars * 2 + 2);
        try
        {
            return GetRawInputDeviceInfoW(device, RIDI_DEVICENAME, buf, ref chars) == uint.MaxValue
                ? "" : Marshal.PtrToStringUni(buf) ?? "";
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>
    /// The name Windows shows for this controller in "Set up USB game controllers". Read from the registry,
    /// so the device itself is still never opened.
    /// </summary>
    static string FriendlyName(ushort vid, ushort pid)
    {
        string key = $@"System\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\VID_{vid:X4}&PID_{pid:X4}";
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var k = hive.OpenSubKey(key);
                if (k?.GetValue("OEMName") is string name && name.Trim().Length > 0) return name.Trim();
            }
            catch { /* fall through to the generic name */ }
        }
        return $"Controller {vid:X4}:{pid:X4}";
    }

    static readonly string[] DesktopAxes = ["X", "Y", "Z", "Rx", "Ry", "Rz", "Slider", "Dial"];
    static readonly Dictionary<int, string> SimulationAxes = new()
    {
        [0xBA] = "Rudder", [0xBB] = "Throttle", [0xC4] = "Accelerator", [0xC5] = "Brake", [0xC6] = "Clutch", [0xC7] = "Shifter", [0xC8] = "Steering",
    };

    /// <summary>Which reported values count as axes: the eight Generic Desktop axes, and the Simulation-page driving controls.</summary>
    static bool IsAxis(ushort page, ushort usage) =>
        (page == 0x01 && usage >= 0x30 && usage <= 0x37) || (page == 0x02 && SimulationAxes.ContainsKey(usage));

    static int CodeOf(ushort page, ushort usage) => page == 0x01 ? usage : (page << 16) | usage;

    /// <summary>A readable name for an axis code, e.g. "Rz" or "Brake".</summary>
    public static string AxisName(int code)
    {
        int page = code >> 16, usage = code & 0xFFFF;
        if (page == 0 && usage >= 0x30 && usage <= 0x37) return DesktopAxes[usage - 0x30];
        if (page == 0x02 && SimulationAxes.TryGetValue(usage, out var name)) return name;
        return page == 0 ? $"0x{usage:X2}" : $"{page:X2}:{usage:X2}";
    }

    // ---- Windows API (input side only) ----
    const int WM_INPUT = 0x00FF, WM_INPUT_DEVICE_CHANGE = 0x00FE;
    const uint RIDEV_INPUTSINK = 0x100, RIDEV_DEVNOTIFY = 0x2000;
    const uint RID_INPUT = 0x10000003, RIDI_PREPARSEDDATA = 0x20000005, RIDI_DEVICENAME = 0x20000007, RIDI_DEVICEINFO = 0x2000000b;
    const uint RIM_TYPEHID = 2;
    const int HIDP_INPUT = 0, HIDP_STATUS_SUCCESS = 0x00110000;

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTDEVICE { public ushort usUsagePage, usUsage; public uint dwFlags; public IntPtr hwndTarget; }

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTDEVICELIST { public IntPtr hDevice; public uint dwType; }

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTHEADER { public uint dwType, dwSize; public IntPtr hDevice, wParam; }

    [StructLayout(LayoutKind.Sequential)]
    struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
            NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct HIDP_VALUE_CAPS
    {
        public ushort UsagePage;
        public byte ReportID, IsAlias;
        public ushort BitField, LinkCollection, LinkUsage, LinkUsagePage;
        public byte IsRange, IsStringRange, IsDesignatorRange, IsAbsolute, HasNull, Reserved;
        public ushort BitSize, ReportCount, R1, R2, R3, R4, R5;
        public uint UnitsExp, Units;
        public int LogicalMin, LogicalMax, PhysicalMin, PhysicalMax;
        public ushort UsageMin, UsageMax, StringMin, StringMax, DesignatorMin, DesignatorMax, DataIndexMin, DataIndexMax;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);
    [DllImport("user32.dll")]
    static extern uint GetRawInputDeviceList([Out] RAWINPUTDEVICELIST[]? list, ref uint count, uint size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint GetRawInputDeviceInfoW(IntPtr device, uint command, IntPtr data, ref uint size);
    [DllImport("user32.dll")]
    static extern uint GetRawInputData(IntPtr rawInput, uint command, IntPtr data, ref uint size, uint headerSize);
    [DllImport("hid.dll")]
    static extern int HidP_GetCaps(IntPtr preparsed, out HIDP_CAPS caps);
    [DllImport("hid.dll")]
    static extern int HidP_GetValueCaps(int reportType, [Out] HIDP_VALUE_CAPS[] caps, ref ushort length, IntPtr preparsed);
    [DllImport("hid.dll")]
    static extern int HidP_GetUsageValue(int reportType, ushort usagePage, ushort linkCollection, ushort usage,
        out uint value, IntPtr preparsed, IntPtr report, uint reportLength);
}
