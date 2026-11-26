# Mini Battlegrounds LAN

Mini Battlegrounds LAN 是非官方的 Windows 多人联机恢复项目，围绕地图 102 提供独立的房间服务。可在局域网发现房间或直连，也可加入自行部署的公网服务器。支持 1–8 人、大厅准备、房主开局、回合设置、战斗、计分、复活和断线重连。

**当前不兼容 Steam 原版（App 658470）**。该发行版缺少地图 102。每位玩家需要合法持有包含地图 102 的兼容 Windows 客户端。项目不提供原游戏、地图、Unity 程序集、素材或提取数据。[GitHub Releases](https://github.com/Ash-Rise/mini-battlegrounds-lan/releases) 目前提供版本记录和源码快照，没有完整游戏安装包或预构建补丁安装器。

## 开始联机

1. 准备合法取得的兼容客户端，保留未修改的原版目录。
2. 按[构建指南](BUILDING.md)在 Windows 上生成本地 Mod 安装包。用 `ModSetup.exe` 指定原版及新安装目录。安装器在独立目录复制并打补丁，地图与皮肤数据从玩家自己的文件提取。
3. 从新目录运行 `Start Game.exe`。LAN 房主选择「创建局域网房间」，其他玩家选择「搜索局域网房间」或手动输入房主 IPv4。加入自行部署的公网房间时，手动输入服务器 IPv4。
4. 在大厅选角色和皮肤并准备，由房主开局。开始对局后，新玩家需要等本局结束再加入。

房主也可按[服务端构建和部署步骤](BUILDING.md#从源码运行服务端)运行 `server.py`。LAN 房间可离线使用，源码构建的启动器默认跳过更新检查。

## 构建与验收

Windows 构建需要 Python 3.11+、.NET Framework C# 编译器、Mono.Cecil 0.11.6 和合法兼容客户端。数据提取需要 UnityPy 和 NumPy。打包需要 PyInstaller。完整命令及输入目录见[构建指南](BUILDING.md)。

`python tests/run-all.py` 运行自动化回归。真实地图 102 的干净 Windows 安装、Unity GUI、长时间运行和跨机器断线恢复仍有未完成的验收项。见 [LAN 验收记录](BETA-ACCEPTANCE-v0.12.0.md)。

## 许可与声明

[MIT License](LICENSE) 适用于项目贡献者有权授权的原创代码和文档，包括原创的服务端、补丁工具、启动器、更新器、安装器和测试。原游戏、地图、程序集、素材及从原游戏生成的数据或含原游戏代码的补丁产物不在此授权范围内。

第三方组件按各自许可证分发。Mono.Cecil 见[随附声明](steam-install/MONO_CECIL_LICENSE.txt)。本项目与原游戏开发商和发行商没有官方关联。