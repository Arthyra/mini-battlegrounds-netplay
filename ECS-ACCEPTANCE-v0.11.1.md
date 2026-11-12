# v0.11.1 physical Windows acceptance

Status: PREPARATION. No physical dual-machine PASS has been recorded.

## Isolated deployment

The production service remains on 431xx. The independent `mini-battleground-v0111-test.service` runs the same server code on TCP 43220/43223/43234 and UDP 43235, with its own profiles and admin disabled. Faults in this kit target only this instance. The port translation is explicit: production battle TCP 43134 corresponds to isolated test TCP 43234; UDP 43135 corresponds to 43235. Do not apply faults to production.

## Two computers

Use the identical acceptance ZIP on both physical Windows computers. Unzip completely. In PowerShell run `./Start-EcsAcceptance.ps1 -Player pc1` on the first machine and `./Start-EcsAcceptance.ps1 -Player pc2` on the second. Keep these windows open. The script verifies launcher/DLL hashes before launch, assigns distinct profile IDs, records UTC clock status, frame and Connect timings, and TCP connection states. Do not edit the Windows clock; report any offset so the evidence can be aligned.

Both players ready up in the lobby; the room owner starts the game. Confirm map 102, movement and actions in both directions, hit/damage/down/revive, vehicles, weapons/equipment and end/start of the next match. Do not infer these from packet traffic alone.

For the real blackhole, open an administrator PowerShell in the extracted directory and run `./Invoke-EcsNetworkFault.ps1 -Protocol TCP -ClientExe ./client/CrazyShooter.exe -Seconds 25`. UDP-only uses `-Protocol UDP`. These rules target only the test game executable, ECS IP and isolated battle port. A SYSTEM scheduled watchdog is installed before blocking; finally and the watchdog both remove only this run's GUID-named rules. The script records blocking and recovery UTC times. Never substitute a process close, RST or server socket kill for blackhole testing.

Hard TCP drop and loading-stage drop are performed on the isolated ECS instance by the operator. Run each for both player IDs. An untouched peer must remain interactive. Capture hb/od/tick/jb traces, server silence/takeover/UDP registration, state after recovery and duplicated entities. Use protocol/source traces to establish client 8-second and server 15-second detection independently.

Close the test client and run `./Collect-EcsEvidence.ps1`. Only this test's build metadata and evidence directory are collected. Share both machines' evidence ZIPs, not whole user profiles or application directories.

## Required observations for each run

Record operator/machine, build hashes, battle ID and both player IDs, fault direction, UTC block/detect/reconnect/recovered times, max Unity frame time, restored UDP and actual gameplay observations. If any required observation is missing, use NOT RUN/INCOMPLETE rather than PASS. Distinguish an actual gameplay failure from a missing observation.

| Test | Current result | Required evidence |
|---|---|---|
| A normal round and restart | NOT RUN | Two physical clients + ECS |
| B hard TCP drop pc1 and pc2 | NOT RUN | Restore, peer unaffected, UDP |
| C real firewall TCP blackhole | NOT RUN | Block, 8s/15s detection, recover, cleanup |
| D scene/resource/init drop | NOT RUN | No duplicate init, return to playable state |
| E UDP-only block | NOT RUN | Accurate bounded HUD warning, recovered sync |
| 60s hands-off idle | NOT RUN | 2s ping/echo and zero false reconnects |

| Physical recovery round | Result | Evidence / actual state |
|---|---|---|
| 1 | NOT RUN | |
| 2 | NOT RUN | |
| 3 | NOT RUN | |
| 4 | NOT RUN | |
| 5 | NOT RUN | |
| 6 | NOT RUN | |
| 7 | NOT RUN | |
| 8 | NOT RUN | |
| 9 | NOT RUN | |
| 10 | NOT RUN | |

## Open engineering gates

- Instrumented candidate is being smoke-tested; it is not yet a final acceptance build.
- Actual synchronous Connect duration across normal/refused/unreachable/blackhole/server-down cases remains to be measured. Background connect change is not implemented.
- Firewall script syntax checked; elevated application and watchdog cleanup still require actual execution.
- Full old-version update, interrupted download, locked-file installation, rollback and relaunch matrix pending.
- Certificate renewal execution pending. The timer's presence alone is not PASS.
