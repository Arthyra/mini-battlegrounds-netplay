# v0.11.1 physical Windows acceptance

Status: PHYSICAL TESTING FOUND FAILURES. No completed physical dual-machine acceptance PASS has been recorded. Candidate3 PC1 recovered TCP/UDP but showed a pavement artifact; PC2 later lost traffic and remained at login. These failures require repair/retest, not a stable release.

## Isolated deployment

The production service remains on 431xx. The independent `mini-battleground-v0111-test.service` runs the same server code on TCP 43220/43223/43234 and UDP 43235, with its own profiles and admin disabled. Faults in this kit target only this instance. The port translation is explicit: production battle TCP 43134 corresponds to isolated test TCP 43234; UDP 43135 corresponds to 43235. Do not apply faults to production.

## Two computers

Use the identical acceptance ZIP on both physical Windows computers. Unzip completely into a new directory. Run `./ECS-Acceptance.exe pc1` on the first machine and `./ECS-Acceptance.exe pc2` on the second. Keep these windows open. The EXE verifies launcher/DLL hashes before launch, assigns distinct profile IDs, records UTC clock status, frame and Connect timings, and TCP connection states. It requires no PowerShell execution-policy change. Do not edit the Windows clock; report any offset so the evidence can be aligned.

Both players ready up in the lobby; the room owner starts the game. Confirm map 102, movement and actions in both directions, hit/damage/down/revive, vehicles, weapons/equipment and end/start of the next match. Do not infer these from packet traffic alone.

For the real blackhole, only when coordinated with the operator during battle, open an administrator terminal in the extracted directory and run `./ECS-NetworkFault.exe tcp 25`. UDP-only uses `./ECS-NetworkFault.exe udp 25`. These rules target only the test game executable, ECS IP and isolated battle port. A SYSTEM scheduled watchdog is installed before blocking; it executes only Windows-owned netsh/schtasks binaries. Finally and the watchdog remove only this run's GUID-named rules. The helper records blocking and recovery UTC times. Elevated application/watchdog execution still requires verification. Never substitute a process close, RST or server socket kill for blackhole testing. The older PS1 alternatives are retained but not required.

Hard TCP drop and loading-stage drop are performed on the isolated ECS instance by the operator. Run each for both player IDs. An untouched peer must remain interactive. Capture hb/od/tick/jb traces, server silence/takeover/UDP registration, state after recovery and duplicated entities. Use protocol/source traces to establish client 8-second and server 15-second detection independently.

Close the actual test game window (leaving battle for the lobby does not exit the game). The EXE monitor automatically collects this run's build metadata and evidence into `ECS-evidence-pcN-*.zip`. Share both machines' evidence ZIPs, not whole user profiles or application directories.

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
| 1 | FAIL | Candidate3 PC1 hard drop, 2026-11-12 04:29:48 UTC: TCP/UDP recovered; user observed new pavement artifact. |
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

- Candidate 0.11.1.3 physical gameplay and first recovery exposed visual/state failures. PC2 evidence received; corrected-candidate physical regression remains pending.
- Async Socket.BeginConnect, main-thread socket adoption and queued join sends are implemented. Normal local connection completed in 26/27 ms including frame scheduling. Refusal/deadline/cancellation helper tests pass. Real unreachable/blackhole/server-down frame measurements remain pending.
- Firewall script syntax checked; elevated application and watchdog cleanup still require actual execution.
- Full old-version update, interrupted download, locked-file installation, rollback and relaunch matrix pending.
- Certificate renewal dry-run succeeded on ECS. Actual next scheduled renewal remains pending.
