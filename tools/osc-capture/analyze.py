"""Three-hop analysis: module IPC -> VRCFT, VRCFT -> VRChat (:9000), VRChat echo (:9001).

usage: python analyze.py capture.pcap [vrcft_send_port vrchat_echo_src_port ipc_port]
Ports are detected from the traffic when left out.
"""
import bisect
import collections
import statistics
import sys

from pcapread import read_pcap, parse_osc, parse_ipc_update, detect_ports

path = sys.argv[1]
SEND_PORT, ECHO_PORT, IPC_PORT = (map(int, sys.argv[2:5]) if len(sys.argv) > 4
                                  else detect_ports(path))
print(f"ports: VRCFT sends from {SEND_PORT}, VRChat echoes from {ECHO_PORT}, module IPC to {IPC_PORT}")

ipc = []                                   # (t, L_Open, R_Open)
sent = collections.defaultdict(list)       # addr -> [(t, v)]
echo = collections.defaultdict(list)
out_bursts = []                            # packet times of VRCFT -> 9000
msgs_per_packet = []
t_first = None
for t, sp, dp, p in read_pcap(path):
    t_first = t_first or t
    if dp == IPC_PORT:
        f = parse_ipc_update(p)
        if f:
            ipc.append((t, f[5], f[9]))
    elif dp == 9000 and sp == SEND_PORT:
        out_bursts.append(t)
        m = parse_osc(p)
        msgs_per_packet.append(len(m))
        for a, v in m:
            sent[a].append((t, v))
    elif dp == 9001 and sp == ECHO_PORT:
        for a, v in parse_osc(p):
            echo[a].append((t, v))

T = lambda t: t - t_first
print(f"IPC frames {len(ipc)}, VRCFT packets {len(out_bursts)}, sent addrs {len(sent)}, echoed addrs {len(echo)}")

# ---- 1. module input cadence
gaps = [(b[0] - a[0]) * 1000 for a, b in zip(ipc, ipc[1:])]
if gaps:
    print(f"\n[input] module frames: {len(ipc)/(ipc[-1][0]-ipc[0][0]):.1f}/s, gap median {statistics.median(gaps):.1f} ms, "
          f"p99 {sorted(gaps)[int(len(gaps)*.99)]:.1f} ms, max {max(gaps):.0f} ms; gaps>50ms: {sum(g>50 for g in gaps)}")
    # frozen openness while the other eye moves
    frozen = []
    run_start = None
    for i in range(1, len(ipc)):
        same_l = ipc[i][1] == ipc[i - 1][1]
        if same_l and 0.05 < ipc[i][1] < 0.95:
            run_start = run_start if run_start is not None else ipc[i - 1][0]
        else:
            if run_start is not None and ipc[i - 1][0] - run_start > 0.15:
                frozen.append((T(run_start), ipc[i - 1][0] - run_start, ipc[i - 1][1]))
            run_start = None
    print(f"[input] left openness held at a mid value (0.05..0.95) for >150 ms: {len(frozen)}", frozen[:8])

# ---- 2. VRCFT output cadence
bg = []
burst_starts = [out_bursts[0]]
for a, b in zip(out_bursts, out_bursts[1:]):
    if b - a > 0.003:
        burst_starts.append(b)
bg = [(b - a) * 1000 for a, b in zip(burst_starts, burst_starts[1:])]
print(f"\n[output] send ticks {len(burst_starts)/(out_bursts[-1]-out_bursts[0]):.1f}/s, gap median {statistics.median(bg):.1f} ms, "
      f"p99 {sorted(bg)[int(len(bg)*.99)]:.1f} ms, max {max(bg):.0f} ms; gaps>50ms {sum(g>50 for g in bg)}, >100ms {sum(g>100 for g in bg)}")
