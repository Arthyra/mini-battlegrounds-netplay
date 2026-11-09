"""Copy the prepared Map 102 client and point it at a LAN host."""
import argparse
import ipaddress
from pathlib import Path
import shutil
import struct

SOURCE = Path(__file__).resolve().parent / "client"
ASSEMBLY = Path("CrazyShooter_Data/Managed/Assembly-CSharp.dll")
LOGIN_PORT = 43120
LOGIN_PORT_IL = b"\x20" + struct.pack("<I", LOGIN_PORT) + b"\0\0"
DIAGNOSTIC = "Tried to Initialize the SteamAPI twice in one session!"


def prepare(destination, host):
    destination = destination.resolve()
    if destination.exists() or destination.is_relative_to(SOURCE):
        raise ValueError("Choose a new directory outside the existing test client.")
    data = bytearray((SOURCE / ASSEMBLY).read_bytes())
    metadata = data.index(b"BSJB")
    user_strings = data.index(b"#US\0", metadata, metadata + 256)
    string_heap, = struct.unpack_from("<I", data, user_strings - 8)
    heap_start = metadata + string_heap
    localhost = "127.0.0.1".encode("utf-16le")
    current = bytes((len(localhost) + 1,)) + localhost + b"\0"
    login_offsets = []
    offset = data.find(LOGIN_PORT_IL)
    while offset >= 5:
        if data[offset - 5] == 0x72 and data[offset - 1] == 0x70:
            token, = struct.unpack_from("<I", data, offset - 4)
            address = heap_start + (token & 0xFFFFFF)
            if data[address:address + len(current)] == current:
                login_offsets.append(offset - 4)
        offset = data.find(LOGIN_PORT_IL, offset + 1)
    if len(login_offsets) != 1:
        raise ValueError("Expected a single Map 102 loopback login endpoint.")

    original = DIAGNOSTIC.encode("utf-16le")
    slot = bytes((len(original) + 1,)) + original + b"\0"
    if data.count(slot) != 1:
        raise ValueError("Expected the original diagnostic string in the source client.")
    address_slot = data.index(slot)
    encoded = str(host).encode("utf-16le")
    replacement = bytes((len(encoded) + 1,)) + encoded + b"\0"
    data[address_slot:address_slot + len(slot)] = replacement.ljust(len(slot), b"\0")
    struct.pack_into("<I", data, login_offsets[0], 0x70000000 | (address_slot - heap_start))
    shutil.copytree(SOURCE, destination, ignore=shutil.ignore_patterns("*.log", "output_log.txt"))
    (destination / ASSEMBLY).write_bytes(data)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("host", type=ipaddress.IPv4Address)
    parser.add_argument("destination", type=Path)
    args = parser.parse_args()
    if not args.host.is_private or args.host.is_unspecified or args.host.is_link_local:
        parser.error("host must be a loopback or private LAN IPv4 address")
    try:
        prepare(args.destination, args.host)
    except (OSError, ValueError) as error:
        parser.exit(1, f"{error}\n")
    print(f"Prepared {args.destination.resolve()} -> {args.host}:{LOGIN_PORT}")
