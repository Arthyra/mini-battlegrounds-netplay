# Agent handoff（当前状态入口）

此文件只保留轻量入口；原先的 v0.11.0 长篇部署与本机路径说明已过时，且不适合成为将来 Public 仓库的资料。

请按以下顺序接手：

1. `AGENTS.md` — 长期工程规则及安全边界。
2. `README.md` — 功能、运行方式和版本说明。
3. [Issue #7](https://github.com/Ash-Rise/mini-battlegrounds-lan/issues/7) — 当前尚未完成的双机/网络故障验收。
4. `WORKLOG-v0.11.1.md`、`ECS-ACCEPTANCE-v0.11.1.md`、`ECS-RELEASE-REPORT-v0.11.1.md` — 测试、豁免和正式发布证据。
5. `WORKLOG-v0.11.0.md`、`ECS-STABLE-REPORT-v0.11.0.md` — 历史档案，仅供查因。

截至 2026-11-12：**v0.11.1 已正式发布**（启动器内部版本 0.11.1.10），发布与部署记录见 `ECS-RELEASE-REPORT-v0.11.1.md`。真实 PC2 曾发生异常掉线/恢复失败，路面亮格也曾被观察到；连接清理相关补丁已写入，但用户明确豁免的双物理机回归不能记为 PASS。具体剩余问题以 Issue #7 为准。

**不要在本文件新增个人 ECS 地址、管理 Token、SSH 配置、签名私钥路径、原始机器日志或本地游戏资产路径。** 本地部署说明放在 Git 忽略的私有资料中。
