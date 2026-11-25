# Mini Battlegrounds LAN · 地图 102

基于 Mini Battlegrounds Windows Steam 版本的非官方联机实验项目，为地图 102 提供独立的房间、战斗和复活玩法。支持 **1～8 人**，使用项目自己的登录身份；**ECS 公网联机**和**局域网直连**共用同一套服务端与战斗协议。

> 这是非官方 Mod，v1.0.0 已功能定版，后续仅维护现有功能。仓库以源码和补丁工具为主，不包含完整游戏客户端、原始 APK 或所有资源构建输入。

## 版本与进展

- **最新正式版：[v1.0.0 — 功能定版：房主自定义房间 + 稳定性收尾](https://github.com/Ash-Rise/mini-battlegrounds-lan/releases/tag/lan-player-v1.0.0)**。房主可调整人数上限（2–8，受服务器槽位限制），并选择 30、60、120 或 300 秒的回合时长。设置经服务端确认后从下一局生效。本版修复了双开第二客户端卡加载和人数上限调小后无法调回的问题。owner/join 双包已发布。**ECS 更新镜像部署需单独执行**（更新服务按规范仅在 8445 端口）。
- **历史版本：[v0.12.0](https://github.com/Ash-Rise/mini-battlegrounds-lan/releases/tag/lan-player-v0.12.0)**。单机协议与跨网卡发现测试及剩余验收项见 [LAN 验收记录](BETA-ACCEPTANCE-v0.12.0.md)。

历史修改与安装包统一以 [GitHub Releases](https://github.com/Ash-Rise/mini-battlegrounds-lan/releases) 为准。单机测试通过不等于两台物理电脑或长期稳定性验收；连续重进地图的内存变化仍需测量。

## 玩法概览

- **房间与回合**：大厅选择角色/皮肤、准备、房主开局；战斗结束后可重新准备。地图为 102。
- **战斗**：主武器、副武器、近战、换弹、狙击瞄准、伤害、倒地与复活，以及击杀榜。
- **道具与场景**：手雷、燃烧弹、急救包、掉落武器、可交互车辆、门与可破坏物件。
- **基础操作**：WASD 移动；1/2/3 切换主武器/副武器/近战；R 换弹；F 交互/上下车；4 投掷物；5 急救包；Enter 聊天；F2 改名。

## 运行方式

### 已有完整 Windows 玩家包

在获得授权的私有测试环境中，从对应 [Release](https://github.com/Ash-Rise/mini-battlegrounds-lan/releases) 下载适用的 Windows 包，**完整解压**后运行 `Start Game.exe`。

- **ECS / 公网**：通过启动器连接已授权的云端房间，或手动指定可访问的服务器 IPv4。
- 当前房间不支持在进行中的对局里新登录。若已有玩家开局，新玩家需等待这局结束；客户端会重试。此时停留登录页不代表服务器已停止，人数未到上限也不代表允许中途加入。
- **LAN / 局域网**：房主在 Windows 启动器选择“创建局域网房间”；其他玩家选择“搜索局域网房间”或手动填写房主 IPv4。
- 创建房间需要安装包包含 `server/server.exe`。部分仅供加入的客户端包可能不含服务端；这种包不能开房。

当前私有发布的 Windows 房主包含有 Unity 程序集和预处理的游戏资源。公开候选需要单独核验这些文件的许可，并排除未获公开分发授权的文件。

### 一键安装器（仅适用于已验证的地图 102 游戏版本）

**Steam App 658470 不支持**：该发行版缺少地图 102 场景，不能使用本 Mod 的现有玩法。玩家需要自己合法取得、包含地图 102 的兼容游戏版本。不会在此仓库或公开安装包提供原游戏文件。

安装器源码位于 steam-install/。其设计流程是：选择玩家持有的原版目录 → 验证原版程序集完整 SHA256 与场景 → 在新临时目录复制游戏 → 应用补丁 → **从玩家本机资源生成服务端 JSON** → 验证全部产物 → 移动成独立安装目录。出错时不覆盖原版或已有安装目录。未知版本默认拒绝；开发者可显式用 --allow-unknown-version 进行诊断实验。

**当前为发布候选，尚未完成真实地图 102 版本在干净 Windows 环境中的完整安装、Unity GUI 和物理双机验证。不要把 CI 编译通过当作玩家端验收通过。**

开发者构建**不含原游戏资源的安装 ZIP**时，需先准备自有程序构建产物和一个离线数据提取器：

1. python -m pip install PyInstaller UnityPy numpy（仅构建机器需要）。
2. python -m PyInstaller --onefile --name ExtractLocalData --distpath dist/extractor export-local-data.py，并在干净 Windows 环境验证生成的 ExtractLocalData.exe。
3. 从已验证的**原版、未打补丁**的 Assembly-CSharp.dll 生成可信版本指纹；将其文件路径作为 --supported-assembly 传给打包脚本。
4. 准备构建好的 LanRoomUI.dll、PatchLanCombat.exe、Mono.Cecil.dll、server.exe、启动器 EXE，以及提取器包含的 Python/UnityPy/NumPy/PyInstaller 等第三方依赖的完整许可说明。
5. 在仓库根目录执行（将占位路径替换为本机实际位置）：

    python steam-install/build-installer.py --client "已准备的客户端目录" --server-dist "server.exe所在目录" --launcher-dir "启动器EXE目录" --patch-tools "补丁工具目录" --extractor "dist/extractor/ExtractLocalData.exe" --extractor-notices "提取器第三方许可文件" --supported-assembly "已验证的未修改Assembly-CSharp.dll"

公开安装包采用文件白名单：只包括自写 Mod 工具、Mono.Cecil（附 MIT 许可）、启动器、服务端程序、离线提取器及其许可说明。不打包 Assembly-CSharp.dll、UnityEngine DLL、地图资源或预提取的三个 JSON。**离线提取器的冻结依赖和实际运行仍需要独立核验；在通过版权与端到端验证前，不发布公共二进制安装包。**

### 使用源码运行服务端

需要 Python 3.11+；服务端本体只使用标准库，但启动时读取三个**从原版游戏
提取的运行时数据文件**（`map102-vehicle-obstacles.json`、
`map102-vehicle-props.json`、`skin-catalog.json`），它们属于原游戏衍生数据，
不在本仓库中分发。使用你合法取得的原版游戏生成它们：

```powershell
# 1. 将原版游戏数据文件 cs_scene_102 与已准备客户端放入仓库的 client/Data/
# 2. 安装提取依赖并运行提取（在仓库根目录执行）
pip install UnityPy numpy
python export-local-data.py
# 3. 把生成的三个 JSON 复制到服务端运行目录（源码运行即仓库根；打包后为 server/）
```

随后 LAN 房主可在项目目录执行：

```powershell
python server.py --players 8 --share-client --admin-port 0
```

它会选择本机 LAN IPv4、启动房间服务，并在局域网内提供临时邀请链接。多网卡环境可显式使用 `--host <房主局域网IPv4>`；朋友必须能连到这个地址。若已有完整房主版玩家包，更推荐直接使用启动器开房，不需要朋友安装 Python。

对于单独部署的公网 Linux 服务器，可使用：

```bash
python3 server.py --players 8 --host <服务器公网IPv4> --bind 0.0.0.0
```

须正确设置防火墙/安全组：TCP 43120（登录）、43123（世界）、43134（战斗），UDP 43135（战斗状态）。LAN 自动发现还使用 UDP 43122；LAN 临时下载链接使用 TCP 43121。不要将管理 API、管理 Token 或测试故障注入服务直接暴露公网。

### 离线 LAN 与已知限制

LAN 游戏的数据通信不依赖 ECS。启动器的公网更新检查已缩短超时，但完全离线启动仍可能等待一段时间。LAN 的物理双机、断线、热点和防火墙场景仍需专项验收，**目前不宣称 LAN 已全面稳定**。

## 源码结构

| 文件 | 作用 |
| --- | --- |
| `server.py` | 登录、房间、战斗、TCP/UDP 同步与复活规则 |
| `lan-launcher.cs`、`updater.cs` | Windows 启动器与更新器 |
| `patch-lan-combat.cs` | Unity Mono/Cecil 战斗与网络补丁 |
| `lan-room-ui.cs`、`lan-scoreboard.cs` | 房间 UI 与计分板 |
| `lan_share.py`、`prepare_lan_client.py` | LAN 临时分享与客户端地址准备 |
| `build-friend-zip.py`、`build-release.py` | 从已准备的客户端生成分发包、签名更新清单 |
| `tests/` | 协议、连接生命周期与更新器回归测试 |
| `tools/` | 构建输入校验、隔离 ECS 验收与故障注入工具 |
| `BETA-ACCEPTANCE-v0.12.0.md` | LAN 历史验收记录与剩余验收项 |
| [GitHub Releases](https://github.com/Ash-Rise/mini-battlegrounds-lan/releases) | 发布记录、更新说明与玩家下载 |
| `AGENTS.md` | Agent 开发约定 |

`build-release.py` 需要本地、未提交的合法构建输入，以及与启动器内置公钥配对的签名私钥。发行清单及 ZIP 的地址应按部署环境配置，不能将个人 ECS 地址或密钥硬编码进公共版本。源码提交成功不等于 Windows DLL 已重新构建、测试或发布。

### 从源码重建客户端 DLL 与服务端

```powershell
# 服务端 exe（需要 PyInstaller）
python -m PyInstaller --distpath dist/server --workpath build/pyinstaller/build --noconfirm build/pyinstaller/server.spec

# 客户端补丁 DLL（需要用户自备的原版 Assembly-CSharp.dll 与 Mono.Cecil 0.11.6）
$env:MBG_PATCH_INPUT = "<原版未打补丁的 Assembly-CSharp.dll 路径>"
$env:MBG_MONO_CECIL = "<Mono.Cecil.dll 路径>"
python tools/rebuild-client-dlls.py
```

DLL 重建会读取仓库 `client/CrazyShooter_Data/Managed/` 下的 Unity/Mono 程序集
（来自你合法取得的原版游戏），产出打补丁的 `Assembly-CSharp.dll` 与
`LanRoomUI.dll`，并运行 IL 检查。`export-local-data.py` 的输入同样来自这个
目录。完整的权威构建配方见 `AGENTS.md`。

## 测试与版本记录

自动回归在 `tests/`，隔离验收工具在 `tools/`；已解决的改动见 [GitHub Releases](https://github.com/Ash-Rise/mini-battlegrounds-lan/releases)。没有执行的物理双机与网络故障测试不能标记通过。

## 常见问题排查

- **服务端启动报缺少 JSON 数据文件**：按"使用源码运行服务端"一节用 `export-local-data.py` 从原版客户端提取三个 JSON，并放到 server.exe 同目录。
- **搜索不到房间**：自动发现使用 UDP 43122 定向广播，只在同一网段可靠； 跨网段（不同路由/热点隔离）需要房主把地址告诉朋友手动加入。 多网卡机器上发现包会覆盖所有上行子网，但隧道/虚拟网卡（VPN、WSL、虚拟机） 可能抢走默认路由，必要时在启动器里手动输入房主 IPv4。
- **加入失败**：按顺序检查 TCP 43120/43123/43134 与 UDP 43135 是否可达 （Windows 防火墙会拦截未授权的 server.exe——启动器提供放行按钮）。
- **完全离线启动**：启动器的更新检查在未配置更新服务器时自动跳过， 不会阻塞进入大厅。
- **连续对局后内存偏高**：属于 Unity 资源缓存高水位，21 轮实测无持续 增长证据；长时间会话后偶发名牌空引用会自愈，不影响联机。

## 公开发布与资源说明

仓库目前用于私有开发，包含内部部署及实验历史；**不能直接将整个私有仓库或历史 Release 改为 Public**。将来发布公开 LAN 仓库时，应另行清理历史和个人环境信息，移除专有游戏资产，提供可以由用户自己持有的原版合法构建所生成补丁客户端的安装流程，并确认源码及资源的许可边界。
