"""Build an immutable isolated ECS test package, never a stable Release."""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import zipfile

ROOT = Path(__file__).resolve().parents[2]


def run(*args):
    subprocess.run([str(arg) for arg in args], cwd=ROOT, check=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--input", type=Path,
                        default=Path(os.environ["MBG_PATCH_INPUT"]) if os.environ.get("MBG_PATCH_INPUT") else None,
                        help="Pristine (unpatched) Assembly-CSharp.dll; set MBG_PATCH_INPUT or pass --input")
    parser.add_argument("--cecil", type=Path,
                        default=Path(os.environ["MBG_MONO_CECIL"]) if os.environ.get("MBG_MONO_CECIL") else None,
                        help="Mono.Cecil 0.11.6; set MBG_MONO_CECIL or pass --cecil")
    parser.add_argument("--version", required=True, help="Acceptance package version label")
    args = parser.parse_args()
    for name in ("input", "cecil"):
        if getattr(args, name) is None:
            parser.error(f"--{name} (or its MBG_* env var) is required: machine-local build asset")
    output = args.output.resolve()
    if output.exists():
        parser.error("Choose a new output directory to retain earlier evidence.")
    output.mkdir(parents=True)
    tools = output / "tools"
    tools.mkdir()
    shutil.copy2(args.cecil, tools / "Mono.Cecil.dll")
    runtime = ROOT / "client/CrazyShooter_Data/Managed"
    csc = Path(os.environ["WINDIR"]) / "Microsoft.NET/Framework64/v4.0.30319/csc.exe"
    run(csc, "/nologo", "/noconfig", "/target:library", "/nostdlib", "/define:ECS_ACCEPTANCE",
        *("/reference:" + str(runtime / name) for name in
          ("mscorlib.dll", "System.dll", "System.Core.dll", "UnityEngine.dll", "UnityEngine.UI.dll")),
        "/out:" + str(output / "LanRoomUI.dll"), "lan-room-ui.cs", "lan-scoreboard.cs", "ecs-diagnostics.cs", "ecs-battle-connect.cs")
    run(csc, "/nologo", "/reference:" + str(args.cecil),
        "/out:" + str(tools / "PatchLanCombat.exe"), "patch-lan-combat.cs")
    run(tools / "PatchLanCombat.exe", args.input, output / "Assembly-CSharp.dll", output / "LanRoomUI.dll")

    spec = importlib.util.spec_from_file_location("friend", ROOT / "build-friend-zip.py")
    friend = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(friend)
    candidate = output / "prepared-client"
    friend.copy_runtime(candidate, ROOT / "_local-archive/public-client-v0109")
    for name in ("Assembly-CSharp.dll", "LanRoomUI.dll"):
        shutil.copy2(output / name, candidate / "CrazyShooter_Data/Managed" / name)
    base = output / "base-player.zip"
    friend.build(base, candidate, "198.51.100.10", args.version)
    install = output / "package"
    with zipfile.ZipFile(base) as archive:
        archive.extractall(install)
    run(csc, "/nologo", "/reference:System.Web.Extensions.dll",
        "/reference:System.IO.Compression.FileSystem.dll", "/reference:System.IO.Compression.dll",
        "/out:" + str(install / "ECS-Acceptance.exe"), "tools/ecs-acceptance-runner.cs")
    run(csc, "/nologo", "/reference:Microsoft.CSharp.dll", "/reference:System.Web.Extensions.dll",
        "/out:" + str(install / "ECS-NetworkFault.exe"), "tools/ecs-network-fault.cs")
    for name in ("Start-EcsAcceptance.ps1", "Collect-EcsEvidence.ps1", "Invoke-EcsNetworkFault.ps1", "fullscreen.ps1"):
        shutil.copy2(ROOT / "tools" / name, install / name)
    (install / "ACCEPTANCE-README.txt").write_text(
        "Mini Battlegrounds isolated ECS acceptance package; NOT a stable Release.\n"
        "Verify package hashes: ECS-Acceptance.exe --verify\n"
        "Start the test client: ECS-Acceptance.exe or Start-EcsAcceptance.ps1\n"
        "Collect logs using bundled evidence helpers.\n"
        "Fault injection requires explicit authorization.\n"
        "Physical Windows and firewall-blackhole tests are NOT PASS without real observations.\n",
        encoding="utf-8",
    )
    files = []
    for name in ("Start Game.exe", "Updater.exe", "client/CrazyShooter_Data/Managed/Assembly-CSharp.dll", "client/CrazyShooter_Data/Managed/LanRoomUI.dll"):
        files.append({"path": name, "sha256": hashlib.sha256((install / name).read_bytes()).hexdigest()})
    commit = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
    manifest = {"version": args.version + "-acceptance", "commit": commit,
                "dirty": bool(subprocess.check_output(["git", "status", "--porcelain"], cwd=ROOT)),
                "purpose": "Isolated ECS acceptance; ECS_ACCEPTANCE port override enabled; not a stable release",
                "files": files}
    (install / "acceptance-build.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    archive = shutil.make_archive(str(output / "MiniBattlegrounds-ECS-Acceptance"), "zip", install)
    print(archive)


if __name__ == "__main__":
    main()
