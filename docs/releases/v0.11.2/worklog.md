# v0.11.2 launcher hotfix

2026-11-12: user reported repeated update prompt after installing v0.11.1.

- Initial Git worktree clean; main 31df335. User launcher path is `dist/Mini-Battlegrounds-LAN/Start Game.exe`; preserve installation and game data.
- Confirmed root cause: LocalVersion uses Version.ToString(3), dropping revision10 from0.11.1.10. The release gate had checked AssemblyName.Version instead of the actual LocalVersion method and missed this.
- `test-launcher-version.cs` executes real compiled launcher methods in a fixture process whose file version is0.11.1.10. Original formal launcher returns0.11.1 and reports same_is_newer=True, deterministically FAIL (`build/v0.11.2/version-before.txt`).
- Minimal fix preserves all version components. No game/server logic changes. Publish separate v0.11.2 / numeric0.11.2.0; preserve existing Release assets/tags.
- Targeted validation only: actual version comparison, final same-version no-update check, signed artifact/hash and unchanged game/server payloads.

## Result

- Regression after fix: file=0.11.1.10, local=0.11.1.10, same_is_newer=False, next_is_newer=True (`build/v0.11.2/version-after.txt`).
- Exact formal executable, against live signed update feed: `NO-UPDATE current=0.11.2.0 latest=0.11.2.0`. Desktop shortcut restored after this isolated check. No extra multiplayer regression performed.
- Both game DLLs and bundled server.exe have identical SHA256 to v0.11.1. Existing production service was not restarted; ongoing players were not disconnected.
- User's login wait correlated with existing in-progress match and rejected new-login requests. Later observed that the actual installed client established world/battle TCP and UDP; README documents the mid-round login limitation. No mid-match join feature added.
- Preserved concurrent remote documentation organization by a normal merge; no history rewrite. Tests/release notes/worklog use the new repository directories.
- Published formal `lan-player-v0.11.2`, title `v0.11.2 — 修复重复更新提示`. Local ZIP, GitHub asset digest and ECS copies agree: `3ab07de8e543c586288300b1eaef3af9465c5aab249d435e3d459e9e7c460174`.
- HTTPS443/8445 and legacy8080 manifests advertise0.11.2.0 with HTTPS versioned download. Canonical aliases also updated; v0.11.1 manifest/ZIP backups retained for rollback. No prior Release/Tag overwritten.
