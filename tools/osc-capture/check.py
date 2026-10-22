"""One-command health check of the running VRCFaceTracking output.

usage: python check.py [seconds=60] [out.pcap]

Records the module -> VRCFT link, VRCFT -> VRChat (:9000) and VRChat's own output
(:9001, received by the 518 Hub) from the loopback adapter, then prints the analysis and
how many of VRCFT's value changes VRChat confirmed. Needs Npcap with loopback support;
it only listens, so VRChat and the hub are not disturbed.
"""
import os
import re
import subprocess
import sys

here = os.path.dirname(os.path.abspath(__file__))
seconds = sys.argv[1] if len(sys.argv) > 1 else "60"
out = sys.argv[2] if len(sys.argv) > 2 else os.path.join(here, "check.pcap")

# The app's IPC port is chosen at start-up; the module logs it on its handshake
log = os.path.join(os.path.expanduser("~"), "Documents", "VRCFaceTracking", "latest.log")
ipc = None
try:
    with open(log, encoding="utf-8", errors="replace") as f:
        for line in f:
            m = re.search(r"Received ACK from host on port (\d+)", line)
            if m:
                ipc = m.group(1)
except OSError:
    pass

flt = "udp and (dst port 9000 or dst port 9001" + (f" or dst port {ipc}" if ipc else "") + ")"
if not ipc:
    print("module IPC port not found in latest.log - recording VRChat traffic only")

py = sys.executable
subprocess.run([py, os.path.join(here, "capture.py"), out, seconds, flt], check=True)
subprocess.run([py, os.path.join(here, "analyze.py"), out], check=True, cwd=here)
print("\n[confirmation] VRCFT value changes VRChat echoed back")
subprocess.run([py, os.path.join(here, "confirm.py"), out], check=True, cwd=here)
