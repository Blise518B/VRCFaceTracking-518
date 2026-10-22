"""Loopback capture via Npcap's wpcap.dll (ctypes, no extra packages).

Usage: python capture.py <out.pcap> <seconds> <bpf filter>
Writes a standard pcap file and prints a packet count every 10 s.
"""
import ctypes as C
import os
import sys
import time

os.add_dll_directory(r"C:\Windows\System32\Npcap")
wpcap = C.CDLL(r"C:\Windows\System32\Npcap\wpcap.dll")


class pcap_if(C.Structure):
    pass


pcap_if._fields_ = [("next", C.POINTER(pcap_if)), ("name", C.c_char_p),
                    ("description", C.c_char_p), ("addresses", C.c_void_p), ("flags", C.c_uint)]


class timeval(C.Structure):
    _fields_ = [("tv_sec", C.c_long), ("tv_usec", C.c_long)]


class pcap_pkthdr(C.Structure):
    _fields_ = [("ts", timeval), ("caplen", C.c_uint32), ("len", C.c_uint32)]


class bpf_program(C.Structure):
    _fields_ = [("bf_len", C.c_uint), ("bf_insns", C.c_void_p)]


wpcap.pcap_findalldevs.argtypes = [C.POINTER(C.POINTER(pcap_if)), C.c_char_p]
wpcap.pcap_open_live.restype = C.c_void_p
wpcap.pcap_open_live.argtypes = [C.c_char_p, C.c_int, C.c_int, C.c_int, C.c_char_p]
wpcap.pcap_compile.argtypes = [C.c_void_p, C.POINTER(bpf_program), C.c_char_p, C.c_int, C.c_uint32]
wpcap.pcap_setfilter.argtypes = [C.c_void_p, C.POINTER(bpf_program)]
wpcap.pcap_geterr.restype = C.c_char_p
wpcap.pcap_geterr.argtypes = [C.c_void_p]
wpcap.pcap_dump_open.restype = C.c_void_p
wpcap.pcap_dump_open.argtypes = [C.c_void_p, C.c_char_p]
wpcap.pcap_next_ex.argtypes = [C.c_void_p, C.POINTER(C.POINTER(pcap_pkthdr)), C.POINTER(C.POINTER(C.c_ubyte))]
wpcap.pcap_dump.argtypes = [C.c_void_p, C.POINTER(pcap_pkthdr), C.POINTER(C.c_ubyte)]
wpcap.pcap_dump_flush.argtypes = [C.c_void_p]
wpcap.pcap_dump_close.argtypes = [C.c_void_p]
wpcap.pcap_close.argtypes = [C.c_void_p]
wpcap.pcap_set_buffer_size = getattr(wpcap, "pcap_setbuff", None)


def find_loopback():
    devs = C.POINTER(pcap_if)()
    err = C.create_string_buffer(256)
    if wpcap.pcap_findalldevs(C.byref(devs), err) != 0:
        sys.exit("findalldevs failed: " + err.value.decode())
    d = devs
    while d:
        name = d.contents.name.decode()
        if "Loopback" in name:
            return d.contents.name
        d = d.contents.next
    sys.exit("no loopback adapter")


def main():
    out, seconds, flt = sys.argv[1], float(sys.argv[2]), sys.argv[3]
    err = C.create_string_buffer(256)
    dev = find_loopback()
    h = wpcap.pcap_open_live(dev, 65535, 0, 50, err)
    if not h:
        sys.exit("open failed: " + err.value.decode())
    setbuff = getattr(wpcap, "pcap_setbuff", None)
    if setbuff:
        setbuff.argtypes = [C.c_void_p, C.c_int]
        setbuff(h, 64 * 1024 * 1024)
    prog = bpf_program()
    if wpcap.pcap_compile(h, C.byref(prog), flt.encode(), 1, 0xFFFFFFFF) != 0:
        sys.exit("compile failed: " + wpcap.pcap_geterr(h).decode())
    if wpcap.pcap_setfilter(h, C.byref(prog)) != 0:
        sys.exit("setfilter failed: " + wpcap.pcap_geterr(h).decode())
    dumper = wpcap.pcap_dump_open(h, out.encode())
    if not dumper:
        sys.exit("dump_open failed: " + wpcap.pcap_geterr(h).decode())

    hdr = C.POINTER(pcap_pkthdr)()
    data = C.POINTER(C.c_ubyte)()
    end = time.time() + seconds
    n = 0
    next_report = time.time() + 10
    print(f"capturing on {dev.decode()} for {seconds:.0f}s, filter: {flt}", flush=True)
    while time.time() < end:
        r = wpcap.pcap_next_ex(h, C.byref(hdr), C.byref(data))
        if r == 1:
            wpcap.pcap_dump(dumper, hdr, data)
            n += 1
        elif r < 0:
            print("pcap_next_ex error", r, wpcap.pcap_geterr(h).decode(), flush=True)
            break
        if time.time() >= next_report:
            wpcap.pcap_dump_flush(dumper)
            print(f"{time.strftime('%H:%M:%S')} packets={n}", flush=True)
            next_report += 10
    wpcap.pcap_dump_flush(dumper)
    wpcap.pcap_dump_close(dumper)
    wpcap.pcap_close(h)
    print(f"done, {n} packets", flush=True)


if __name__ == "__main__":
    main()
