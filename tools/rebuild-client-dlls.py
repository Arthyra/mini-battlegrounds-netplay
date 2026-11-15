"""Rebuild the patched client DLLs from current sources.

Inputs (machine-local, none in git):
  MBG_PATCH_INPUT   pristine unpatched Assembly-CSharp.dll
  MBG_MONO_CECIL    Mono.Cecil 0.11.6
Output:
  overwrites client/CrazyShooter_Data/Managed/{Assembly-CSharp.dll,LanRoomUI.dll}
  with freshly compiled builds, and runs the IL/JIT sanity check.

Steps (mirrors tools/build-acceptance.py, minus the ECS_ACCEPTANCE define):
  1. compile LanRoomUI.dll from lan-room-ui.cs + lan-scoreboard.cs +
     ecs-diagnostics.cs + ecs-battle-connect.cs against the client's own
     Unity/Mono runtime assemblies
  2. compile the patch tool and apply it to the pristine input
  3. ilcheck the result (only the known baseline noise is acceptable)
  4. install both DLLs into client/

Usage: python tools/rebuild-client-dlls.py
"""
import os
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def fail(message):
    print("FAIL " + message)
    sys.exit(1)


def main():
    input_dll = os.environ.get("MBG_PATCH_INPUT")
    cecil = os.environ.get("MBG_MONO_CECIL")
    if not input_dll or not Path(input_dll).is_file():
        fail("MBG_PATCH_INPUT must point at the pristine Assembly-CSharp.dll")
    if not cecil or not Path(cecil).is_file():
        fail("MBG_MONO_CECIL must point at Mono.Cecil 0.11.6")
    managed = ROOT / "client/CrazyShooter_Data/Managed"
    if not (managed / "UnityEngine.dll").is_file():
        fail("client/CrazyShooter_Data/Managed is missing the Unity runtime assemblies")
    csc = Path(os.environ["WINDIR"]) / "Microsoft.NET/Framework64/v4.0.30319/csc.exe"
    if not csc.is_file():
        fail("Framework64 csc.exe not found")

    out = managed
    ui_sources = ["lan-room-ui.cs", "lan-scoreboard.cs", "ecs-diagnostics.cs", "ecs-battle-connect.cs"]
    run = lambda *args: subprocess.run([str(a) for a in args], cwd=ROOT, check=True)

    # 1. LanRoomUI.dll against the client's own runtime assemblies (no
    #    ECS_ACCEPTANCE define: that variant is for isolated test packages).
    run(csc, "/nologo", "/noconfig", "/target:library", "/nostdlib",
        *("/reference:" + str(managed / name) for name in
          ("mscorlib.dll", "System.dll", "System.Core.dll",
           "UnityEngine.dll", "UnityEngine.UI.dll")),
        "/out:" + str(out / "LanRoomUI.dll"), *ui_sources)
    print("OK LanRoomUI.dll")

    # 2. patch tool (with Cecil beside it so the exe can resolve the assembly)
    shutil.copy2(cecil, ROOT / "tools/Mono.Cecil.dll")
    run(csc, "/nologo", "/reference:" + str(cecil),
        "/out:" + str(ROOT / "tools/PatchLanCombat.exe"), "patch-lan-combat.cs")
    print("OK PatchLanCombat.exe")

    # 3. apply
    run(ROOT / "tools/PatchLanCombat.exe", input_dll, out / "Assembly-CSharp.dll",
        out / "LanRoomUI.dll")
    print("OK Assembly-CSharp.dll")

    # 4. sanity
    ilcheck = ROOT / "tools/ilcheck.exe"
    if ilcheck.is_file():
        result = subprocess.run([str(ilcheck), str(out / "Assembly-CSharp.dll")],
                                capture_output=True, text=True, encoding="utf-8", errors="replace")
        stdout = result.stdout or ""
        noise = [line for line in stdout.splitlines()
                 if line.startswith("FAIL") and "RemoveUnit" not in line]
        if noise:
            fail("ilcheck reported unexpected failures:\n  " + "\n  ".join(noise))
        print("OK ilcheck (baseline noise only)")
    else:
        print("SKIP ilcheck (tools/ilcheck.exe not present)")


if __name__ == "__main__":
    main()
