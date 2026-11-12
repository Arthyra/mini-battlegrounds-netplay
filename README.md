# Mini Battlegrounds LAN · 地图 102

基于 Mini Battlegrounds Windows Steam 版本的非官方联机实验项目，为地图 102 提供独立的房间、战斗和复活玩法。支持 **1～8 人**，使用项目自己的登录身份；**ECS 公网联机**和**局域网直连**共用同一套服务端与战斗协议。

> 这是持续开发中的 Mod，不是原游戏的官方在线服务。仓库以源码和补丁工具为主，不包含完整游戏客户端、原始 APK 或所有资源构建输入。

## 版本与进展

- **最新正式版：v0.11.2 — 修复重复更新提示**（[Release](https://github.com/Ash-Rise/mini-battlegrounds-lan/releases/tag/lan-player-v0.11.2)）。启动器内部版本为 `0.11.2.0`；保留完整版本号比较，避免更新成功后反复提示同一个版本。
- **连接恢复修复：v0.11.1**（[Release](https://github.com/Ash-Rise/mini-battlegrounds-lan/releases/tag/lan-player-v0.11.1)）。游戏与服务端逻辑继续沿用此版本。
- **上一里程碑：v0.11.0 — ECS Stable**（[Release](https://github.com/Ash-Rise/mini-battlegrounds-lan/releases/tag/lan-player-v0.11.0)）。

v0.11.0 完成了战斗断线恢复、重登、UDP 同步与服务端静默检测，并引入签名更新。v0.11.1 进一步修复 TCP 正常关闭后残留连接与登录卡住的问题，将战斗重连的连接等待移出 Unity 主线程，添加入场响应超时和重试限制，修复旧连接误清理新连接任务的竞态；新启动器只通过 HTTPS 获取更新，同时改进更新失败回滚。

ECS 另已应用服务端热修复：所有玩家的大厅/战斗连接均断开后立即回收空对局，服务继续监听；车辆爆炸、投掷物及燃烧范围伤害能伤及引发者，自杀不计击杀数。客户端仍使用 v0.11.2，无需下载新包；现有发行包中附带的 Windows 房主程序尚未包含这次服务端热修复。

已有 ECS 双客户端对局和断线恢复测试，但**不能据此声称全部真实网络故障场景已通过**。两台物理 Windows 的多轮故障回归、防火墙 TCP 黑洞、异常掉线及场景重载亮格问题仍在跟踪。

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

LAN 游戏的数据平面不应依赖 ECS；但启动器的公网自动更新检查可能在离线时失败，需确保它不会阻塞 LAN 游戏。LAN 的完全离线双机、不同网卡和防火墙场景还需专项验收，**目前不宣称 LAN 已全面稳定**。

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
| `tools/acceptance/` | 隔离 ECS 双机验收构建、故障注入与证据采集 |
| `docs/releases/`、`docs/history/` | 版本说明、验收报告与归档记录 |
| `AGENTS.md` | Agent 开发约定 |

`build-release.py` 需要本地、未提交的合法构建输入，以及与启动器内置公钥配对的签名私钥。发行清单及 ZIP 的地址应按部署环境配置，不能将个人 ECS 地址或密钥硬编码进公共版本。源码提交成功不等于 Windows DLL 已重新构建、测试或发布。

## 测试与文档

当前未解决的问题以 [Issue #7](https://github.com/Ash-Rise/mini-battlegrounds-lan/issues/7) 为准。v0.11.1 的测试、发布证据见 [`docs/releases/v0.11.1/`](docs/releases/v0.11.1/)，详细会话日志归档于 [`docs/history/`](docs/history/)；v0.11.0 的历史报告见 [`docs/releases/v0.11.0/`](docs/releases/v0.11.0/)。归档文档不代表当前版本已经通过相同测试。旧 Worklog 已从工作树清除，其历史可通过 Git 查阅。

## 公开发布与资源说明

仓库目前用于私有开发，包含内部部署及实验历史；**不能直接将整个私有仓库或历史 Release 改为 Public**。将来发布公开 LAN 仓库时，应另行清理历史和个人环境信息，移除专有游戏资产，提供可以由用户自己持有的原版合法构建所生成补丁客户端的安装流程，并确认源码及资源的许可边界。
