# v0.11.1 ECS stability acceptance

## 2026-11-12 — Baseline and execution scope

- Stage 1 in progress. Read `AGENT-HANDOFF.md` in full.
- Initial worktree clean. Local main `f80ab02`, including handoff commits after `439f935`.
- GitHub latest release `lan-player-v0.11.0`; issue #7 OPEN.
- ECS `ssh midnight` reachable; production `mini-battleground.service` active with TCP 43120/43123/43134 and UDP 43135. No disruptive production tests authorized or performed.
- Existing v0.11.0 report says all acceptance criteria met while listing physical dual-machine regression and real firewall blackhole as untested. These remain NOT RUN for this goal.
- Confirmed candidate race in `Match.battle.finally`: stale writer can cancel the replacement writer's UDP warning task. Next action is a deterministic failing asyncio regression before changing cleanup.
- Next: isolate ECS testing, inventory DLL/build hashes, prepare physical-machine test scripts and evidence collection, then request second-machine access once the package is ready.
- Physical two-Windows-PC tests A–F, 10-round physical regression, real firewall blackhole, and upgrade matrix are NOT RUN.
- No v0.11.1 release published. Test build must remain a candidate until acceptance succeeds.

## Evidence

- User selected manual two-PC execution and log return, then authorized use of their configured Alibaba Cloud CLI. Read-only CLI found the midnight instance security group allowed only the production ports (not all inbound ports). Added four temporary acceptance rules limited to the current SSH source IPv4 /32. Exact rules/responses are in ignored `build/v0.11.1/ecs-test-securitygroup.json` for removal after acceptance. Public test login now returns message 20. Production rules unchanged.
- On-host isolated ECS protocol regression PASS (`ecs-isolated-protocol.txt`): login, start, UDP forwarding, battle reconnect, UDP re-registration, mounted status and scoped/mounted cleanup. This is protocol simulation, not physical-client acceptance.
- Candidate 0.11.1.3 local two-Unity idle observation: >110 seconds each, 55 ping echoes each with median 2.008s intervals, no disconnects. `idle-local.json` is LOCAL ONLY.
- Second physical PC screenshot showed PSSecurityException before the startup script ran. Added a native .NET EXE startup/collection helper, preserving system execution policy and the exact tested game DLLs. Helper `--verify` PASS. Delivered a small supplemental ZIP; second PC should run `ECS-Acceptance.exe pc2`. Physical gameplay/fault tests still NOT RUN.

- Candidate `0.11.1.3` (`bd096dc` source, clean build) has entered lobby and map 102 with two real local Unity processes using async Connect. Normal completion took 26/27 ms including Main.Update scheduling. Actual ping echoes now appear at approximately 2-second intervals. Physical-machine/ECS testing remains NOT RUN.
- Certificate renewal dry-run PASS: `mbg-v0111-cert-dryrun.service` ran certbot's staging renewal, journal at 2026-11-12 04:07:06 UTC reports all simulated renewals succeeded. Production certificate was not replaced. Next actual scheduled renewal remains pending.
- Installer lock/rollback tests and async socket helper tests are separate from actual Unity and physical acceptance. No final v0.11.1 release created.

- P1 updater failure reproduced with the real pre-fix updater and a Windows FileShare.None lock: first file was overwritten before a later locked target failed (`locked-before.txt`). New installer preflights target occupancy, replaces files atomically with rollback copies, and restores prior files on failure. Seven compiled installer tests PASS, including occupied-file rejection, injected partial-install rollback, success and preserved unrelated Zone.Identifier (`installer-after.txt`). Whole-version upgrade/relaunch matrix still pending.
- Candidate async battle Connect now initiates only Socket.BeginConnect outside the Unity frame; Main.Update polls completion, adopts socket and starts the original receive loop. Join Send is queued until adoption. Per-attempt identity prevents stale Close cancelling a newer attempt. No Unity object is accessed by a background connect worker. Nine compiled socket/helper tests PASS (`battle-connect-tests.txt`), including refusal, deadline and replacement cancellation. This is NOT a real firewall blackhole PASS.
- Moved stale battle-disconnect identity guard ahead of clearing the shared heartbeat-active flag, preventing an obsolete proxy callback disabling the new proxy's heartbeat.

