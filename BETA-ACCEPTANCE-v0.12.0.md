# v0.12.0 LAN Beta — 候选版本验收结论（不公开发布）

候选包：`dist-beta/Mini-Battlegrounds-LAN-Windows.zip`（owner 版，含服务端）
commit：`7067c87`；SHA256：`7a14a657821134ee08c153b9f3091c156a9e2b9da2bed6a45df543540b2cb3b1`（2026-11-15 重算——干净 clone + 机器本地输入经 `tools/rebuild-client-dlls.py` 与 PyInstaller spec 重建，DLL 与发布版 md5 一致 9d999564，流程可复现；此前的 `72127a4` 包含陈旧 server.exe，已作废）

## 已验证（单机证据）
- 13 单测全绿；CI（Python + Windows 编译检查）绿
- 协议回归 `--remote` 对 ECS 与对 **WLAN 绑定的本地房间**（跨接口 LAN 形态）均 PASS：
  登录/开局/UDP 中继/战斗断线重连/UDP 重注册/乘车状态/断线状态重置
- 真实客户端 + ECS + iptables 双向丢弃：8s 黑障自检 → 自动重连 →
  快照重放 → 场景纹理/光照完整（0.11.5 内容，本包继承）
- 房间发现端点计算：本机真实网卡 5 子网覆盖（隧道/WSL/VMware×2/WLAN）
- 跨网卡发现：房间绑定 WLAN、默认路由走隧道，6 条路径应答 ✓（单机）
- 更新链：8445/443 双 HTTPS 入口 + 8080 旧兼容，签名/SHA256 强制

## 未验证（双机验收时必须执行）
- [ ] 两台物理 Windows：房主开房 → PC2 发现并加入 → 完整对局 → 多局循环
- [ ] 物理双机断线恢复 10 连（PC2 端口黑洞 + Wi-Fi 断连）
- [ ] 手机热点场景（不同子网间的发现）
- [ ] Windows 防火墙真实拦截下的发现与连接
- [ ] 完全离线（断外网）启动时长与离线对局

## 2026-11-17 工程验收（v0.12.0-beta 候选收敛）

上一节记录的是 7067c87 候选（7067c87→v0.11.x 内容）。本轮在 2d91eec..188434f 上重做
了候选收敛：故障注入回归落地、验收构建链修复、候选包双包重建。

### 新增自动化回归（全部本地 PASS）
- `tests/test-multiround.py`：同一服务器连续 3 局；每局等待服务端关闭 battle 连接
  （finish_round EOF）再重新就绪；就绪 toggle 只发一次（重试再 toggle 会把就绪态
  翻回去）；UDP 82 探针带当轮 battle_id（陈旧 id 被数据报校验静默拒绝）。覆盖：
  82→52 中继、UDP 中断不触发 TCP 重连、回合结束重置周期。
- `tests/test-crash-restart.py`：自含服务器生命周期；PyInstaller onefile 需
  `taskkill /F /T` 杀整树（proc.kill() 只杀引导进程，子进程仍占端口）；
  kill -9 → 重启 → 双人重登 → 新进程完整开局。
- `tests/test-jitter-proxy.py`：world/battle 两条 TCP 链路经帧级随机延迟代理
  （每帧 0.05-2.0s 每方向）；最坏服务端间隔 ~4s，远小于 15s 静默窗口；整局完成、
  战斗帧持续到达、大厅链路存活到回合结束后。ping 必须在握手期间启动（抖动握手
  本身消耗 ~10s 大厅计时）。
- UDP 部分丢包不单测：数据报相互独立，100% 中断（multiround）为最严苛超集。
- 套件 13 单测全绿；4 个故障脚本 + 单测全部本地验证（2026-11-17）。

### 干净区验收构建暴露并修复的缺陷（2d91eec..188434f）
1. build-acceptance 未把 Unity 运行时放在 LanRoomUI.dll 旁 → 补丁工具解析 UI 依赖
   失败（3602390）。
2. 主模块 UnityEngine 引用从 exe 目录/cwd 解析（6c79345）。
3. 补丁步骤 cwd 依赖输入目录内容 → 改为从 staging 目录运行（09b302c）。
4. build-friend-zip 按模块名导入 prepare_lan_client，干净克隆 ModuleNotFoundError
   （861ccf2）。
5. 预发布版本号（0.12.0-rc2）使 AssemblyVersion 非法（188434f）。
另：确定原始输入 SHA256=928baee6…84c2（补丁产物与现用客户端逐字节一致 57cfc55f…）
——过去构建依赖一个未记录位置的会话临时文件，这是最大的可复现性风险，已固化到
机器本地稳定路径（不入库）。

### 本轮候选包（commit 188434f，2026-11-17 干净克隆构建）
- owner（含服务端）：`05f6ad84871b504b211b7ea4cfca36112e21c2d3a840ebcf169026c29521a126`
  （90,028,192 字节；server.exe 克隆内 PyInstaller 重建 + 冒烟通过）
- join（仅加入）：`51b83de0aed788cb4785aceca54260c3fc01717b38a55cf139f2f94c36eaf0c4`
  （80,317,270 字节）
- 两包内 Assembly-CSharp.dll 与确定性参考逐字节一致；update/latest.json 以 pinned
  RSA 私钥签名（本机可信构建环境，私钥不入库）
- 验收包（ECS_ACCEPTANCE 变体）：`map102-verify-out/acceptance-rc2/`（本机，manifest
  commit 188434f、dirty=false）

### 环境变量化输入（本轮固化）
- MBG_PATCH_INPUT：未打补丁原始 Assembly-CSharp.dll（SHA256 928baee6…84c2）
- MBG_MONO_CECIL：Mono.Cecil 0.11.6
- MBG_CLIENT_DIR：新版发行版基础客户端（含 UnityEngine 运行时）
- MBG_UPDATE_SIGNING_KEY：pinned RSA PEM；MBG_ECS_HOST / MBG_TEST_HOST：服务器地址


## 已知限制
- 连续重进地图的内存占用未测量（前台占用推迟；多轮重进的正确性已验证）
- 客户端无"房主已离开"提示（服务端转移所有权后客户端经 ROOM_STATE 感知，
  新房主的开始按钮即生效——功能完整，仅缺 toast）
- ECS 服务端大厅静默修复已入 main；正式 ECS 的实际部署版本须另行核对（不涉及 LAN）
