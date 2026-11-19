"""Raw HID access on Windows through ctypes (no third-party packages).

Enumerates HID devices, reads their axis layout from the report descriptor
(via the HID parser in hid.dll) and reads input reports at the device's own
report rate and native resolution.
"""
import ctypes
from ctypes import wintypes as wt

setupapi = ctypes.WinDLL("setupapi", use_last_error=True)
hid = ctypes.WinDLL("hid", use_last_error=True)
kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)

GENERIC_READ = 0x80000000
FILE_SHARE_READ, FILE_SHARE_WRITE = 1, 2
OPEN_EXISTING = 3
INVALID_HANDLE = ctypes.c_void_p(-1).value
DIGCF_PRESENT, DIGCF_DEVICEINTERFACE = 0x02, 0x10
HIDP_INPUT = 0
HIDP_STATUS_SUCCESS = 0x00110000

AXIS_NAMES = {0x30: "X", 0x31: "Y", 0x32: "Z", 0x33: "Rx", 0x34: "Ry",
              0x35: "Rz", 0x36: "Slider", 0x37: "Dial"}


class GUID(ctypes.Structure):
    _fields_ = [("Data1", wt.DWORD), ("Data2", wt.WORD), ("Data3", wt.WORD),
                ("Data4", ctypes.c_ubyte * 8)]


class SP_DEVICE_INTERFACE_DATA(ctypes.Structure):
    _fields_ = [("cbSize", wt.DWORD), ("InterfaceClassGuid", GUID),
                ("Flags", wt.DWORD), ("Reserved", ctypes.c_void_p)]


class HIDD_ATTRIBUTES(ctypes.Structure):
    _fields_ = [("Size", wt.ULONG), ("VendorID", wt.USHORT),
                ("ProductID", wt.USHORT), ("VersionNumber", wt.USHORT)]


class HIDP_CAPS(ctypes.Structure):
    _fields_ = [("Usage", wt.USHORT), ("UsagePage", wt.USHORT),
                ("InputReportByteLength", wt.USHORT),
                ("OutputReportByteLength", wt.USHORT),
                ("FeatureReportByteLength", wt.USHORT),
                ("Reserved", wt.USHORT * 17),
                ("NumberLinkCollectionNodes", wt.USHORT),
                ("NumberInputButtonCaps", wt.USHORT),
                ("NumberInputValueCaps", wt.USHORT),
                ("NumberInputDataIndices", wt.USHORT),
                ("NumberOutputButtonCaps", wt.USHORT),
                ("NumberOutputValueCaps", wt.USHORT),
                ("NumberOutputDataIndices", wt.USHORT),
                ("NumberFeatureButtonCaps", wt.USHORT),
                ("NumberFeatureValueCaps", wt.USHORT),
                ("NumberFeatureDataIndices", wt.USHORT)]


class HIDP_VALUE_CAPS(ctypes.Structure):
    _fields_ = [("UsagePage", wt.USHORT), ("ReportID", ctypes.c_ubyte),
                ("IsAlias", ctypes.c_ubyte), ("BitField", wt.USHORT),
                ("LinkCollection", wt.USHORT), ("LinkUsage", wt.USHORT),
                ("LinkUsagePage", wt.USHORT), ("IsRange", ctypes.c_ubyte),
                ("IsStringRange", ctypes.c_ubyte),
                ("IsDesignatorRange", ctypes.c_ubyte),
                ("IsAbsolute", ctypes.c_ubyte), ("HasNull", ctypes.c_ubyte),
                ("Reserved", ctypes.c_ubyte), ("BitSize", wt.USHORT),
                ("ReportCount", wt.USHORT), ("Reserved2", wt.USHORT * 5),
                ("UnitsExp", wt.ULONG), ("Units", wt.ULONG),
                ("LogicalMin", wt.LONG), ("LogicalMax", wt.LONG),
                ("PhysicalMin", wt.LONG), ("PhysicalMax", wt.LONG),
                ("UsageMin", wt.USHORT), ("UsageMax", wt.USHORT),
                ("u", wt.USHORT * 6)]


H = ctypes.c_void_p
setupapi.SetupDiGetClassDevsW.restype = H
setupapi.SetupDiGetClassDevsW.argtypes = [ctypes.POINTER(GUID), wt.LPCWSTR, H, wt.DWORD]
setupapi.SetupDiEnumDeviceInterfaces.argtypes = [
    H, H, ctypes.POINTER(GUID), wt.DWORD, ctypes.POINTER(SP_DEVICE_INTERFACE_DATA)]
setupapi.SetupDiGetDeviceInterfaceDetailW.argtypes = [
    H, ctypes.POINTER(SP_DEVICE_INTERFACE_DATA), H, wt.DWORD,
    ctypes.POINTER(wt.DWORD), H]
setupapi.SetupDiDestroyDeviceInfoList.argtypes = [H]
kernel32.CreateFileW.restype = H
kernel32.CreateFileW.argtypes = [wt.LPCWSTR, wt.DWORD, wt.DWORD, H, wt.DWORD, wt.DWORD, H]
kernel32.ReadFile.argtypes = [H, H, wt.DWORD, ctypes.POINTER(wt.DWORD), H]
kernel32.CloseHandle.argtypes = [H]
kernel32.CancelIoEx.argtypes = [H, H]
hid.HidD_GetHidGuid.argtypes = [ctypes.POINTER(GUID)]
hid.HidD_GetAttributes.argtypes = [H, ctypes.POINTER(HIDD_ATTRIBUTES)]
hid.HidD_GetAttributes.restype = ctypes.c_ubyte
hid.HidD_GetProductString.argtypes = [H, H, wt.ULONG]
hid.HidD_GetProductString.restype = ctypes.c_ubyte
hid.HidD_GetPreparsedData.argtypes = [H, ctypes.POINTER(H)]
hid.HidD_GetPreparsedData.restype = ctypes.c_ubyte
hid.HidD_FreePreparsedData.argtypes = [H]
hid.HidP_GetCaps.argtypes = [H, ctypes.POINTER(HIDP_CAPS)]
hid.HidP_GetCaps.restype = wt.LONG
hid.HidP_GetValueCaps.argtypes = [ctypes.c_int, H, ctypes.POINTER(wt.USHORT), H]
hid.HidP_GetValueCaps.restype = wt.LONG
hid.HidP_GetUsageValue.argtypes = [ctypes.c_int, wt.USHORT, wt.USHORT, wt.USHORT,
                                   ctypes.POINTER(wt.ULONG), H, H, wt.ULONG]