- `7fb5e48` adds the isolated-instance/physical-PC kit. Candidate Unity runtime initially exposed unsupported `Monitor.Enter(object, ref bool)` from framework-default compilation. Fixed to legacy Monitor.Enter/Exit and compiled against the bundled runtime using `/noconfig /nostdlib`; new candidate enters the original lobby and map 102 in two actual local Unity processes, with no exception stacks in their new logs. This is LOCAL SMOKE, not physical/ECS acceptance.
- Measured local normal synchronous battle Connect on Unity thread 1: 6.817 ms and 6.809 ms in the two clients. Fault-path timing remains pending.
- External 432xx protocol probe closes/times out while ECS-loopback protocol probe returns message 20. Instance INPUT policy ACCEPT. New-port external reachability remains an open preparation gate; do not claim ECS candidate play PASS.
- Found existing receive trace recorded local 2 (frame size), not local 3 (message ID). Corrected the trace operand so 2-second ping echoes can be counted. The prior local idle observation has no reconnects but its ping trace cannot establish cadence; repeat is required after rebuild.

- Isolated ECS unit created under `/srv/example/mbg-3`, with separate profiles, admin disabled and 432xx ports. Its TCP endpoints are reachable. Production unit remains active on 431xx. No production disconnect, firewall change or restart performed.
- Found correct unpatched input at `C:/Users/Example/.deepseek/mbg-lan-test/Assembly-CSharp-input.dll`. The handoff's workcopy DLL fails the patcher's collision-branch guard; it was not used in the candidate.
- Added opt-in main-frame/Connect timing, UTC+thread IDs, bounded two-file network diagnostics, and a compile-gated acceptance login port override. Release builds must compile without `ECS_ACCEPTANCE`.
- Candidate CLR IL comparison shows the same Unity internal-call failures and baseline `Battle.RemoveUnit` MissingMethodException as v0.11.0, with no added non-internal-call failures. This is not a Unity runtime PASS; actual candidate launch validation is pending.
- Added distinct-PC launch/hash verification, evidence collection and temporary firewall scripts, and `ECS-ACCEPTANCE-v0.11.1.md` with explicit NOT RUN rows. All three PowerShell scripts parse successfully. Admin firewall execution remains NOT RUN.

- `4519bff` captures the baseline and deterministic failing regression. `python test-connection-lifecycle.py` reproduced stale A cancelling B's warning (1 FAIL, 1 PASS), saved in `build/v0.11.1/lifecycle-before.txt`.
- P2 server task-ownership fix scopes warning cancellation to the current writer. Re-run result is recorded with the fix commit. Production is unchanged.
- `14a89b5` fixes task ownership; `build/v0.11.1/lifecycle-after.txt` records 2 PASS.
- HTTPS actual deployment: both 443 and 8445 return signed manifests with valid system-trusted TLS from Windows/.NET. Both nginx listeners use the RSA `mbg-update-rsa` certificate, expiring 2026-11-18. Renewal timer enabled but has no execution history yet; renewal success NOT VERIFIED.
- P2 update transport fix removes HTTP fallback, rejects HTTP package URLs and automatic redirects, retaining pinned RSA-SHA256 signature validation. `test-update-policy.cs` runs against the compiled launcher and real ECS manifests: 7 PASS in `build/v0.11.1/https-policy.txt` (including forged signature and HTTP refusal).
- Removed startup-wide and update-stage Zone.Identifier deletion and destination ADS stripping. No Windows security settings changed. Newly compiled launcher/updater compile successfully; full installer regression still pending.
- Download network/IO interruption now gets bounded retries using a fresh download; installation starts only after SHA256 validation.

Local scratch/build/test results will be kept under ignored `build/v0.11.1/`. Each completed stage records commands, results and commit here. Secrets and private signing keys are excluded.

## 2026-11-12 — First physical session, visual regression found

- Two distinct physical Windows PCs entered isolated ECS battle 1 / map 102 at 12:28:08 local (04:28:08 UTC). User confirmed second PC sees the other player. Server records hits/down/revive from both players; normal-test A remains INCOMPLETE (vehicle/equipment/restart observations not complete).
- At 04:29:48 UTC, selectively killed only player 1 battle TCP socket (isolated 43234, observed peer port 18504). Client callback, async reconnect, messages 34/37/39/40 and server UDP registration all returned in the same second; Connect completion 6 ms, next frame summary max 69 ms. User confirmed reconnect succeeded BUT reported new bright/dark pavement grid on PC1 and supplied screenshot. This round is FAIL for visual state, not PASS. Evidence: `build/v0.11.1/physical-session-server.log`, candidate3 `evidence/pc1-20261112-122024/frames-connect.log`, user screenshot `codex-clipboard-0176d839-0504-4f9c-9f2d-a7531772eb7c.png`.
- P2 visual regression under investigation. Stage re-entry reloads the scene; CameraController also adds Occluding and Blur components without an existing-component guard. This is a candidate cause, not a proven diagnosis. Added acceptance-only before/after scene, camera-component, renderer/lightmap diagnostics to obtain evidence before changing rendering behavior.
- Planned player 2 hard-drop command matched zero sockets because round 1 had already ended on its 5-minute limit at 04:33:17 UTC. That test is NOT RUN.
- Legacy 0.10.8 reflection harness installed candidate files, but its process identity caused the installer to relaunch the harness, so auto-relaunch is NOT VERIFIED by that run. Revised actual-process test: only the three manifest feed string literals in a disposable 0.10.9 launcher copy point to the signed isolated HTTPS fixture; update/signature/download/install logic is unchanged. Actual old launcher PID 55668 exited, new launcher PID 51268 started and fetched the production manifest at 04:33:24 UTC. All four installed acceptance hashes match. This proves the fixture-fed update/install/restart path, not unchanged production manifest discovery for legacy versions.
- Native firewall helper compiles; usage guard executed. Elevated application/watchdog execution is still NOT RUN. Watchdog uses only Windows-owned netsh/schtasks binaries, never SYSTEM execution of a user-writable helper. No rules applied on this non-admin agent session.

