"""pcap reader + OSC / VRCFT IPC decoders shared by the analysis scripts."""
import collections
import struct


def read_pcap(path):
    """Yields (t, sport, dport, payload) for IPv4/UDP packets on a DLT_NULL loopback capture."""
    with open(path, "rb") as f:
        gh = f.read(24)
        magic, = struct.unpack("<I", gh[:4])
        if magic != 0xA1B2C3D4:
            raise ValueError("unexpected pcap magic %x" % magic)
        linktype, = struct.unpack("<I", gh[20:24])
        while True:
            ph = f.read(16)
            if len(ph) < 16:
                return
            sec, usec, caplen, _ = struct.unpack("<IIII", ph)
            pkt = f.read(caplen)
            if len(pkt) < caplen:
                return
            off = 4 if linktype == 0 else 14 if linktype == 1 else 0
            ip = pkt[off:]
            if len(ip) < 20 or ip[0] >> 4 != 4 or ip[9] != 17:
                continue
            ihl = (ip[0] & 0x0F) * 4
            udp = ip[ihl:]
            sport, dport, ulen = struct.unpack(">HHH", udp[:6])
            yield sec + usec / 1e6, sport, dport, bytes(udp[8:ulen])


def _str(b, i):
    j = b.index(b"\0", i)
    s = b[i:j].decode("ascii", "replace")
    return s, (j + 4) & ~3


def parse_osc(b):
    """Returns list of (address, value). Handles nested bundles; ignores unknown tags."""
    out = []
    if b.startswith(b"#bundle\0"):
        i = 16
        while i + 4 <= len(b):
            n, = struct.unpack(">i", b[i:i + 4])
            out.extend(parse_osc(b[i + 4:i + 4 + n]))
            i += 4 + n
        return out
    try:
        addr, i = _str(b, 0)
        tags, i = _str(b, i)
    except ValueError:
        return out
    vals = []
    for t in tags[1:]:
        if t == "f":
            vals.append(struct.unpack(">f", b[i:i + 4])[0]); i += 4
        elif t == "i":
            vals.append(struct.unpack(">i", b[i:i + 4])[0]); i += 4
        elif t == "T":
            vals.append(True)
        elif t == "F":
            vals.append(False)
        elif t == "s":
            s, i = _str(b, i); vals.append(s)
        elif t == "b":
            n, = struct.unpack(">i", b[i:i + 4])
            vals.append(bytes(b[i + 4:i + 4 + n])); i += 4 + ((n + 3) & ~3)
        else:
            break
    out.append((addr, vals[0] if len(vals) == 1 else vals))
    return out


IPC_HEADER = ["MaxDil", "MinDil", "L_GazeX", "L_GazeY", "L_Pupil", "L_Open",
              "R_GazeX", "R_GazeY", "R_Pupil", "R_Open", "HeadYaw", "HeadPitch", "HeadRoll",
              "HeadX", "HeadY", "HeadZ"]


def parse_ipc_update(b):
    """Module -> app ReplyUpdate packet: returns float tuple (16 header + shapes) or None."""
    if len(b) < 12 + 16 * 4:
        return None
    size, = struct.unpack("<i", b[8:12])
    n = min(size, len(b) - 12) // 4
    if n < 16:
        return None
    return struct.unpack("<%df" % n, b[12:12 + n * 4])


def detect_ports(path):
    """(vrcft_send_port, vrchat_echo_port, ipc_port) guessed from traffic volume: VRCFT is the
    biggest sender to :9000, VRChat the biggest sender to :9001, and the IPC port is where
    ReplyUpdate-sized packets land. Ports change with every restart, hence the guessing."""
    to9000, to9001, ipc = collections.Counter(), collections.Counter(), collections.Counter()
    for _, sp, dp, p in read_pcap(path):
        if dp == 9000:
            to9000[sp] += len(p)
        elif dp == 9001:
            to9001[sp] += 1
        elif parse_ipc_update(p) and len(p) > 300:
            ipc[dp] += 1
    top = lambda c: c.most_common(1)[0][0] if c else None
    return top(to9000), top(to9001), top(ipc)
