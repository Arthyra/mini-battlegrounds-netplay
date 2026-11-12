# AGENT HANDOFF — v0.11.0 ECS Stable

## v0.11.1 acceptance in progress

Read `WORKLOG-v0.11.1.md` and `ECS-ACCEPTANCE-v0.11.1.md` for current unpassed gates. Production remains v0.11.0. The isolated ECS service uses 432xx, its own profile file and admin disabled. External 432xx access still needs diagnosis before physical testing.

Correct patch input on this machine is `C:/Users/Example/.deepseek/mbg-lan-test/Assembly-CSharp-input.dll`. `workcopy/Assembly-CSharp.dll` fails the collision-branch input guard. Do not use it as the unpatched input.

`LanRoomUI.dll` now also requires `ecs-diagnostics.cs` and `ecs-battle-connect.cs` alongside `lan-room-ui.cs` and `lan-scoreboard.cs`. Compile with `/noconfig /nostdlib` and explicit references to the client runtime's mscorlib, System, System.Core, UnityEngine and UnityEngine.UI. This prevents modern framework methods unsupported by the bundled Mono. Compile with `ECS_ACCEPTANCE` only for the isolated test port override. Release builds omit that symbol. `build-acceptance.py` automates this in an immutable output directory.

All formal physical-PC, firewall and upgrade gates remain as recorded in the v0.11.1 worklog, independently of earlier v0.11.0 report conclusions.

给接手继续开发的 Agent。先读本文，再读 `WORKLOG-v0.11.0.md`（进行中状态）、
`ECS-STABLE-REPORT-v0.11.0.md`（已验收项）、GitHub issue #7（剩余任务）。

## 1. 当前状态（2026-11-12）

- 版本 **v0.11.0 — ECS Stable** 已发布：`lan-player-v0.11.0`
- main HEAD 含全部修复；构建输入 commit 见 `dist/release-v0.11.0.json` 的 `commit` 字段
- ECS `198.51.100.10`（`ssh midnight`）：systemd `mini-battleground.service`，
  `server.py --players 8 --host 198.51.100.10 --bind 0.0.0.0 --admin-port 8080`，
  工作目录 `/srv/example/mbg-2`
- 更新入口（均可用，均强制 RSA-SHA256 签名校验）：
  - `https://198.51.100.10/mbg-update/latest.json`（443，RSA 证书链 ISRG Root X1）
  - `https://198.51.100.10:8445/mbg-update/latest.json`（镜像）
  - `http://198.51.100.10:8080/update/latest.json`（旧版兼容入口）
- 更新文件真源：`/var/www/mbg-update/`（443/8445 静态服务）与
  `/srv/example/mbg-2/update/`（8080 由 Python 服务提供）——**两处都要更新**
- qq-agent 已删除；nginx 配置在我们自己控制的
  `/etc/nginx/sites-enabled/mbg-update`；80 端口 ACME 在 `acme-challenge`
- 证书续期独立：`mbg-update-cert-renew.timer`（每日 01/13 点）

## 2. 本地环境

- 仓库：`C:\GameInstall\mini-battlegrounds-lan\map102-test`
- 客户端源目录：`client/`（已打补丁的 `Assembly-CSharp.dll` + `LanRoomUI.dll`）
- 发布构建输入：`_local-archive/public-client-v0109/`（= v0.10.8 底 + 最新补丁 DLL）
- 签名私钥：`C:\Users\Example\.deepseek\mbg-signing2\mini-lan-update-signing-private.pem`
  （**绝不能提交、上传 ECS/GitHub、写进日志**）
- 补丁工具链：`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe` +
  `C:\Users\Example\.deepseek\workcopy\Mono.Cecil.dll`
- Windows 下用 **pwsh**（PowerShell 7），不要用 powershell.exe
- **这些是本机专有、不在 git 里**（新 Agent 必须跑在本机，或先拿到这些资产）：
  `_local-archive/public-client-v0109/`（约 200MB 客户端）、`client/`、
  `dist/`、`workcopy/Mono.Cecil.dll`、签名私钥、`ssh midnight` 配置。
  没有它们就无法构建发布包；仓库只含源码。

## 3. 构建与测试命令（按序）

