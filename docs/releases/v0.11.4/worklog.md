# v0.11.4 gameplay tuning

2026-11-12. Requested: +30% vehicle top speeds and shorter throwable hand delay.

- Initial tree clean at25ebb3b; read AGENTS.md, README and open Issue7. Changes restricted to vehicle limits and throwable timing.
- Vehicle limits (forward/reverse, game units per second): hatchback20.8/9.75, pickup11.7/5.2, van14.3/6.5. Existing skin mapping covers kinds1–6. Acceleration, steering and collision algorithm unchanged.
- Server quick-click delay was0.35+0.55=0.90s; now0.10+0.15=0.25s, subject to50ms ticks. Holding still delays release until mouse-up. Native client data parser consumes the original columns then overrides only prepare/toss fields; no animation controller or projectile physics replacement.
- tests/test-gameplay-timing.py: two tests, covering12 vehicle direction/variant cases and2 throw release cases; all pass. Running against HEAD server code fails14 subcases as expected. Throw test checks no early release and exactly one spawn.
- Existing tests/test-room-and-explosion.py:7 pass. No broad network matrix repeated.
- Patched pristine client input with production LanRoomUI.dll. IL comparison against v0.11.3 reports only ThrowableDataManager.Load changed. Actual patched DLL invoked in a data-loader harness: prepare0.1, toss0.15, completion/fuse retain fixture values1.0.
- Windows host EXE rebuilt successfully. Ignored evidence/tools: build/v0.11.4/ (server-build.log, client logs, baseline runner, IL comparison and data-loader harness).
- Actual Unity visual check pending: initial isolated launches stalled visually at hall loading, including unchanged v0.11.3 DLL. No new exception stack in correct-working-directory launches. First launch used wrong working directory and produced resource-load errors; discarded. Temporary phase-logging DLL is diagnostic only, not a release input; restored clean patched DLL afterward.
- Formal ECS and update feed unchanged. Do not claim live gameplay or release acceptance yet.

## Targeted Unity check completed

- Re-launched with an explicit working directory and visible window, activated it and reached the hall/map102 using the clean production patch (no diagnostic injection). Previous stalled observations were not a reproducible patch regression; unchanged DLL had the same symptom. Hidden/background launches are unsuitable for this visual check.
- Actual quick-click: equipped throwable with4; clicked once; observed grenade in flight and automatic return to primary weapon. Server recorded one kind1 throw and successful TCP entry/load plus UDP registration. Projectile subsequently disappeared; no new exception/InvalidProgram/MissingMethod stack in smoke.log. Screenshot cadence does not establish a measured0.25s visual latency; that bound is verified at server scheduling level only.
- Only owned local test game/helper stopped. No dual-PC/network acceptance repeated. Formal ECS and update feed remain unchanged pending deployment approval.
