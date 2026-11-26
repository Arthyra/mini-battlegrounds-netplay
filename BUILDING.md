# 从源码构建和运行

## Windows 本地 Mod 安装包

构建需要 Python 3.11+、Windows 的 .NET Framework C# 编译器、[Mono.Cecil 0.11.6](https://www.nuget.org/packages/Mono.Cecil/0.11.6)，以及玩家合法取得且包含地图 102 的兼容客户端。使用干净的源码检出，在仓库根目录运行以下命令。替换输入路径，并保留原版目录和未修改的 `Assembly-CSharp.dll`。

```powershell
$game = 'C:\Games\CompatibleOriginal'
# client 必须是新的目录，下一步只修改该副本
Copy-Item -LiteralPath $game -Destination .\client -Recurse
$env:MBG_PATCH_INPUT = Join-Path $game 'CrazyShooter_Data\Managed\Assembly-CSharp.dll'
$env:MBG_MONO_CECIL = 'C:\Dependencies\Mono.Cecil.dll'
python -m pip install PyInstaller UnityPy numpy
python tools/rebuild-client-dlls.py
python -m PyInstaller --noconfirm --distpath dist/server --workpath dist/build/server build/pyinstaller/server.spec
python -m PyInstaller --noconfirm --onefile --name ExtractLocalData --distpath dist/extractor --workpath dist/build/extractor --specpath dist/build export-local-data.py

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
New-Item -ItemType Directory -Force dist/launcher | Out-Null
& $csc /nologo /target:winexe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.IO.Compression.FileSystem.dll '/out:dist\launcher\Start Game.exe' lan-launcher.cs
& $csc /nologo /target:winexe /reference:System.Windows.Forms.dll '/out:dist\launcher\Updater.exe' updater.cs
& $csc /nologo /target:winexe '/out:dist\launcher\Fullscreen.exe' fullscreen-helper.cs

python steam-install/build-installer.py --client client --server-dist dist/server --launcher-dir dist/launcher --patch-tools tools --extractor dist/extractor/ExtractLocalData.exe --extractor-notices '<依赖许可声明文件>' --supported-assembly $env:MBG_PATCH_INPUT
```

`--extractor-notices` 指向实际冻结程序的依赖声明文件，应包含随程序分发的 Python、UnityPy、NumPy、PyInstaller 及其他组件适用的许可和声明。打包器附上项目 LICENSE 和 Mono.Cecil 声明，并仅打包所需工具和自建组件。

将 `dist` 中新生成的 Mod 安装包解压到独立工具目录，保留 `payload`。在该目录运行以下命令。先运行 `--check-only` 验证指纹和目录，再执行安装。`--out` 必须是新的安装目录。

```powershell
.\ModSetup.exe --game 'C:\Games\CompatibleOriginal' --out 'C:\Games\MiniBattlegrounds-LAN' --check-only
.\ModSetup.exe --game 'C:\Games\CompatibleOriginal' --out 'C:\Games\MiniBattlegrounds-LAN'
```

成功后从新安装目录运行 `Start Game.exe`。自动更新默认关闭。自管镜像可通过 `MBG_UPDATE_MANIFEST_URLS` 配置完整 HTTPS 清单地址，多个镜像以分号分隔。清单和 ZIP 仍须通过签名和摘要验证。

## 从源码运行服务端

用 Python 3.11+ 安装 UnityPy 和 NumPy。从自己持有的兼容客户端读取地图与数据库，把三个运行时 JSON 输出到运行服务端的仓库根目录。

```powershell
python -m pip install UnityPy numpy
python export-local-data.py --client 'C:\Games\CompatibleOriginal' --output .
python server.py --players 8 --admin-port 0
```

LAN 多网卡时可用 `--host` 显式设置房主可达的 IPv4。建在独立公网主机上时，用 `--host` 指定对外 IPv4，用 `--bind 0.0.0.0` 监听，并按部署环境开放 TCP 43120、43123、43134 及 UDP 43135。LAN 自动发现使用 UDP 43122。示例关闭管理接口。

安装器和 CI 的合成测试无法替代真实游戏的端到端验收。传播源码或自建工具时按[项目许可与第三方声明](README.md#许可与声明)处理，含有原游戏内容的本机安装副本不应视作项目分发物。
