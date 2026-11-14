"""Check that every machine-local input required by the release build is
present and current, then print the exact build commands.

Inputs (none of them are in git):
  MBG_PATCH_INPUT   pristine unpatched Assembly-CSharp.dll
  MBG_MONO_CECIL    Mono.Cecil 0.11.6
  MBG_CLIENT_DIR    prepared public client directory (base runtime)
  MBG_UPDATE_SIGNING_KEY  RSA private PEM matching the launcher public key
  dist/server/server.exe  freshly built (PyInstaller server.spec)

Usage: python tools/check-build-inputs.py
"""
import os
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

CHECKS = [
    ("MBG_PATCH_INPUT", "pristine Assembly-CSharp.dll"),
    ("MBG_MONO_CECIL", "Mono.Cecil 0.11.6"),
    ("MBG_CLIENT_DIR", "prepared public client directory"),
    ("MBG_UPDATE_SIGNING_KEY", "RSA update signing key"),
]


def main():
    missing = []
    for var, purpose in CHECKS:
        value = os.environ.get(var)
        path = Path(value) if value else None
        ok = path is not None and path.exists()
        print(("OK   " if ok else "MISS ") + f"{var}: {purpose}" + (f" ({path})" if path else ""))
        if not ok:
            missing.append(var)
    server = ROOT / "dist/server/server.exe"
    source = ROOT / "server.py"
    # PyInstaller compresses the bytecode, so plaintext probes do not work.
    # Staleness heuristic: the exe must be newer than the server source.
    fresh = (server.is_file() and source.is_file()
             and server.stat().st_mtime >= source.stat().st_mtime)
    print(("OK   " if fresh else "STALE") + " dist/server/server.exe (newer than server.py)")
    if not fresh:
        missing.append("server.exe rebuild: python -m PyInstaller --noconfirm build/pyinstaller/server.spec, then smoke-run it")
    client = ROOT / "client/CrazyShooter_Data/Managed/Assembly-CSharp.dll"
    lan = client.read_bytes() if client.is_file() else b""
    print(("OK   " if b"DiscoveryEndpoints" not in lan and len(lan) else "CHECK") +
          f" client/ Assembly-CSharp.dll ({len(lan)} bytes; rebuild with PatchLanCombat after patch-source changes)")
    if missing:
        print("\nMISSING INPUTS:")
        for item in missing:
            print("  " + item)
        sys.exit(1)
    print("\nAll inputs present. Build:")
    print("  python build-release.py --version X.Y.Z --client <MBG_CLIENT_DIR> "
          "--host <MBG_ECS_HOST> --update-base-url https://<host>/mbg-update "
          "--signing-key <MBG_UPDATE_SIGNING_KEY>")


if __name__ == "__main__":
    main()
