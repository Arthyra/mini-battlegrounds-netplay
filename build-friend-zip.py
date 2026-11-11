"""Build a Windows player ZIP from the already prepared client."""
import argparse
import os
from pathlib import Path
import shutil
import subprocess
from tempfile import TemporaryDirectory

from prepare_lan_client import ASSEMBLY, SOURCE, copy_runtime

ROOT = Path(__file__).resolve().parent
GUIDE = """Mini Battlegrounds LAN · 102 复活模式

1. 把整个 ZIP 解压到一个目录。
2. 双击 Start Game.exe，地址栏会带出房主地址；没有就粘贴房主的 IP（局域网或公网），点击加入房间。
3. 进入大厅后，点界面上的「改名」按钮（或按 F2）设置昵称，服务端会永久保存。
4. 在游戏大厅点「开始游戏」表示准备，等房主开局。

这个包供加入房间使用。房主仍须在自己的电脑或 ECS 上运行服务端。
适用 Windows 10 / 11。可以给 Start Game.exe 创建桌面快捷方式。
"""


def _quad(version):
    parts = (str(version).split(".") + ["0", "0", "0", "0"])[:4]
    return ".".join(parts)


def _assembly_info(version, title):
    return """using System.Reflection;
[assembly: AssemblyTitle("%s")]
[assembly: AssemblyProduct("Mini Battlegrounds LAN")]
[assembly: AssemblyDescription("Mini Battlegrounds LAN")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyVersion("%s")]
[assembly: AssemblyFileVersion("%s")]
[assembly: AssemblyInformationalVersion("%s")]
""" % (title, _quad(version), _quad(version), version)


def build(output, client, host=None, version=None):
    output = output.resolve()
    if output.suffix.lower() != ".zip" or output.exists():
        raise ValueError("Choose a new .zip output path.")
    managed = client / ASSEMBLY.parent
    if b"GetLoginHost" not in (managed / "LanRoomUI.dll").read_bytes() or b"GetLoginHost" not in (client / ASSEMBLY).read_bytes():
        raise ValueError("Install the matching launcher-compatible Assembly-CSharp.dll and LanRoomUI.dll first.")
    csc = Path(os.environ["WINDIR"]) / r"Microsoft.NET\Framework64\v4.0.30319\csc.exe"
    output.parent.mkdir(parents=True, exist_ok=True)
    with TemporaryDirectory(prefix="mbg-player-build-") as temporary:
        stage = Path(temporary)
        launcher_info = stage / "LauncherAssemblyInfo.cs"
        updater_info = stage / "UpdaterAssemblyInfo.cs"
        launcher_info.write_text(_assembly_info(version or "0.0.0", "Mini Battlegrounds LAN"), encoding="utf-8")
        updater_info.write_text(_assembly_info(version or "0.0.0", "Mini Battlegrounds Updater"), encoding="utf-8")
        subprocess.run([str(csc), "/nologo", "/target:winexe", "/reference:System.Windows.Forms.dll",
                        "/reference:System.Drawing.dll",
                        "/reference:System.IO.Compression.FileSystem.dll", f"/out:{stage / 'Start Game.exe'}",
                        str(ROOT / "lan-launcher.cs"), str(launcher_info)], check=True)
        subprocess.run([str(csc), "/nologo", "/target:winexe", "/reference:System.Windows.Forms.dll",
                        "/reference:System.Drawing.dll", f"/out:{stage / 'Updater.exe'}",
                        str(ROOT / "updater.cs"), str(updater_info)], check=True)
        subprocess.run([str(csc), "/nologo", "/target:winexe", f"/out:{stage / 'Fullscreen.exe'}",
                        str(ROOT / "fullscreen-helper.cs")], check=True)
        shutil.copy2(ROOT / "fullscreen.ps1", stage / "fullscreen.ps1")
        launcher_info.unlink(missing_ok=True)
        updater_info.unlink(missing_ok=True)
        copy_runtime(stage / "client", client)
        # The prepared client carries read-only files from its original
        # extraction; zipped directory entries with the read-only attribute
        # make .NET ExtractToDirectory fail with access denied on update.
        for dirpath, dirnames, filenames in os.walk(stage):
            for name in dirnames + filenames:
                os.chmod(os.path.join(dirpath, name), 0o755)
        server_source = ROOT / "dist" / "server"
        if (server_source / "server.exe").is_file():
            server_stage = stage / "server"
            server_stage.mkdir()
            shutil.copy2(server_source / "server.exe", server_stage / "server.exe")
            for name in ("map102-vehicle-obstacles.json", "map102-vehicle-props.json", "skin-catalog.json"):
                shutil.copy2(server_source / name, server_stage / name)
        if host:
            (stage / "server.txt").write_text(str(host) + "\n", encoding="ascii")
        archive = Path(shutil.make_archive(str(stage.parent / stage.name) + "-client", "zip", stage))
        try:
            shutil.move(archive, output)
        finally:
            archive.unlink(missing_ok=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path)
    parser.add_argument("--client", default=SOURCE, type=Path, help="prepared client directory")
    parser.add_argument("--host", help="host IP pre-filled in the launcher's server.txt")
    parser.add_argument("--version", help="version written into version.txt")
    args = parser.parse_args()
    build(args.output, args.client, args.host, args.version)


