"""Build a public Mod installer without copying original-game assets.

Inputs are explicit and local to the release builder; no personal paths or
extracted map/skin JSON are included in the distributed ZIP.

The offline ExtractLocalData.exe is built separately from export-local-data.py
using PyInstaller, UnityPy and NumPy. Its full dependency notices must be
supplied with --extractor-notices and reviewed before public distribution.
"""
import argparse
import hashlib
import os
import shutil
import subprocess
import tempfile
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CSC = Path(os.environ.get("WINDIR", r"C:\Windows")) / "Microsoft.NET/Framework64/v4.0.30319/csc.exe"


def file_at(path, label):
    path = Path(path).resolve()
    if not path.is_file() or path.stat().st_size == 0:
        raise SystemExit(f"Missing or empty {label}: {path}")
    return path


def copy(source, destination, label):
    source = file_at(source, label)
    destination.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(source, destination)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--version", default="1.0.0")
    parser.add_argument("--client", type=Path, required=True, help="prepared client directory with self-built LanRoomUI.dll")
    parser.add_argument("--server-dist", type=Path, required=True, help="directory containing freshly built server.exe")
    parser.add_argument("--launcher-dir", type=Path, required=True, help="directory of freshly compiled launcher EXEs")
    parser.add_argument("--patch-tools", type=Path, required=True, help="directory containing PatchLanCombat.exe and Mono.Cecil.dll")
    parser.add_argument("--extractor", type=Path, required=True, help="PyInstaller-built offline ExtractLocalData.exe")
    parser.add_argument("--extractor-notices", type=Path, required=True, help="complete license notices for the extractor's bundled dependencies")
    parser.add_argument("--supported-assembly", type=Path, required=True, help="verified PRISTINE Assembly-CSharp.dll (never distributed)")
    parser.add_argument("--output", type=Path, default=ROOT / "dist")
    args = parser.parse_args()

    pristine = file_at(args.supported_assembly, "verified original Assembly-CSharp.dll")
    if pristine.name != "Assembly-CSharp.dll":
        parser.error("--supported-assembly must be a pristine Assembly-CSharp.dll")
    if not CSC.is_file():
        parser.error(f".NET Framework csc.exe is missing: {CSC}")
    version_sha256 = hashlib.sha256(pristine.read_bytes()).hexdigest()
    print("Fingerprint for the verified original Assembly-CSharp.dll:", version_sha256)

    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    archive = output / f"mbg-lan-mod-setup-{args.version}.zip"
    if archive.exists():
        parser.error(f"Refusing to overwrite an existing installer: {archive}")

    with tempfile.TemporaryDirectory(prefix="mbg-mod-installer-") as workdir:
        work = Path(workdir)
        payload = work / "payload"
        work.mkdir(exist_ok=True)
        subprocess.run([
            str(CSC), "/nologo", "/out:" + str(work / "ModSetup.exe"),
            str(ROOT / "steam-install/ModSetup.cs")
        ], check=True)
        tools = payload / "tools"
        copy(args.patch_tools / "PatchLanCombat.exe", tools / "PatchLanCombat.exe", "patcher")
        copy(args.patch_tools / "Mono.Cecil.dll", tools / "Mono.Cecil.dll", "Mono.Cecil 0.11.6")
        managed = args.client / "CrazyShooter_Data/Managed"
        copy(managed / "LanRoomUI.dll", payload / "LanRoomUI.dll", "built LanRoomUI.dll")
        copy(args.server_dist / "server.exe", payload / "server/server.exe", "LAN server")
        copy(args.extractor, payload / "ExtractLocalData.exe", "local offline data extractor")
        for name in ("Start Game.exe", "Updater.exe"):
            copy(args.launcher_dir / name, payload / name, name)
        fullscreen = args.launcher_dir / "Fullscreen.exe"
        if fullscreen.is_file():
            copy(fullscreen, payload / "Fullscreen.exe", "fullscreen helper")

        (payload / "supported-assemblies.sha256").write_text(
            version_sha256 + "  Assembly-CSharp.dll\n", encoding="utf-8")
        copy(ROOT / "steam-install/MONO_CECIL_LICENSE.txt",
             payload / "MONO_CECIL_LICENSE.txt", "Mono.Cecil license")
        copy(args.extractor_notices, payload / "EXTRACTOR_NOTICES.txt",
             "third-party notices for the offline extractor")

        # Explicit allowlist: no UnityEngine DLLs, no proprietary game data,
        # no pre-extracted map or skin JSON, and no client runtime.
        with zipfile.ZipFile(archive, "x", zipfile.ZIP_DEFLATED) as z:
            z.write(ROOT / "LICENSE", "LICENSE")
            z.write(work / "ModSetup.exe", "ModSetup.exe")
            for path in sorted(payload.rglob("*")):
                if path.is_file():
                    z.write(path, (Path("payload") / path.relative_to(payload)).as_posix())

    with zipfile.ZipFile(archive) as z:
        names = set(z.namelist())
        forbidden = [
            n for n in names
            if n.lower().endswith((".json", ".unity3d", ".assets", ".apk")) or
            Path(n).name.lower() in ("unityengine.dll", "unityengine.ui.dll", "assembly-csharp.dll")
        ]
        if forbidden:
            archive.unlink(missing_ok=True)
            raise SystemExit("Release blocked: forbidden game-derived files: " + repr(forbidden))
    print(f"Created {archive} ({archive.stat().st_size} bytes)")

if __name__ == "__main__":
    main()
