# v0.11.2 launcher hotfix

2026-11-12: user reported repeated update prompt after installing v0.11.1.

- Initial Git worktree clean; main 31df335. User launcher path is `dist/Mini-Battlegrounds-LAN/Start Game.exe`; preserve installation and game data.
- Confirmed root cause: LocalVersion uses Version.ToString(3), dropping revision10 from0.11.1.10. The release gate had checked AssemblyName.Version instead of the actual LocalVersion method and missed this.
- `test-launcher-version.cs` executes real compiled launcher methods in a fixture process whose file version is0.11.1.10. Original formal launcher returns0.11.1 and reports same_is_newer=True, deterministically FAIL (`build/v0.11.2/version-before.txt`).
- Minimal fix preserves all version components. No game/server logic changes. Publish separate v0.11.2 / numeric0.11.2.0; preserve existing Release assets/tags.
- Targeted validation only: actual version comparison, final same-version no-update check, signed artifact/hash and unchanged game/server payloads.
