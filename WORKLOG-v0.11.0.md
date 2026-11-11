# Worklog — v0.11.0 ECS Stable

Goal：完成 ECS 模式开发、稳定性验证、HTTPS 自动更新与可靠发布。
持久化状态文件，每阶段更新。跟踪 Issue：#7。

## 基线报告（2026-11-12 03:10 审计）

### 已验证事实
- 本地 main == origin/main == `d3b94a5`，工作区干净
- Release `lan-player-v0.10.8` = tag @ `7fe07d1`，**缺 7 个后续修复**（1606bd6..d3b94a5）
- ECS（midnight / 198.51.100.10）：systemd `mini-battleground.service`
  运行 `server.py --players 8 --host 198.51.100.10 --admin-port 8080`，部署于 11-12 00:35
- **ECS server.py 是旧版**：md5 `3736b58a` ≠ 本地 `eaf1e044`；缺 vehicle.y 修复、
  scoped/vehicle 断线重置、MBG_TEST 钩子（"源码已修复但 ECS 运行旧代码"成立）
- 更新入口现状：`http://198.51.100.10:8080/update/`（HTTP；与 admin API 同端口 8080；
  admin API 有 token 401 保护；UFW inactive，8080 公网可达）
- **HTTPS 基础设施已存在**：nginx 1.24 监听 80/443；Let's Encrypt **IP 证书**
  （SAN=198.51.100.10，有效期 11-08 → **11-14**）；续期 `qq-agent-cert-renew.timer`
  每日两次（webroot + ExecStartPost cert-health-check），与 qq-agent 服务共用证书
- 启动器：`lan-launcher.cs:273` `UpdateUrl = "http://...:8080/update/latest.json"` 硬编码；
  WebClient/HttpWebRequest，未显式启用 TLS1.2（.NET 4.8 默认可用，需加保底）
- build-release.py：`--update-base-url` 已支持自定义 ZIP URL ✓；签名流程已有
- 日志：仅 3 个 IncompleteReadError（玩家强退），无异常栈
- v0.10.8 ZIP（89MB）与 latest.json 在 ECS `/srv/example/mbg-2/update/`

### 已知问题
1. 证书 11-14 到期（续期自动化已存在，信任但需观察 11-12 12:23 的下一次运行）
2. ECS 服务端代码落后 main（ vehicle.y 崩溃修复、断线状态重置未部署）
3. 更新与 admin API 共用 8080（需按 Goal 分离到 nginx 443 静态）
4. v0.10.8 客户端不含 4205aa9..d3b94a5 的全部玩法/重连修复

### 执行顺序
1. 阶段二：nginx 443 静态更新目录 → 新签名清单（HTTPS URL）→ 启动器 TLS12+URL → 兼容矩阵
2. 阶段三：ECS 服务端升级到 main + 重启方案 + 稳定性/长跑
3. 阶段四：玩法回归
4. 阶段五：0.10.9（修复版）或 0.11.0（ECS Stable）

## 阶段二进行中

（待填）
