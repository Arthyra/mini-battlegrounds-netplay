# v0.11.3 lobby UI fixes

2026-11-12. User reported rename control remaining in battle and non-owner ready control labelled as start.

- Initial worktree clean, main e0ccbd2. Read current AGENTS.md. Preserve unrelated existing local server and the user's running installed client.
- Root cause: rename is parented to the shared Canvas, but LanRoomUI belongs to the hall ready button. Hall deactivation can stop Update before it hides the separately parented rename object.
- Add OnDisable to close rename state/hide its button and OnDestroy to remove the owned rename object. Keep the existing layout and rename behavior. Ready label now uses only ready/not-ready state, not ownership; the owner's independent start button remains unchanged.
- Compile against the bundled Unity runtime without ECS_ACCEPTANCE; reuse unchanged production Assembly-CSharp.dll. Actual tested LanRoomUI.dll is the release input.
- Local UI smoke: two actual Unity processes, isolated loopback service on a different local address to preserve an already running local server. Test wrapper shortens round length to60s only; production server code unchanged. Verified non-owner Ready/Cancel Ready, owner's separate Start, map102 entry without rename button, F2 not opening rename in battle, round end and lobby rename/F2 restored without duplicate controls. No NullReference/MissingMethod/InvalidProgram stacks in new logs.
- Evidence: inline computer-use observations and ignored build/v0.11.3/ui/owner.log and guest.log. No full network acceptance repeated. Test processes stopped and computer-use kernel reset after observation; existing local server and user game not terminated.
- Bundled Windows server rebuilt from deployed room/explosion source. Existing Release assets remain immutable; publish v0.11.3 / package0.11.3.0. Deployment and artifact checks pending.

## Publication

- Build source a5c1862. Release ZIP SHA256 `371f8a852c810400788c1ec16947377995e90bfb91ac4e8e8c0826f35be1cc6a`, 89,798,968 bytes. Packaged UI DLL matches the actual Unity smoke input; game assembly unchanged; bundled server matches the fresh host build. No acceptance port override present.
- Actual launcher same-version check against the live signed feed prints `NO-UPDATE current=0.11.3.0 latest=0.11.3.0`. Restored the user's desktop shortcut after the isolated check.
- Published formal lan-player-v0.11.3 / title v0.11.3 — 大厅界面修复. GitHub asset digest, local ZIP and both ECS canonical aliases match. HTTPS443/8445 and legacy8080 manifests advertise the same versioned HTTPS package.
- Old manifests/ZIP aliases backed up with before-v0113 suffixes; prior Release assets/tags preserved. No production service restart. Only owned local UI test processes stopped; existing local server and user-installed game preserved. The UI patch requires a client update.
