# Mini Battlegrounds 联机接手说明

记录日期：2026-11-10。目标是继续开发最多 8 人的熟人联机和 102 复活模式。Bot 已搁置。先把真实跨设备的 LAN 局跑稳，再按需做 ECS 公网直连。

## 仓库与本机位置

- 当前项目根目录是 `C:/GameInstall/mini battleground/map102-test`。从这个目录运行服务器和构建命令。
- GitHub 私有仓库是 [Ash-Rise/mini-battlegrounds-lan](https://github.com/Ash-Rise/mini-battlegrounds-lan)。当前远端 `main` 包含房间 UI、LAN 下载、多局重置、Windows 玩家启动器、公网 ECS 和 LanRoomUI 改名，对应 [私有 Release lan-player-v0.5](https://github.com/Ash-Rise/mini-battlegrounds-lan/releases/tag/lan-player-v0.5)。接手先看 `git status -sb` 和 `git diff`。
- Windows 客户端在项目的 `client/CrazyShooter.exe`，基于 Steam Build 2655148（32 位 Unity 5.6.3、Mono），已准备好 102 资源和本地登录。APK 来源是 `C:/Workspace/Mini+Battlegrounds_1.0.5_APKPure.apk`（Unity 2017.4），两套资源不能整包混用。
- 前一环境的分析和构建材料在 `C:/Users/Example/Documents/Codex/2026-11-09/mini-battleground-steam-4399-qq-22/work`。以下把它简称为 `work`。新 harness 未必附带这个目录，需要能访问同一台 Windows 的文件。仓库单独克隆只有源码。玩家可以从私有 Release 下载已经准备好的客户端。
- `client/`、APK、原版素材、`lan-profiles.json`、运行日志和导出 JSON 都是本地文件，仓库采用白名单式 `.gitignore`。已准备好的客户端作为约 80 MB 的私有 Release 附件分发，包含 102 地图和原版游戏资源。Git 历史仍只跟踪源码与文档。若以后要公开源码，先核对仓库的 Release 附件和提交历史。

## 启动和诊断

### 玩家

从 [私有 Release 下载 Windows 玩家包](https://github.com/Ash-Rise/mini-battlegrounds-lan/releases/download/lan-player-v0.5/Mini-Battlegrounds-LAN-Windows.zip)，完整解压后双击 `Start Game.exe`。下载者的 GitHub 账号需要仓库访问权限。包已带 102 客户端，玩家无需安装 Steam 或 Python。默认地址为公网 ECS `198.51.100.10`，局域网时在启动器里改成房主 IPv4。同一局域网只发现一个房间时会自动连接，搜不到时粘贴房主的 IPv4 或邀请链接，再点「加入房间」。也可以给启动器创建桌面快捷方式。

### 房主

在项目目录运行 `python .\server.py --share-client`。它会选择房主的私有 IPv4、生成面向该地址的客户端 ZIP（本机约 80 MB）、启动房主客户端并打印下载链接。已有 Release 玩家包的朋友可直接运行 `Start Game.exe`。通过房主的临时邀请链接下载的 ZIP 已写入房主地址，解压后运行其中的 `client\CrazyShooter.exe`。多网卡时使用 `--host <朋友能访问的本机 IPv4>`。只启用本机测试时运行 `python .\server.py --players 2`，然后打开两个客户端。可以传 `-lan-profile=slot-1` 和 `-lan-profile=slot-2` 以固定档案。默认地址是 `127.0.0.1`，默认房间上限是 8；`--players` 是容量，房主可按实际准备人数开局。

- 游戏端口为 TCP `43120`（登录）、`43123`（大厅）、`43134`（战斗），UDP `43135`（位置同步）；房间搜索为 UDP `43122`；邀请下载为 TCP `43121`。`lan_share.py` 的链接随房主进程有效，自动选网卡只看默认路由，VPN 或多网卡时需要显式指定地址。
- 客人点「开始游戏」表示准备，再点一次取消。房主先准备，再点独立的「开始游戏」。开局只带走已准备的成员。房主目前由服务端所在电脑发起的连接取得，远程 ECS 房主需要显式配置身份。
- 退出对局用客户端原版的返回大厅流程。最后一名玩家断开战斗 TCP 后，服务端清空当局状态，准备人数归零，并给下一局分配新的 battle ID。大厅 TCP、下载链接、角色和皮肤档案保留。下局直接重新准备。加载阶段断开会取消当局，未到齐的加载最多等 90 秒。原版客户端的断线处理可能返回登录页，掉线重连或自动回房还没做。
- 可查项目内 `current-server.log`、`current-server.err`、`current-server.pid` 和 `client/current-client-*.log`。安装新服务端前只重启这个项目的进程，别停掉其他 Python 或 Unity。阶段测试可以用独立的临时客户端副本、档案和 `127.0.0.8`，以免写进正式的 `lan-profiles.json`。
- `export-local-data.py` 用 UnityPy 1.25.2 和 NumPy 从准备好的客户端生成 `skin-catalog.json`、`map102-vehicle-obstacles.json` 和 `map102-vehicle-props.json`。服务端运行本身只用 Python 3.11+ 标准库。换地图包或皮肤数据后重新导出。

## 代码与构建

| 文件 | 职责 |
| --- | --- |
| `server.py` | 单进程的登录、大厅、战斗模拟和 UDP 转发。读写档案，开局和收尾。 |
| `patch-lan-combat.cs` | Mono.Cecil 补丁，修改原 Windows 客户端，接入复活、命中、房间消息、武器和 UI。 |
| `lan-room-ui.cs` | 编译为 `LanRoomUI.dll`，使用原大厅按钮、准备状态和匹配动画。 |
| `prepare_lan_client.py`、`prepare-lan-client.py` | 模块及兼容的命令行入口。从已准备的回环客户端生成目标 IPv4 的副本。 |
| `lan_share.py` | 房主模式只共享一份已配置客户端 ZIP，并打开房主游戏。 |
| `lan-launcher.cs`、`build-friend-zip.py` | 构建可分发的玩家 ZIP。启动器通过 UDP 43122 搜索房间，或把手工输入的地址以 `-lan-host` 传给游戏。 |
| `patch-local-login.py` | 针对特定原版程序集的本地登录补丁。已有准备好的客户端不要重复应用。 |

补丁工具用 Mono.Cecil 0.11.6。玩家启动器使用 Windows 自带 .NET Framework 和 WinForms。参考 `work/Mono.Cecil.dll`。战斗补丁的输入是 `work/map102-local-combat-base/CrazyShooter_Data/Managed/BeforeCombat.dll`。它已有本地登录和地图基础改动。**不要把当前已经打过战斗补丁的 `Assembly-CSharp.dll` 当输入。** Unity DLL 和 Mono `mscorlib.dll` 在同一 Managed 目录。

以下为 PowerShell 构建示例，路径用实际所在位置。Windows Framework csc 对路径的解析有坑，输入路径优先用反斜杠或已解析的绝对路径。把 Mono.Cecil 放在生成的补丁工具旁边。

```powershell
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
# $managedBase 是含 BeforeCombat.dll、Unity DLL 和 mscorlib.dll 的目录
# $cecil 是 Mono.Cecil.dll 的路径
& $csc /nologo /noconfig /target:library /nostdlib+ /out:LanRoomUI.dll `
  "/reference:$managedBase\mscorlib.dll" "/reference:$managedBase\UnityEngine.dll" `
  "/reference:$managedBase\UnityEngine.UI.dll" .\lan-room-ui.cs
& $csc /nologo /out:PatchLanCombat.exe "/reference:$cecil" .\patch-lan-combat.cs
.\PatchLanCombat.exe "$managedBase\BeforeCombat.dll" .\Assembly-CSharp.dll .\LanRoomUI.dll
```

先在副本测试，再备份并将两份 DLL 放到 `client/CrazyShooter_Data/Managed`。如果只改 `server.py`，直接重启服务端即可。仓库的根目录白名单会忽略这些二进制输出。

## 网络协议和对局生命周期

- 帧头 `<HH`，依次是总长度和消息号。TCP 登录 `20`、大厅登录 `23`、准备切换 `27`、分配对局 `32`、战斗登录 `34`、快照 `37`、加载完成 `38`、开局 `39/40`。自定义大厅消息 `210` 是准备数/成员数/容量及本人的准备、房主、已开局标记，`211` 是房主请求开局。
- 大厅 TCP 和战斗 TCP 是独立连接。Windows 返回大厅会主动关闭战斗 TCP/UDP 并保留大厅。服务端在战斗 handler 的 `finally` 释放玩家战斗连接；最后一个退出时 `finish_round` 复位车辆、门、可破坏物、掉落、弹道和准备状态。下局重新初始化玩家出生位置、血量和装备。战斗 TCP 与 UDP 均检查当局 battle ID，避免旧包写进新局。
- 位置由客户端上行 UDP `82`，服务端以 `52` 转给其他成员。普通枪弹由射手客户端报命中（`201`）。复活 `202`，近战动作 `203`，整套装备更新 `204`，移除掉落物 `205`，开镜状态与姿态 `206/207`，滚轮切换 `208`。
- 客户端底层仍走老版 Windows 的 `BattleSurvival` 路径。大厅的复活模式 ID 为 4；下发对局数据有意保留老客户端可读格式。APK 的 `BattleDeathmatch` 和原版队伍结算可供移植，原服目标分和复活参数没有从客户端中确认。

## 当前玩法和原版依据

- 102 是独立街区复活图。规则按本项目需求做了倒地可爬、3 秒原地站起、1 秒保护、随机换主武器。手枪和近战分别在 2/3 键；滚轮切 1–3，4 是手雷/燃烧弹，5 是急救包。取消烟雾弹和可拾取头盔/护甲。枪械备弹无限，弹匣和各枪换弹时间保留。支持 /98k、/dg、/rpg 等本地聊天指令和原版击杀广播。具体操作见 README。
- 18 把枪的原版配置主要来自 APK `GunData`。急救包 4 秒/400 血、手雷和燃烧弹伤害与半径有原表依据。投掷轨迹、部分车辆物理和 8 m/s 撞人致倒阈值为本地实现。火箭筒能破坏带标记的门和自行车类道具。
- 狙击移植了 APK 的 `Hold_Rifle_Snipe`、`Fire_Rifle_Snipe`、白色拖尾、视野参数及锥形散布/后坐恢复。98K/AWM 每次按下一发，SG550 可按住连射。此 APK 的原始散布下限为腰射 2°、开镜 4°，不要为了符合直觉擅改成另一套参数。切刀再切回狙击重置间隔是目前的熟人局规则。原服服务端实现未获得。
- 102 的人行道彩纹规避通过只关闭地形对象 PathID 2562 的 Android 烘焙光照索引实现。地面少一层烘焙阴影。原始画面或素材迁移可参考 `work/map102-render-5`、`work/map102-graft-android.py`、`work/map102-graft-scope.py` 和 `work/map102-graft-round.py`。这些是历史实验，部分脚本预期更早的输入包，运行前先检查路径及版本。
- `work/map102-dump-safe.exe` 可查看 Windows 托管程序集 IL；`work/map102-mobile-native/tool/dump.cs`、`script.json` 和 `disasm.py` 用于追 APK 的 ARM IL2CPP 实现。移植参数或行为前优先对照这些材料。103 生化模式的 `NavigationInfoKeeper.mGraphText` 有原版导航图，102 没找到。APK Bot 客户端会走路线和转向，`UpdateFire` 为空，攻击由网络消息驱动。Bot 暂停开发。

## 验证边界和下一步

- 旧构建同机测过四个真实 Windows 客户端和位置同步。八人用一个 Windows 客户端和七个协议客户端验证过进图、位置转发和狙击伤害。房间流程和临时邀请 ZIP 在本机测过。也实际解压了 Release 同一份 ZIP，双击启动器后自动发现房间并进入大厅。多台实体电脑、Wi-Fi 隔离、载具抢座、门动画、隔门爆炸、草丛内外互看仍适合在两台电脑实测。
- 多局重置已用两个实际 Windows 客户端同机验证。测试调用了原版大厅按钮和原版退出对局回调，连续开局并返回大厅，后续局也重新注册了 UDP。另用 TCP 协议测试确认先退出的人不会提前结束本局，最后退出后车辆、门、掉落和下一局初始血量复位。测试材料在 `work/map102-replay-stage/replay.log` 和 `work/map102-replay-test.py`。下一步做跨电脑 TCP/UDP 测试。遇到启动卡死先对照 `32 → 34 → 37 → 38 → 39/40`，遇到能进图但角色不动先检查 UDP 注册和 `82 → 52` 转发。战斗掉线后其他人仍在局内时没有专门的角色消失或重连协议。
- ECS 公网最省工的路线是让 Python 服务端直接跑在 ECS，朋友免装 VPN。启动器已能接受手工输入的公网 IPv4。服务端仍限制私有地址，且绑定地址和下发地址共用 `--host`。ECS 直连需要拆开这两个地址，并为远程房主提供房主 IP 或简单凭据。旧的 `prepare-lan-client.py` 命令仍只接受私有 IPv4。先沿用现有 TCP/UDP，熟人 IP 固定时通过 ECS 安全组放行，IP 常变再做房间码。当前 UDP 票据仍等于玩家 ID，普通命中相信射手客户端，适用于受信任的玩家。别把 Windows 自动下载/启动工具直接当成 Linux ECS 的启动方式。
- 若只想让所有人退出后能再开局，常驻一个轻量大厅和下载服务、按局清理战斗是当前实现。让进程无人自动退出也可行，但再由客户端启动需要独立的启动器或常驻控制端，还要恢复监听和下载链接。对目前单房间用途没有必要。

## 2026-11-10 公网 ECS 现状

- ECS `198.51.100.10`（`cn-hangzhou`，实例 `<ECS_INSTANCE_ID>`），服务端目录 `/srv/example/mbg-2`。
- 启动方式：`python3 -u server.py --players 8 --host 198.51.100.10 --bind 0.0.0.0 --owner-profile slot-1`。`--host` 只用于下发给客户端的公网地址，`--bind 0.0.0.0` 负责监听；两者已拆开。
- ECS 上提供 `ecs-start.sh` / `ecs-stop.sh`，PID 写 `server.pid`，日志 `server.log`。安全组 `<ECS_INSTANCE_ID>` 已放行 TCP `43120/43123/43134` 和 UDP `43135` 到 `0.0.0.0/0`。
- 房主可用 `--owner-profile slot-1` 固定；设置了固定房主时，朋友先登录也不能开局，房主重连后重新取得房主。未设置时仍由第一个登录大厅的玩家取得。`--host` 也允许公网地址，`prepare-lan-client.py` 不再限制私有 IPv4。
- 昵称在联机大厅的 LanRoomUI「改名」按钮或 F2 修改，客户端发 213，服务端保存后回发 117 并刷新大厅顶栏；启动器不再负责昵称，只自动分配唯一 `pc-<机器名>` 档案。
- 公网客户端 ZIP 是本地文件，放在 `map102-test/dist/`，不要提交 Git；朋友通过 Release 或网盘分发。公网服务端没有鉴权，UDP 票据仍等于玩家 ID，只适合熟人。
- P0 已补：登录 `lan-v3` 版本握手；固定房主档案；战斗断线广播 214 让其他客户端删除角色；每次保存档案同步写 `lan-profiles.json.bak`。已用两个协议客户端从本机经公网 ECS 验证登录、准备、开局、TCP 43134、UDP 43135 和 214。
- 房间成员列表：210 房间状态追加成员 id/准备/房主/名字，LanRoomUI 右上角显示；协议版本升到 `lan-v4`。
- 战斗重连：服务端保留断线玩家 30 秒，期间不广播 214；客户端 `BattleTcpServerProxy.OnDisconnect` 每秒尝试重连并补发 34，重连成功继续原局；超时后由服务端清理并广播 214。已用协议客户端验证断线后 34/37 重连；真实客户端自动重连仍待双机验证。
- 贴图：只把建筑使用的 lightmap（index 1，texture 20）恢复原彩色；路面/道具 index 0 和地形 index 2 继续用暖色单色，避免人行道彩纹。
- 尚未实测：两台不同网络的真实 Unity 客户端完整对局；真实客户端自动重连；ECS 重启后的自动拉起。

## 生成朋友用的 ZIP

运行 `python .\build-release.py --version 0.4` 会自动编译启动器、打包 `dist/Mini-Battlegrounds-LAN-Windows.zip`、写 `dist/release-v0.4.json`（大小、SHA256、提交、时间）；加 `--publish` 会创建并设为 Latest 的 GitHub Release。打包源的 `Assembly-CSharp.dll` 和 `LanRoomUI.dll` 都须支持 `-lan-host`。

本机项目的 `client/` 目前仍是启动器改动前的房主测试构建，可继续用于固定地址的邀请 ZIP。要重新生成 Release 玩家包，使用已验证的 `work/map102-player-stage/client`，或按上面的步骤重建两份 DLL 后再打包。






