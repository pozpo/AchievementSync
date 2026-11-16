# Development status

Updated: 2026-11-16

## Current state

- This is the existing AchievementSync project in `C:\AchievementSync`; no second app or versioned output was created.
- The source remote is `https://github.com/pozpo/AchievementSync.git`. The original remote README and MIT license history were merged without force. The prior harness was pushed to `main` at `42259c1d3eec9b2a2ec7694d93dfda4523939a74`; the final raw-cache handling, matching fixture path, and live-test handoff continue on that same branch.
- The viewer data repository is separate. Public GitHub confirms `pozpo/achievement-viewer` is owned by `pozpo`, is a fork, and has a writable `user` branch. The local `achievement-viewer/` folder has invalid Git metadata and remains ignored and untouched.
- The existing executable remains at `C:\AchievementSync\output\AchievementSync.exe`; its application identity, icon, stable file metadata, and previous publish with the public OAuth Client ID were preserved. This session changed only the smoke-test project, fixture path, and documentation, so the executable was not republished.
- AchievementSync authenticated GitHub account `pozpo` with `public_repo`, confirmed the public writable fork `pozpo/achievement-viewer`, and targeted branch `user`. Automatic sync remained off.
- A successful synthetic-only commit added only `AppID/9999999998/achievements.json` ([commit `d5215a6`](https://github.com/pozpo/achievement-viewer/commit/d5215a60f4fc85552ca3f2458873331d684c1b30)). The viewer's raw `user`-branch data endpoint returned the exact synthetic fixture. Cleanup commit [`3ec7f7d`](https://github.com/pozpo/achievement-viewer/commit/3ec7f7d4a2feebe379cde5f93e0e26c5f2ecdd5b) removed only that file. The current GitHub Contents API reports 404 for the path. The reserved ID is absent from the viewer's default-branch index, so it was not shown as a game card. Raw CDN caching can temporarily serve an earlier synthetic response after cleanup.
- Two earlier synthetic-only attempts using ID `9999999999` were also followed by cleanup commits after a cached raw-file 404. They touched no other paths. No local achievement profile data was uploaded.
- Three ignored historical EXEs under `dist`, user files, legacy scripts, and the local viewer folder remain untouched; see [the cleanup plan](CLEANUP_PLAN.md).

## Milestones

| Milestone | Status | Evidence / remaining work |
|---|---|---|
| 1. Inspection and architecture | Complete | Supplied archives reviewed; findings and architecture are in `docs/guides/`. |
| 2. Desktop foundation | Implemented; window check pending | Existing WPF dashboard, scan controls, details, metadata cache, and logging build. The app window was not launched because Computer Use returned `Computer Use was not approved to use achievementsync`. Do not work around this desktop permission block. |
| 3. Achievement processing | Initial adapters complete | JSON, `.db`, Goldberg-style catalog, viewer catalog, and partial INI support; emulator-specific fixture coverage remains incomplete. |
| 4. Metadata and artwork | Initial integration complete | Steam Store names/header artwork cache and achievement icons; metadata availability depends on public data/network access. |
| 5. GitHub synchronization | Synthetic live path verified | Real Device Flow credentials authenticated `pozpo`; the app checked `public_repo`, ownership, public push access, and `user` branch. The one-file synthetic commit, GitHub contents readback, viewer raw endpoint readback, and cleanup were verified. The fallback AppID was intentionally unindexed, so UI card discovery was not part of this check. No local profile sync was attempted. |
| 6. Public release | Preparation in progress | MIT license, stable output publisher, README, cleanup plan, app assets, and release build exist. Installer/signing, current app-window verification, and verified live sync remain. |

## Work completed in this session

1. Confirmed the app's stored account and target settings, with automatic sync off.
2. Fixed and hardened the synthetic verifier. It checks candidate directories through GitHub metadata to avoid caching raw-file misses, uses the reserved synthetic fixture only when no indexed path is free, retries raw endpoint propagation, and cleans up only while the current remote JSON still equals the synthetic fixture.
3. Built the Release solution successfully with 0 warnings and 0 errors; all 50 functional checks passed.
4. Ran the real GitHub check successfully: account/scope/owner/repository/branch checks passed; commit `d5215a6` had exactly one expected synthetic path; GitHub and viewer raw endpoint readbacks matched the fixture; cleanup commit `3ec7f7d` removed the path.
5. Verified via the GitHub Contents API that the synthetic path is absent from the current `user` branch. The `raw.githubusercontent.com` CDN can retain a cached response temporarily; because the fallback ID is not in the viewer's default-branch index, it is not requested or displayed by the viewer's game list.
6. Updated README guidance and moved the synthetic fixture directory to match the reserved test ID. The stable app executable was not rebuilt or replaced.

## Exact next steps

1. Continue UI-level scan and game-detail verification when desktop control is available. The earlier desktop-control gate rejected automated window launch; do not work around it.
2. Add representative emulator fixtures and expand parser coverage without changing already working parsers unnecessarily.
3. Continue public-release preparation (installer/signing and UI validation). The live synthetic GitHub upload/readback path is verified; no local achievement profiles have been synchronized.
4. Review `docs/CLEANUP_PLAN.md` before archiving ignored build artifacts. Do not remove user files or achievement data.
