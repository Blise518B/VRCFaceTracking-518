# native-ft-stall-check

Offline check that native face tracking hands the face back to VRChat when tracking stalls
(FORK-NOTES.md, section 9, "When tracking stalls"). It needs no headset, no module and no
VRChat, and it sends nothing to VRChat's port: the sender targets this program's own socket.

```
dotnet run --project tools/native-ft-stall-check
```

It runs three parts and exits with 0 when every check passes:

1. **`NativeFrameGate` on a synthetic clock.** A stall pauses 500 ms plus five ticks after the
   last new frame. Our own 700 ms pause does not pause it. A lost module pauses on the same
   tick. An avatar loaded while tracking is stale gets nothing, and no neutral frame. A 450 ms
   hiccup passes.
2. **`ReplyUpdatePacket.TrackingChanged` on decoded IPC frames.** A module's first frame,
   repeats, a head pose change and a NaN that stays NaN are no new data. A one-ulp change is.
   Each slot owner only counts its own values.
3. **The real pipeline for 8 s.** Module frames go through `VrcftPacketDecoder` and
   `UnifiedLibManager.ApplyTrackingFrame`. `ParameterSenderService`'s 100 Hz loop runs
   `NativeFaceTrackingSender`, and `OscSendService` sends to a loopback UDP port this program
   records. Before the run, every parameter is reset for the native avatar the way an avatar
   load does it, so the blob is all that goes out. The timeline:

   | ms | module side |
   | --- | --- |
   | 0 | module A tracks (72 Hz tracker, answered at every 10 ms poll) |
   | 1500 | A's tracker stalls: A repeats its last values, its head pose keeps moving |
   | 3500 | A tracks again |
   | 4500 | A's face holds still: floats change by one ulp, the blob bytes don't |
   | 5500 | A's process dies: no answers |
   | 5700 | the supervisor notices: `TrackingFreshness.MarkLost()` |
   | 6500 | module B (respawned) answers with its start-up values |
   | 7000 | B tracks |

Reference run (2026-11-08), what reached the socket:

```
      23.0 -   2031.6 ms  live    195 blobs (97/s)
    2041.9             neutral blob x1 (payload all zero)
    2041.9 -   3510.3 ms  silent  (1468 ms)
    3510.3 -   5691.3 ms  live    211 blobs (96/s)
    5701.6             neutral blob x1 (payload all zero)
    5701.6 -   7002.8 ms  silent  (1301 ms)
    7002.8 -   7992.3 ms  live    96 blobs (96/s)
ALL 34 CHECKS PASSED
```

To see the same on the wire, record it with osc-capture while the check runs on a fixed port:

```
python tools/osc-capture/capture.py stall.pcap 14 "udp dst port 39555"
dotnet run --project tools/native-ft-stall-check -- --port 39555
```

Start the capture first; it prints `capturing on ...` when it is ready. The 2026-11-08 capture
had 504 datagrams, all 132-byte `/tracking/face/v1` blobs, 2 of them all zero, with silences
of 1468 ms and 1301 ms after them.

What this cannot show is VRChat's side: whether the avatar's face relaxes on the neutral
frame, and whether VRChat falls back to its own eye look and blinking once the blobs stop.
That needs an in-client check (see FORK-NOTES.md).
