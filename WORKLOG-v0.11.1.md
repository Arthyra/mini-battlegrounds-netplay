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

Local scratch/build/test results will be kept under ignored `build/v0.11.1/`. Each completed stage records commands, results and commit here. Secrets and private signing keys are excluded.
