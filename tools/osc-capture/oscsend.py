import socket, struct, sys
def pad(b): return b + b"\0" * (4 - len(b) % 4)
addr, val, port = sys.argv[1], sys.argv[2].lower() == "true", int(sys.argv[3])
msg = pad(addr.encode()) + pad(b"," + (b"T" if val else b"F"))
socket.socket(socket.AF_INET, socket.SOCK_DGRAM).sendto(msg, ("127.0.0.1", port))
print("sent", addr, val, "to", port)
