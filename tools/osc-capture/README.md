# osc-capture

Listens to the loopback adapter (Npcap, loopback support on, no admin needed) and checks
whether face tracking reaches VRChat intact. Nothing binds VRChat's or the hub's ports,
so it is safe to run mid-session.

```
python check.py 60
```

It reports, for one recording:

- **[input]** module -> VRCFT frame rate and gaps (the headset/module side).
- **[output]** VRCFT -> VRChat tick rate, gaps, messages per second, and how many times
  each eyelid bit goes out per real change (about 1x; a little more from the re-send of
  settled values).
- **[divergence]** how long VRChat's echoed value differed from what VRCFT last sent.
  Anything at or above 100 ms is a value that got stuck.
- **[confirmation]** the share of VRCFT's bool changes VRChat echoed back.
- **[native]** only for an avatar on VRChat's native face tracking: `/tracking/face/v1`
  blobs per second, their payload sizes (VRChat drops any blob that isn't 102 bytes) and
  the range of a few keys.

Reference numbers (2026-10-15, Quest Pro over Virtual Desktop, OSCm/Binary avatar):

| | before the bit-leak fix | after |
| --- | --- | --- |
| messages/s to VRChat | 16,224 | 1,454 |
| copies of each bit per change | 108 | 1.2-1.7 |
| changes VRChat never confirmed | 24-40% | 0.1-0.2% |
| longest wrong value | 8,015 ms | 75 ms |

`oscsend.py /vrcft/settings/resendUnchanged false <VRCFT OSC port>` switches the re-send
off for A/B tests (the port is in latest.log: "Successfully connected to remote endpoint").
From Git Bash, set `MSYS_NO_PATHCONV=1` first or the address is rewritten into a path.

The scripts run on the standard library only. `capture.py` drives Npcap's `wpcap.dll`
through ctypes; the others read the .pcap it writes.
