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
