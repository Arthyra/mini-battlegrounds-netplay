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
2. 双击 Start Game.exe，在「昵称」栏填自己的名字（会保存在 launcher.ini）。
3. 地址栏会带出房主地址；没有就粘贴房主的 IP（局域网或公网），点击加入房间。
4. 在游戏大厅点「开始游戏」表示准备，等房主开局。

这个包供加入房间使用。房主仍须在自己的电脑或 ECS 上运行服务端。
适用 Windows 10 / 11。可以给 Start Game.exe 创建桌面快捷方式。
"""


def build(output, client, host=None):
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
        subprocess.run([str(csc), "/nologo", "/target:winexe", "/reference:System.Windows.Forms.dll",
                        "/reference:System.Drawing.dll", f"/out:{stage / 'Start Game.exe'}",
                        str(ROOT / "lan-launcher.cs")], check=True)
        copy_runtime(stage / "client", client)
        if host:
            (stage / "server.txt").write_text(str(host) + "\n", encoding="ascii")
        (stage / "README.txt").write_text(GUIDE, encoding="utf-8-sig")
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
    args = parser.parse_args()
    build(args.output, args.client, args.host)
