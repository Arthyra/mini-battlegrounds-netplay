"""Assemble the one-shot Mod installer zip for players.

Inputs (all machine-local, none committed):
  - repo client\CrazyShooter_Data\Managed\  : patched Assembly-CSharp.dll +
    LanRoomUI.dll built by tools/rebuild-client-dlls.py
  - repo dist\server\                       : PyInstaller server.exe + the
    three extracted runtime-data JSON files
  - repo dist\rel-verify\Start Game.exe / Updater.exe : launcher built by
    build-friend-zip.py (or pass --launcher-dir)
  - repo tools\PatchLanCombat.exe and tools\Mono.Cecil.dll : the patch tool

Output:
  dist\mbg-lan-mod-setup-<version>.zip containing
    ModSetup.exe                     this converter (compiled by this script)
    payload\tools\                   PatchLanCombat.exe + Mono.Cecil.dll
    payload\LanRoomUI.dll            built room UI
    payload\server\                  server.exe + JSON data
    payload\Start Game.exe|Updater.exe

The zip bundles derived data (patched DLL, extracted JSON) that only makes
sense together with the player's own legally obtained game files - keep it
out of the public source repository.
"""
import argparse
import os
import subprocess
import sys
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]      # public candidate tree root
CSC = r"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--version", default="1.0.0")
    parser.add_argument("--client", default=Path(r"C:\GameInstall\mini-battlegrounds-lan\map102-test\client"),
                        help="prepared client whose Managed dlls are the build output")
    parser.add_argument("--server-dist", default=ROOT.parent / "map102-test" / "dist" / "server")
    parser.add_argument("--launcher-dir", default=Path(r"C:\GameInstall\mini-battlegrounds-lan\map102-verify-out\rel-verify")
                        if Path(r"C:\GameInstall\mini-battlegrounds-lan\map102-verify-out\rel-verify").exists()
                        else ROOT.parent / "map102-verify-out" / "rel-verify")
    parser.add_argument("--output", default=ROOT / "dist")
    args = parser.parse_args()

    work = Path(__file__).resolve().parent / "build"
    if work.exists():
        import shutil
        shutil.rmtree(work)
    payload = work / "payload"
    payload_tools = payload / "tools"
    payload_tools.mkdir(parents=True)

    # 1. compile the converter
    print("compile ModSetup.exe")
    subprocess.run([CSC, "/nologo", "/out:" + str(work / "ModSetup.exe"),
                    str(ROOT / "steam-install" / "ModSetup.cs")], check=True)

    # 2. collect the patch tool
    for name in ("PatchLanCombat.exe", "Mono.Cecil.dll"):
        src = ROOT.parent / "map102-test" / "tools" / name
        if not src.exists():
            sys.exit("missing " + str(src))
        (payload_tools / name).write_bytes(src.read_bytes())

    # 3. room UI + client assembly from the prepared client
    managed = Path(args.client) / "CrazyShooter_Data" / "Managed"
    for name in ("Assembly-CSharp.dll", "LanRoomUI.dll"):
        src = managed / name
        if not src.exists():
            sys.exit("missing " + str(src))
    (payload / "LanRoomUI.dll").write_bytes((managed / "LanRoomUI.dll").read_bytes())
    # LanRoomUI references the Unity runtime; stage the two assemblies beside
    # it so the patch tool can resolve the UI module's dependencies.
    for name in ("UnityEngine.dll", "UnityEngine.UI.dll"):
        (payload / name).write_bytes((managed / name).read_bytes())
    # Assembly-CSharp.dll is NOT shipped: ModSetup patches the player's own.

    # 4. server + data
    server_dst = payload / "server"
    server_dst.mkdir()
    for name in ("server.exe", "map102-vehicle-obstacles.json",
                 "map102-vehicle-props.json", "skin-catalog.json"):
        src = Path(args.server_dist) / name
        if not src.exists():
            sys.exit("missing " + str(src))
        (server_dst / name).write_bytes(src.read_bytes())

    # 5. launcher
    for name in ("Start Game.exe", "Updater.exe"):
        src = Path(args.launcher_dir) / name
        if not src.exists():
            sys.exit("missing " + str(src))
        (payload / name).write_bytes(src.read_bytes())

    # 6. zip
    out = Path(args.output)
    out.mkdir(parents=True, exist_ok=True)
    zip_path = out / ("mbg-lan-mod-setup-" + args.version + ".zip")
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as archive:
        archive.write(work / "ModSetup.exe", "ModSetup.exe")
        for base, _dirs, files in os.walk(payload):
            for name in files:
                full = Path(base) / name
                archive.write(full, "payload" / full.relative_to(payload))
    print("installer:", zip_path)
    print("size:", zip_path.stat().st_size, "bytes")


if __name__ == "__main__":
    main()
