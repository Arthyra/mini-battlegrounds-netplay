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

## 阶段二结果（HTTPS 自动更新）——基本完成

### 服务端（ECS）
- nginx 443 新增 `/mbg-update/` 静态目录（改动前备份 `/srv/example/mbg-4*`，`nginx -t` 通过后 reload，失败自动回滚）
- 双入口：443 HTTPS（新启动器）+ 8080 HTTP（旧启动器兼容入口保留）
- 管理 API 未被 443 暴露（/api/* 未代理）；目录列举 403
- 证书：Let's Encrypt IP 证书（SAN=198.51.100.10），`qq-agent-cert-renew.timer` 每日两次自动续期 ✓
- ECS server.py 已升级到 main（`d3b94a5`+，含 vehicle.y 修复、断线状态重置、测试钩子），systemd 重启验证 ✓，旧版备份 `server.py.bak-*`

### 客户端（lan-launcher.cs / updater.cs）
- UpdateUrl → `https://198.51.100.10/mbg-update/latest.json`；显式启用 TLS 1.2
- `--apply-update` 无头模式：无 UI，stdout 输出 NO-UPDATE/UPDATE-OK/UPDATE-FAIL
- `launcher-update.log` / `updater.log` 诊断日志
- staging 优先安装目录（360 拦截 %TEMP% 写入时的回退）；更新器从 staging 目录运行
- staging/替换文件剥离 Zone.Identifier（MotW）

### 测试结果（headless-tests.ps1）
- T1 HTTPS 拉取+签名验证+版本门槛：✓（NO-UPDATE 正确）
- T3 篡改清单（改已签名字段）：✓ 拒绝（签名无效），客户端未动
- T4 损坏 ZIP（sha 不匹配）：✓ 3 次重试后拒绝，客户端未动
- T5 完整替换：**待用户配合**——本机 360 安全卫士拦截未签名更新器 exe 的启动（安全对话框），需要一次人工"运行"或加入信任列表。机制本身已到 apply ok（下载 89MB+校验+解压+MotW 剥离全通过）

### 环境发现
- 测试机同时运行 360 安全卫士 + 荣耀电脑管家 + Windows Defender
- 360 对未签名程序写 %TEMP%/执行下载的 exe 有策略拦截（勒索防护类行为）——正式用户（国内）大概率遇到，已在启动器做缓解（staging 回退 + 明确错误提示）

### 阶段三进展（ECS 稳定性）
- ECS server.py 已升级到 main + start_room 诊断（临时）+ **房主转移修复**
  （重登录曾把 self.owner 置 None，导致下一局无法开局——`316d0d9`）
- `test-public-battle.py` 扩展为完整回归套件，对 ECS 实测 PASS：
  登录×2 → 开局 → UDP 中继 → 战斗断线重连 34/37 → **重连后 UDP 重注册+中继** →
  **mounted 状态中继（vehicle.y 回归）** → **scoped/mounted 断线重置**（`78b0f8b`）
- 自愈能力：登录重试（吸收上一轮宽限窗口）、ensure_ready toggle-first、
  ready+start+connect 重试（finish_round 竞态）
- 10 轮 soak：RSS 稳定（29.8→30.0 MB）、fd 稳定（11）无泄漏；轮间竞态由重试吸收
- 发现并修复：重登录丢失房主（`316d0d9`）；finish_round 竞态（测试侧自愈）

### HTTPS 证书链问题（真实客户端发现，已修复）
- 真实用户升级到 0.10.9 后启动器报"未能创建 SSL/TLS 安全通道"
- 根因：nginx 443 证书链 = **ECDSA + 新根 ISRG Root YE**（Let's Encrypt 2025），旧 Windows 信任库无此根
- 修复：`certbot --key-type rsa --preferred-profile shortlived` 新签 RSA IP 证书
  （`mbg-update-rsa`，ISRG Root X1），nginx 8445 端口独立服务 `/mbg-update/`
- 启动器改为三入口顺序尝试：8445 RSA → 443 ECDSA → 8080 旧 HTTP；
  **每个入口都强制 RSA-SHA256 签名校验**，真实性不依赖传输层
- 验证：8445 的 .NET TLS 握手 200、证书 RSA/SHA256、双入口清单/ZIP md5 一致
- 0.10.9 已发布 GitHub Release（`lan-player-v0.10.9`）
- 注意：旧版启动器（内嵌单一 443 URL）无法自更新到新证书链，需一次手动替换

### 阶段三补充（存活检测与测试收敛）
- **服务端静默检测**（`BATTLE_SILENCE_TIMEOUT=15s`）：客户端每 2s ping，
  15s 无任何战斗消息即判定死亡并断开（黑障链路不再形成僵尸连接），
  随后进入正常重连宽限。已通过实际日志验证
  （`battle silent for 15s; closing player 1`）
- 日志发现的实际问题：**黑障时服务端无检测**（原依赖 TCP 重传超时，分钟级）
- 协议测试收敛：keepalive 线程（2s ping，匹配真实客户端）、
  `ensure_both_ready`（一份房间快照确认双人就绪后才开局，避免部分开局
  锁死 toggle）、登录重试窗口 120s（吸收上一轮宽限期）
- 未完成：**客户端侧黑障心跳**（8s 无数据 → OnDisconnect）未用真实客户端在
  防火墙级黑洞下实测；逻辑审查通过、本机无法构造真实黑洞链路

### qq-agent 解耦（用户计划删除 qq-agent）
- 新增独立 nginx 配置 `/etc/nginx/sites-enabled/mbg-update`（8445 RSA + /mbg-update/）
- 从 qq-agent 配置中移除我加的两个块（443 location + 8445 server），qq-agent 文件干净可删
- 80 端口 ACME challenge 由独立文件 `acme-challenge` 提供，续期不受影响
- **独立续期**：`mbg-update-cert-renew.{service,timer}`（每日 01/13 点，certbot renew
  --cert-name mbg-update-rsa + nginx reload），已 enable
- 已备 `/srv/example/mbg-1`：删 qq-agent 后执行即把更新目录迁到 443
  （标准端口 + RSA 证书），此后可停用 8445
- 注意：当前 443 的 /mbg-update 已失效（返回 HTML），启动器自动降级到 8445 ✓

### 更新链路验证矩阵
| 测试 | 结果 |
|---|---|
| 0.10.9 启动器经 8445 拉取+验签 | ✅ launcher-update.log 实证 |
| 8445 .NET TLS 握手 | ✅ 200 |
| 篡改清单拒绝 (T3) | ✅ |
| 损坏 ZIP 拒绝 (T4) | ✅ 3 次重试后拒绝，客户端未动 |
| v0.10.8 → 0.10.9 实际升级 | ✅ 用户机器完成（界面显示 v0.10.9） |
| 下载/校验/解压 | ✅ apply ok（MotW 剥离生效） |
| 独立续期 timer | ✅ 已 enable（每日两次） |
| 服务端日志轮转 | ✅ /etc/logrotate.d/mini-battleground |

### 安全警告（"无法验证发布者"）根因与修复
- 触发条件：**Mark-of-the-Web（网络下载标记）+ 未签名 exe**
- 为什么 0.10 后才出现：0.9 时代经局域网 HTTP 分享（Intranet 区域不标记）；
  0.10 起走 GitHub/ECS 公网下载，且**自动更新把下载的 ZIP 解压覆盖安装目录 →
  每个被覆盖的 exe 都带标记** → 每次启动都弹
- 修复：更新器替换时剥离标记（0.10.9）+ 启动器启动时清除安装目录全部 exe 标记
  （0.10.10）；效果 = 仅首次浏览器下载后运行弹一次
- 彻底消除首次弹窗需代码签名证书（OV ~$100+/年，需实名）——待用户决策
- 已发布 v0.10.10

### 构建一致性
- 0.10.9 构建输入：`_local-archive/public-client-v0109`（= v0108 底 + 最新补丁 Assembly-CSharp.dll）
- manifest commit = `d3b94a5`+；dist/update/latest.json 双入口 md5 与本地一致

