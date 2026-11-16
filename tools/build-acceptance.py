"""Build an immutable isolated ECS test package, never a stable Release."""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import zipfile

ROOT = Path(__file__).resolve().parents[1]


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
    parser.add_argument("--client", type=Path,
                        default=Path(os.environ["MBG_CLIENT_DIR"]) if os.environ.get("MBG_CLIENT_DIR") else None,
                        help="Prepared base client (set MBG_CLIENT_DIR or pass --client)")
    parser.add_argument("--host", default=os.environ.get("MBG_TEST_HOST"),
                        help="Isolated test host (set MBG_TEST_HOST or pass --host)")
    parser.add_argument("--version", required=True, help="Acceptance package version label")
    args = parser.parse_args()
    for name in ("input", "cecil", "client"):
        path = getattr(args, name)
        if path is None or not path.exists():
            parser.error(f"--{name} (or its MBG_* env var) must point to an existing machine-local build input")
    if not args.host:
        parser.error("--host or MBG_TEST_HOST is required; never silently target production ECS")
    output = args.output.resolve()
    if output.exists():
        parser.error("Choose a new output directory to retain earlier evidence.")
    output.mkdir(parents=True)
    tools = output / "tools"
    tools.mkdir()
    shutil.copy2(args.cecil, tools / "Mono.Cecil.dll")
    runtime = args.client / "CrazyShooter_Data" / "Managed"
    csc = Path(os.environ["WINDIR"]) / "Microsoft.NET/Framework64/v4.0.30319/csc.exe"
    run(csc, "/nologo", "/noconfig", "/target:library", "/nostdlib", "/define:ECS_ACCEPTANCE",
        *("/reference:" + str(runtime / name) for name in
          ("mscorlib.dll", "System.dll", "System.Core.dll", "UnityEngine.dll", "UnityEngine.UI.dll")),
        "/out:" + str(output / "LanRoomUI.dll"), "lan-room-ui.cs", "lan-scoreboard.cs", "ecs-diagnostics.cs", "ecs-battle-connect.cs")
    run(csc, "/nologo", "/reference:" + str(args.cecil),
        "/out:" + str(tools / "PatchLanCombat.exe"), "patch-lan-combat.cs")
    # the patch tool resolves the UI module's dependencies from the UI dll's
    # own directory and the game module's from the exe dir / cwd; stage
    # the Unity runtime beside LanRoomUI.dll and in tools/ the same way
    # rebuild-client-dlls stages it inside Managed/
    for name in ("UnityEngine.dll", "UnityEngine.UI.dll", "UnityEngine.Networking.dll"):
        shutil.copy2(runtime / name, output / name)
    # the patch tool resolves the game module's references from the input
    # dll's directory and the current directory; run it from the output dir
    # so the staged Unity runtime covers both, regardless of where the
    # machine-local pristine input lives
    subprocess.run([str(tools / "PatchLanCombat.exe"), str(args.input),
                    str(output / "Assembly-CSharp.dll"), str(output / "LanRoomUI.dll")],
                   cwd=output, check=True)

    # build-friend-zip imports prepare_lan_client by module name; make the
    # repo root importable regardless of how this script was launched
    if str(ROOT) not in sys.path:
        sys.path.insert(0, str(ROOT))
    spec = importlib.util.spec_from_file_location("friend", ROOT / "build-friend-zip.py")
    friend = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(friend)
    candidate = output / "prepared-client"
    friend.copy_runtime(candidate, args.client)
    for name in ("Assembly-CSharp.dll", "LanRoomUI.dll"):
        shutil.copy2(output / name, candidate / "CrazyShooter_Data/Managed" / name)
    base = output / "base-player.zip"
    friend.build(base, candidate, args.host, args.version, kind="join")
    install = output / "package"
    with zipfile.ZipFile(base) as archive:
        archive.extractall(install)
    run(csc, "/nologo", "/reference:System.Web.Extensions.dll",
        "/reference:System.IO.Compression.FileSystem.dll", "/reference:System.IO.Compression.dll",
        "/out:" + str(install / "ECS-Acceptance.exe"),
        str(ROOT / "tools" / "ecs-acceptance-runner.cs"))
    run(csc, "/nologo", "/reference:Microsoft.CSharp.dll", "/reference:System.Web.Extensions.dll",
        "/out:" + str(install / "ECS-NetworkFault.exe"),
        str(ROOT / "tools" / "ecs-network-fault.cs"))
    for name in ("Start-EcsAcceptance.ps1", "Collect-EcsEvidence.ps1", "Invoke-EcsNetworkFault.ps1"):
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
    manifest = {"version": args.version + "-acceptance", "host": args.host, "commit": commit,
                "dirty": bool(subprocess.check_output(["git", "status", "--porcelain"], cwd=ROOT)),
                "purpose": "Isolated ECS acceptance; ECS_ACCEPTANCE port override enabled; not a stable release",
                "files": files}
    (install / "acceptance-build.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    archive = shutil.make_archive(str(output / "MiniBattlegrounds-ECS-Acceptance"), "zip", install)
    print(archive)


if __name__ == "__main__":
    main()
