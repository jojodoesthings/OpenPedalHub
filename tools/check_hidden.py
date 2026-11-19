"""Can an ordinary program (like a game) open the real T-Pedals right now? Read-only check."""
import sys
import os
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))   # hid_device.py lives one folder up
import hid_device

found = [p for p in hid_device.list_paths() if "vid_044f&pid_b678" in p.lower()]
if not found:
    print("T-Pedals: not listed at all for ordinary programs (hidden)")
for p in found:
    try:
        d = hid_device.HidDevice(p)
        print("T-Pedals: VISIBLE to ordinary programs (%d axes)" % len(d.axes))
        d.close()
    except OSError as e:
        print("T-Pedals: listed but cannot be opened (hidden), error", e.args[0])