```powershell
# 1. 编译补丁工具
csc -nologo -out:PatchLanCombat.exe -reference:Mono.Cecil.dll patch-lan-combat.cs
# 2. 打补丁（输入必须是"未打补丁"的原版 Assembly-CSharp.dll）
PatchLanCombat.exe Assembly-CSharp-input.dll Assembly-CSharp-patched.dll LanRoomUI.dll
# 3. IL/JIT 验证（预期：仅 CS.Battle::RemoveUnit 一个基线噪声）
ilcheck.exe Assembly-CSharp-patched.dll
# 4. 装入客户端
copy Assembly-CSharp-patched.dll client\CrazyShooter_Data\Managed\Assembly-CSharp.dll
# 5. 构建发布包（会调用 csc 编译 lan-launcher.cs / updater.cs / fullscreen-helper.cs）
$env:MBG_UPDATE_SIGNING_KEY="...pem"
python build-release.py --version X.Y.Z --client _local-archive/public-client-v0109 `
  --update-base-url "https://198.51.100.10/mbg-update"
# 6. 部署（两处 + 校验 md5 一致）
scp dist/update/latest.json midnight:/var/www/mbg-update/
scp dist/update/Mini-Battlegrounds-LAN-Windows.zip midnight:/var/www/mbg-update/
ssh midnight "cp /var/www/mbg-update/{latest.json,Mini-Battlegrounds-LAN-Windows.zip} /srv/example/mbg-2/update/"
# 7. 协议回归（对 ECS，需服务器无进行中对局；内置登录重试与自愈）
python test-public-battle.py
```

测试钩子（环境变量，正式服默认关闭）：
`MBG_TEST_AUTOSTART=1`（全员就绪自动开局）、`MBG_TEST_DROP_BATTLE=1|2`
（开局后/加载期断线）、`MBG_TEST_DROP_PLAYER=<id>`。
客户端无头更新验证：`Start Game.exe --apply-update`（stdout 输出
`NO-UPDATE/UPDATE-OK/UPDATE-FAIL`，日志 `launcher-update.log`）。

## 4. 必须知道的坑（全部真实踩过）

1. **仓库 `.gitignore` 是白名单制**：新文件默认不追踪！新增源码/文档必须加
   `!/文件名` 到 `.gitignore`，否则"改了却提交不上去"。v0.10.x 的 10 个客户端安装器
   就是这样"丢失"的（只存在于未提交的本地副本）。
2. **工作区文件可能被外部还原到 HEAD**（本机存在 git autocrlf/外部进程干扰）：
   每次改动后立刻 `git add && commit`，不要攒批。
3. **.NET 程序集字符串是 UTF-16**：用 ASCII `grep` 验证 exe 特性会漏判，必须
   `s.encode("utf-16-le") in exe`。
4. **IL 补丁的栈平衡**：`isinst + dup + brtrue` 后每条分支都要成对 pop；值类型
   调虚方法必须先 `box`（否则 Unity Mono JIT 抛 InvalidProgramException，
   **收包路径的异常会触发 Close+重连循环**）。
5. **测试环境 TEMP 会被 Git Bash 污染**（`TEMP=/tmp` → .NET 解析成 `C:\BuildInputs\`）：
   测试脚本必须显式设 `$env:TEMP = "$env:LOCALAPPDATA\Temp"`。
6. **本机三套杀软**（360+荣耀管家+Defender）：360 拦截未签名程序写 %TEMP%/
   执行下载 exe。启动器已做 staging 回退 + MotW 剥离 + 重试，不要"优化"掉。
7. **证书链**：443 必须用 RSA（ISRG Root X1）证书；ECDSA/ISRG Root YE 链在旧
   Windows 上握手失败。证书 6 天有效期，续期由独立 timer 负责。
8. **协议偏移**：UDP 52 包 = `[size2][msg2][flag2][sender4][status20]`，status 在
   偏移 10；ROOM_STATE 成员块 `offset=8` 起、每成员 8+34 字节。
9. **全局指令：Windows 用 pwsh**。

## 5. 剩余任务（issue #7）

- [ ] 两台物理 Windows 机器 10 连断线回归（本机双开已覆盖，物理双机未做）
- [ ] 客户端黑障心跳（8s 无数据 → OnDisconnect）真实防火墙黑洞实测
- [ ] （可选）购买代码签名证书可消除"首次运行"安全提示

## 6. 红线

- 不覆盖已有 Release 资产/Tag；不发未验证版本。
- 签名私钥只在本机使用。
- 改 ECS 前先备份（`server.py.bak-*`、nginx 配置备份），`nginx -t` 通过再 reload。
- 不要把"编译成功/协议模拟通过"当成真实客户端验收。
