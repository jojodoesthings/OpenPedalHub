"""Dump what the pedals and vJoy look like on this PC.

    python probe.py            descriptor info + vJoy state
    python probe.py --watch 8  also sample the pedals for 8 seconds
"""
import ctypes
import os
import sys
import time

import hid_device

VID, PID = 0x044F, 0xB678   # Thrustmaster "T-Pedals" (T3PM on USB)


def pedals():
    dev = hid_device.find(VID, PID)
    if not dev:
        print("pedals %04X:%04X not found" % (VID, PID))
        return
    c = dev.caps
    print("pedals: %r  usage %04X:%04X  input report %d bytes" % (
        dev.product, c.UsagePage, c.Usage, c.InputReportByteLength))
    for a in dev.axes:
        print("  axis %-6s usage 0x%02X  %2d bits  logical %d..%d" % (
            a.name, a.usage, a.bits, a.lmin, a.lmax))
    if "--watch" in sys.argv:
        secs = float(sys.argv[sys.argv.index("--watch") + 1])
        lo, hi, n, t0, last = {}, {}, 0, time.perf_counter(), None
        while time.perf_counter() - t0 < secs:
            v = dev.read()
            n += 1
            last = v
            for k, x in v.items():
                lo[k] = min(lo.get(k, x), x)
                hi[k] = max(hi.get(k, x), x)
        dt = time.perf_counter() - t0
        print("  %d reports in %.1fs = %.0f Hz" % (n, dt, n / dt))
        print("  last raw report:", dev.raw().hex(" "))
        for a in dev.axes:
            print("  %-6s last %6d   seen %d..%d" % (
                a.name, last.get(a.usage, -1), lo.get(a.usage, -1), hi.get(a.usage, -1)))
    dev.close()


def vjoy():
    path = r"C:\Program Files\vJoy\x64\vJoyInterface.dll"
    if not os.path.exists(path):
        print("vJoy: not installed")
        return
    dll = ctypes.WinDLL(path)
    print("vJoy: enabled=%s version=0x%X" % (bool(dll.vJoyEnabled()), dll.GetvJoyVersion()))
    names = {0: "OWNED", 1: "FREE", 2: "BUSY", 3: "MISSING", 4: "UNKNOWN"}
    for rid in range(1, 5):
        st = dll.GetVJDStatus(rid)
        axes = [n for u, n in hid_device.AXIS_NAMES.items() if dll.GetVJDAxisExist(rid, u)]
        print("  device %d: %-8s axes=%s" % (rid, names.get(st, st), ",".join(axes)))


if __name__ == "__main__":
    pedals()
    vjoy()