## 2026-11-12 — Physical PC2 evidence and graceful EOF

- Received `C:/Workspace/ECS-evidence-pc2-20261112-122213.zip`; extracted safely under ignored `build/v0.11.1/received-pc2-122213`. Metadata matches candidate3 / bd096dc and a distinct physical Windows host. PC2 clock service was not running, so cross-machine UTC alignment is approximate.
- User reports PC2 also acquired pavement artifact after starting round2, while freshly restarted PC1 remained normal. Candidate4 repeat scene diagnostics show a fresh camera, no doubled renderer count and 3 lightmaps. User reports PC1 currently normal after diagnostic reconnects. Cause remains OPEN; no visual fix claimed.
- PC2 last battle echo 04:38:37.695 UTC; heartbeat fires 04:38:45.702 (~8.007s), Connect completes in 4ms, but no join response follows. Server cleans old connection after 15s silence. Battle/world sockets enter CLOSE_WAIT, then login socket remains CLOSE_WAIT through capture end. Frames continue, typically 144fps / max8-14ms. FAIL recovery, no sustained frame freeze. This uncontrolled incident is not a firewall-blackhole PASS; initial cause of lost traffic is unknown.
- P1 actual candidate3 TcpPeer.StartReceive graceful FIN reproduction: receive_returned=False, socket_cleared=False, queued_bytes=4 (`tcp-fin-before.txt`, `test-tcp-fin.cs`). d86810c handles EOF without discarding buffered frames. Review found its instrumentation must remain on the exception handler, not EOF path; corrected before acceptance.
- PC2 unity.log also contains TickLanTcpHeartbeat NullReferenceException after round teardown. d86810c adds an absent-proxy guard; separate execution validation pending.

- Actual-DLL FIN regression now PASS (`tcp-fin-after.txt`): receive_returned=True, socket_cleared=True, queued_bytes=4, early_callbacks=0, dispatched_packets=1, final_callbacks=1. Test subclass only counts callbacks; actual TcpPeer receive/dispatch/close code executes unchanged. Baseline candidate3 fails with receive thread/socket retained.
- Candidate5 was built before the exception-instrumentation placement correction and is NOT approved for acceptance. Next candidate must use the corrected catch-scoped instrumentation.
- Actual fixture-fed v0.11.0 process update also completed: old launcher PID45512 exited, new PID45212 started and fetched production manifest at04:38:38 UTC; all four candidate3 binary hashes match. No production update manifest changed. v0.10.8 lacks --apply-update and its hidden UI has not completed; that version's auto-relaunch remains unverified.

- `test-battle-connect.cs` now reproduces accepted TCP with no application join response: baseline helper fails to time out (`battle-join-before.txt`). Added an 8s main-thread join deadline, disarmed only for the matching proxy's join response. Cancellation claims use CompareExchange so receive-thread Close cannot clear a replacement attempt. Updated helper test initially 10 PASS; response/stale-response coverage added before next build.
- `test-reconnect-state.cs` executes actual client lifecycle IL with only the native realtime clock replaced by fixed1000f. Candidate3 reproducibly fails all3 checks: absent-proxy heartbeat NRE, asynchronous failure resets accumulated retry count, retry cap does not stop before opening another connection (`reconnect-state-before.txt`). Source now preserves attempts across callbacks, caps before starting attempt21, resets after real join/new lobby login, and uses the correct StageManager receiver on exhaustion. Full corrected-candidate validation pending.
- Main through8df9a8d pushed successfully. Issue#7 updated to reflect physical failures/open gates; remains OPEN. No stable release.
- Candidate6 build correctly rejected Stopwatch.Restart because bundled Unity runtime predates that API. Replaced it with Reset/Start; no candidate6 client was produced or used.
