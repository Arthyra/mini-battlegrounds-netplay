# Mini Battlegrounds LAN · 地图 102

基于 Mini Battlegrounds Windows Steam 版本的非官方联机实验项目，为地图 102 提供独立的房间、战斗和复活玩法。支持 **1～8 人**，使用项目自己的登录身份；**ECS 公网联机**和**局域网直连**共用同一套服务端与战斗协议。

> 这是持续开发中的 Mod，不是原游戏的官方在线服务。仓库以源码和补丁工具为主，不包含完整游戏客户端、原始 APK 或所有资源构建输入。

## 版本与进展

- **最新正式版：[v0.11.5 — 联机稳定性修复](https://github.com/Ash-Rise/mini-battlegrounds-lan/releases/tag/lan-player-v0.11.5)**。新增大厅 TCP 静默检测、客户端持续无响应连接检测、场景资源保留和多网卡 LAN 发现。Windows 玩家包已发布；**ECS 端是否已同步部署本次服务端修复，需要单独核实**。
- **开发中：v0.12.0 LAN Beta 候选（未发布）**。已完成单机协议与跨网卡发现测试，尚缺两台物理 Windows 的完整对局、断线恢复、离线网络与防火墙验收。详见 [Beta 验收记录](BETA-ACCEPTANCE-v0.12.0.md)。

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

当前玩家包及其部分数据依赖私有、预处理后的游戏资源；本 README **不表示允许公开镜像或再分发这些资源**。

### 使用源码运行服务端

需要 Python 3.11+、对应版本的已准备客户端及服务端依赖数据。LAN 房主可在项目目录执行：

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
| `BETA-ACCEPTANCE-v0.12.0.md` | 当前未发布 Beta 的测试记录与未完成项 |
| [GitHub Releases](https://github.com/Ash-Rise/mini-battlegrounds-lan/releases) | 发布记录、更新说明与玩家下载 |
| `AGENTS.md` | Agent 开发约定 |

`build-release.py` 需要本地、未提交的合法构建输入，以及与启动器内置公钥配对的签名私钥。发行清单及 ZIP 的地址应按部署环境配置，不能将个人 ECS 地址或密钥硬编码进公共版本。源码提交成功不等于 Windows DLL 已重新构建、测试或发布。

## 测试与版本记录

已解决的改动见 [GitHub Releases](https://github.com/Ash-Rise/mini-battlegrounds-lan/releases) 与 Git Commit；当前网络与场景遗留问题见 [Issue #7](https://github.com/Ash-Rise/mini-battlegrounds-lan/issues/7)、[Issue #8](https://github.com/Ash-Rise/mini-battlegrounds-lan/issues/8)。自动回归在 `tests/`，隔离验收工具在 `tools/`。阶段 Worklog、原始机器日志和详细历史报告不再提交；旧文件仍可通过私有仓库 Git 历史回溯。没有执行的物理双机与网络故障测试不能标记通过。

## 公开发布与资源说明

仓库目前用于私有开发，包含内部部署及实验历史；**不能直接将整个私有仓库或历史 Release 改为 Public**。将来发布公开 LAN 仓库时，应另行清理历史和个人环境信息，移除专有游戏资产，提供可以由用户自己持有的原版合法构建所生成补丁客户端的安装流程，并确认源码及资源的许可边界。
