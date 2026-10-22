"""Share of VRCFT's bool transitions that VRChat confirmed (echo with that value before the next transition)."""
import collections, sys
from pcapread import read_pcap, parse_osc, detect_ports
path = sys.argv[1]
SEND, ECHO = (int(sys.argv[2]), int(sys.argv[3])) if len(sys.argv) > 3 else detect_ports(path)[:2]
sent = collections.defaultdict(list); echo = collections.defaultdict(list)
for t, sp, dp, p in read_pcap(path):
    if dp == 9000 and sp == SEND:
        for a, v in parse_osc(p): sent[a].append((t, v))
    elif dp == 9001 and sp == ECHO:
        for a, v in parse_osc(p): echo[a].append((t, v))
res = collections.defaultdict(lambda: [0, 0, 0.0])
for a, s in sent.items():
    if not isinstance(s[0][1], bool): continue
    e = echo.get(a, [])
    trans = [s[0]] + [y for x, y in zip(s, s[1:]) if y[1] != x[1]]
    for k, (t, v) in enumerate(trans):
        t_next = trans[k + 1][0] if k + 1 < len(trans) else t + 2
        conf = [et for et, ev in e if t <= et <= t_next + 0.03 and ev == v]
        kind = "bits" if a[-1].isdigit() else "single bools"
        r = res[kind]; r[0] += 1
        if not conf: r[1] += 1
        else:
            # how long until VRChat had it (first confirmation)
            r[2] = max(r[2], conf[0] - t)
for k, (n, lost, worst) in sorted(res.items()):
    print(f"  {k:13s} transitions {n:5d}, never confirmed {lost:4d} ({100*lost/max(n,1):4.1f}%), slowest confirmation {worst*1000:5.0f} ms")