print(f"[output] messages per packet avg {statistics.mean(msgs_per_packet):.0f}, packets per tick avg {len(out_bursts)/len(burst_starts):.1f}")
tot = sum(len(v) for v in sent.values())
print(f"[output] total messages {tot} ({tot/60:.0f}/s)")
eye = sorted(a for a in sent if "EyeLid" in a)
for a in eye:
    vals = sent[a]
    changes = sum(1 for x, y in zip(vals, vals[1:]) if x[1] != y[1]) + 1
    print(f"   {a.split('/')[-1]:16s} sent {len(vals):6d}  real changes {changes:5d}  -> {len(vals)/max(changes,1):5.1f}x duplication")

# ---- 2b. native face tracking: one 102-byte /tracking/face/v1 blob per tick (byte = value * 255)
blobs = [v for _, v in sent.get("/tracking/face/v1", []) if isinstance(v, bytes)]
if blobs:
    bt = [t for t, v in sent["/tracking/face/v1"] if isinstance(v, bytes)]
    rate = (len(bt) - 1) / (bt[-1] - bt[0]) if len(bt) > 1 and bt[-1] > bt[0] else 0
    sizes = collections.Counter(len(v) for v in blobs)
    print(f"\n[native] /tracking/face/v1: {len(blobs)} blobs, {rate:.1f}/s, payload sizes {dict(sizes)} "
          f"(VRChat ignores any size but 102)")
    good = [v for v in blobs if len(v) == 102]
    for name, idx in (("EyeClosedRight", 8), ("EyeClosedLeft", 9), ("JawOpen", 36), ("MouthClosed", 43)):
        vals = [g[idx] / 255 for g in good]
        if vals:
            print(f"   {name:16s} min {min(vals):.2f}  max {max(vals):.2f}  "
                  f"changes {sum(1 for x, y in zip(vals, vals[1:]) if x != y)}")

# ---- 3. divergence: last value VRCFT sent vs last value VRChat echoed
def last_at(series, times, t):
    i = bisect.bisect_right(times, t) - 1
    return series[i][1] if i >= 0 else None

print("\n[divergence] periods where VRChat's echoed value != VRCFT's latest sent value (after 30 ms grace)")
worst = []
for a in sorted(sent):
    if a not in echo:
        continue
    s, e = sent[a], echo[a]
    st = [x[0] for x in s]
    et = [x[0] for x in e]
    # sample every 5 ms
    t = max(s[0][0], e[0][0]) + 0.05
    t_end = min(s[-1][0], e[-1][0])
    bad_since = None
    periods = []
    while t < t_end:
        sv = last_at(s, st, t - 0.03)   # what VRCFT had sent 30 ms ago
        ev = last_at(e, et, t)
        # VRCFT sent value newer than 30 ms ago may legitimately not be echoed yet
        sv_now = last_at(s, st, t)
        diverged = ev is not None and sv is not None and ev != sv and ev != sv_now
        if isinstance(ev, float) and isinstance(sv, float):
            diverged = diverged and abs(ev - sv) > 1e-3 and abs(ev - sv_now) > 1e-3
        if diverged:
            bad_since = bad_since or t
        elif bad_since:
            periods.append((T(bad_since), t - bad_since))
            bad_since = None
        t += 0.005
    if periods:
        long = [p for p in periods if p[1] >= 0.1]
        worst.append((max(p[1] for p in periods), a, len(periods), len(long), long[:5]))
worst.sort(reverse=True)
for mx, a, n, nl, ex in worst[:25]:
    print(f"   {a.split('parameters/')[-1]:44s} max {mx*1000:6.0f} ms, {n} periods, {nl} >=100ms  e.g. {[(round(s,2), int(d*1000)) for s,d in ex]}")
print(f"   addresses with divergence: {len(worst)} of {len(set(sent)&set(echo))}")

# ---- 4. echo rate vs send rate
print("\n[echo] messages echoed vs sent for eyelid bits")
for a in eye:
    print(f"   {a.split('/')[-1]:16s} sent {len(sent[a]):6d}  echoed {len(echo.get(a, [])):6d}")