hid.HidP_GetUsageValue.restype = wt.LONG


def list_paths():
    """Device interface paths of every present HID device."""
    guid = GUID()
    hid.HidD_GetHidGuid(ctypes.byref(guid))
    info = setupapi.SetupDiGetClassDevsW(ctypes.byref(guid), None, None,
                                         DIGCF_PRESENT | DIGCF_DEVICEINTERFACE)
    paths = []
    try:
        index = 0
        while True:
            iface = SP_DEVICE_INTERFACE_DATA()
            iface.cbSize = ctypes.sizeof(iface)
            if not setupapi.SetupDiEnumDeviceInterfaces(
                    info, None, ctypes.byref(guid), index, ctypes.byref(iface)):
                break
            index += 1
            need = wt.DWORD(0)
            setupapi.SetupDiGetDeviceInterfaceDetailW(
                info, ctypes.byref(iface), None, 0, ctypes.byref(need), None)
            buf = ctypes.create_string_buffer(need.value)
            # cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W: 8 on 64-bit, 6 on 32-bit
            ctypes.cast(buf, ctypes.POINTER(wt.DWORD))[0] = (
                8 if ctypes.sizeof(ctypes.c_void_p) == 8 else 6)
            if setupapi.SetupDiGetDeviceInterfaceDetailW(
                    info, ctypes.byref(iface), buf, need, None, None):
                paths.append(ctypes.wstring_at(ctypes.addressof(buf) + 4))
    finally:
        setupapi.SetupDiDestroyDeviceInfoList(info)
    return paths


class Axis:
    def __init__(self, cap):
        self.page = cap.UsagePage
        self.usage = cap.UsageMin
        self.bits = cap.BitSize
        self.lmin = cap.LogicalMin
        self.lmax = cap.LogicalMax
        if self.lmax <= self.lmin:          # descriptor quirk: treat as unsigned
            self.lmin, self.lmax = 0, (1 << self.bits) - 1
        self.name = AXIS_NAMES.get(self.usage, "0x%02X" % self.usage)


class HidDevice:
    """One opened HID device. Use read() in a loop to get axis values."""

    def __init__(self, path, access=GENERIC_READ):
        self.path = path
        self.handle = kernel32.CreateFileW(
            path, access, FILE_SHARE_READ | FILE_SHARE_WRITE, None,
            OPEN_EXISTING, 0, None)
        if self.handle in (None, INVALID_HANDLE):
            self.handle = None
            raise OSError(ctypes.get_last_error(), "cannot open " + path)
        attr = HIDD_ATTRIBUTES()
        attr.Size = ctypes.sizeof(attr)
        hid.HidD_GetAttributes(self.handle, ctypes.byref(attr))
        self.vid, self.pid = attr.VendorID, attr.ProductID
        name = ctypes.create_unicode_buffer(128)
        hid.HidD_GetProductString(self.handle, name, ctypes.sizeof(name))
        self.product = name.value
        self.pp = H()
        if not hid.HidD_GetPreparsedData(self.handle, ctypes.byref(self.pp)):
            raise OSError("no HID descriptor for " + path)
        self.caps = HIDP_CAPS()
        hid.HidP_GetCaps(self.pp, ctypes.byref(self.caps))
        self.axes = []
        n = wt.USHORT(self.caps.NumberInputValueCaps)
        if n.value:
            arr = (HIDP_VALUE_CAPS * n.value)()
            hid.HidP_GetValueCaps(HIDP_INPUT, arr, ctypes.byref(n), self.pp)
            self.axes = [Axis(c) for c in arr[:n.value] if c.UsagePage == 1]
        self._len = self.caps.InputReportByteLength
        self._buf = ctypes.create_string_buffer(max(self._len, 1))
        self._val = wt.ULONG()

    def read(self):
        """Block until the next input report; return {usage: raw value}."""
        got = wt.DWORD()
        if not kernel32.ReadFile(self.handle, self._buf, self._len,
                                 ctypes.byref(got), None):
            raise OSError(ctypes.get_last_error(), "read failed (unplugged?)")
        out = {}
        for a in self.axes:
            if hid.HidP_GetUsageValue(HIDP_INPUT, a.page, 0, a.usage,
                                      ctypes.byref(self._val), self.pp,
                                      self._buf, got.value) == HIDP_STATUS_SUCCESS:
                out[a.usage] = self._val.value
        return out

    def raw(self):
        return self._buf.raw[:self._len]

    def close(self):
        if self.handle:
            kernel32.CancelIoEx(self.handle, None)
            kernel32.CloseHandle(self.handle)
            self.handle = None
        if self.pp:
            hid.HidD_FreePreparsedData(self.pp)
            self.pp = H()


def find(vid, pid):
    """Open the first HID device matching vid/pid, or return None."""
    tag = "vid_%04x&pid_%04x" % (vid, pid)
    for p in list_paths():
        if tag in p.lower():
            return HidDevice(p)
    return None
