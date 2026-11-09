"""Use a local test identity in the dedicated map 102 Windows client."""
from pathlib import Path


def replace(data, original, replacement):
    if len(replacement) > len(original) or data.count(original) != 1:
        raise ValueError("The expected client instruction sequence does not match.")
    offset = data.index(original)
    data[offset:offset + len(original)] = replacement.ljust(len(original), b"\0")


def main():
    root = Path(__file__).resolve().parent
    assembly = root / "client/CrazyShooter_Data/Managed/Assembly-CSharp.dll"
    backup = root / "assembly-before-local-login.dll"
    if backup.exists():
        raise ValueError("The local login patch already has a backup.")
    original = assembly.read_bytes()
    data = bytearray(original)

    # Application.SetGlobalConsts: set Simplified Chinese without Steam's language API.
    replace(
        data,
        bytes.fromhex(
            "7E A6 09 00 04 1A 40 AE 00 00 00 72 B2 1F 00 70 73 CF 00 00 0A "
            "28 4B 00 00 2B 26 18 80 A7 09 00 04 28 9D 02 00 0A 0A 06 28 9E 02 00 0A "
            "3A 87 00 00 00 06 72 BE 1F 00 70 17 28 9F 02 00 0A 3A 0B 00 00 00 "
            "16 80 A7 09 00 04 38 6B 00 00 00 06 72 D0 1F 00 70 17 28 9F 02 00 0A "
            "3A 0B 00 00 00 17 80 A7 09 00 04 38 4F 00 00 00 06 72 E2 1F 00 70 "
            "17 28 9F 02 00 0A 3A 0B 00 00 00 19 80 A7 09 00 04 38 33 00 00 00 "
            "06 72 F2 1F 00 70 17 28 9F 02 00 0A 3A 0B 00 00 00 1A 80 A7 09 00 04 "
            "38 17 00 00 00 06 72 08 20 00 70 17 28 9F 02 00 0A 3A 06 00 00 00 "
            "1B 80 A7 09 00 04 2A"
        ),
        bytes.fromhex("16 80 A7 09 00 04") + bytes(179) + b"\x2a",
    )

    # GUIEntityLoginSteam.RequestLogin: keep the original request format and use
    # a fixed placeholder account. The local server issues unique player IDs.
    replace(
        data,
        bytes.fromhex(
            "28 47 03 00 06 39 6D 00 00 00 72 55 4B 00 70 28 E4 04 00 0A "
            "0A 12 00 FE 16 E0 00 00 01 6F BB 00 00 0A"
        ),
        bytes.fromhex("72 55 4B 00 70 72 55 4B 00 70"),
    )

    with backup.open("xb") as backup_file:
        backup_file.write(original)
    assembly.write_bytes(data)
    print("Patched the map 102 test client for local login.")


if __name__ == "__main__":
    main()
