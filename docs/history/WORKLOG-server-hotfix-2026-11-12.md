# Server hotfix — empty rounds and explosion self-damage

2026-11-12. User requested empty rooms be reclaimed while keeping the ECS service available, and reported that blowing up vehicles cannot hurt the destroyer.

## Scope and reproduction

- Initial working tree clean, main32f6375. Read current AGENTS.md and README. Preserve client installation, player profiles and production matches.
- Existing code excludes the owner from projectile blasts, excludes the shooter from vehicle explosions, and independently rejects shooter==target in apply_damage. Direct bullet reports already reject self-hit separately.
- Empty rounds retain queued/joined players during reconnect grace or loading timeout even when every world/battle connection is gone; new logins remain blocked meanwhile.
- `tests/test-room-and-explosion.py` runs the real Match damage/update code with in-memory world/battle writers and no private map assets. Baseline source fails six subcases: empty loading/started rounds, three owner blast types, and destroyer damage. Evidence stays in ignored build/v0.11.3/room-explosion-before.txt.

## Changes and validation

- Allow owner damage through the existing explosion paths; preserve can_damage radius/protection checks. Self knockdown increments deaths without adding a kill; direct bullet self-hit reports remain rejected.
- Before round updates, reclaim queued matches when no player has a live world or battle writer. Keep the process/listeners running. A live world connection preserves battle reconnect grace; a live battle connection is not treated as an empty room. Existing separate LAN owner-process lifetime behavior is unchanged.
- Seven focused tests PASS after the fix, including loading/started cleanup, projectile/fire owner damage, vehicle self-knockdown, radius/spawn protection, rejected direct self-hit, and live-connection preservation. Existing writer lifecycle tests2PASS. No full multiplayer matrix repeated.
- Scope is ECS server hotfix only: game DLL and launcher unchanged, no additional client download required. Existing v0.11.2 Release assets remain immutable; its bundled Windows server predates this change. Rebuild that EXE before a future packaged host release.
- Deployment pending idle-server check. Back up deployed server.py, retain rollback, and do not restart an active match. Actual user gameplay confirmation remains pending; unit results are not presented as an in-game PASS.

## Deployment

- Source commit089b248 pushed. User explicitly authorized one lobby disconnect for immediate service restart. Rechecked there were no battle TCP clients; backed up old server.py before replacing it.
- Deployed SHA256 `1d8ae2a7a4d09f066248b52d7d763eb86b1bddd8a41387c1112d778c4f093853` matches the staged and local source. Service is active, login/world/battle listeners remain available, and the actual client re-entered the lobby.
- Rollback file beside deployed source: `server.py.before-empty-blast-20261112`. Profiles and existing release/update artifacts were preserved.
- Initial Git push hit a transient DNS failure; retry succeeded. This did not change source content or deployment scope. Actual in-game owner explosion confirmation still pending.
