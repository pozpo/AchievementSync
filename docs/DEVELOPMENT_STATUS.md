# Development status

Updated: 2026-11-16

## Current state

- This is the existing AchievementSync project in `C:\AchievementSync`; no second app or versioned output was created.
- The source remote is `https://github.com/pozpo/AchievementSync.git`. The original remote README and MIT license history were merged without force. The tested synthetic verification harness and its documentation were pushed non-forced to the same `main` branch in commit `7cf47d1c725a84c0609e5ce9c9a59d57e2bea9ee`; GitHub's branch ref was checked against that commit.
- The viewer data repository is separate. Public GitHub confirms `pozpo/achievement-viewer` is owned by `pozpo`, is a fork, and has a writable `user` branch. The local `achievement-viewer/` folder has invalid Git metadata and remains ignored and untouched.
- The existing executable remains at `C:\AchievementSync\output\AchievementSync.exe`; its application identity, icon, and stable `0.1.2.0` file version were preserved. The previous verified Release publish embedded the supplied public OAuth Client ID. This session changed only the smoke-test project and documentation, so the executable was not republished.
- Local app settings point to `pozpo/achievement-viewer`, branch `user`; automatic sync is off and first-upload authorization is false. Windows Credential Manager currently has no `AchievementSync/GitHub/OAuthToken` credential.
- No local achievement profile data was uploaded. No live GitHub commit was made.
- Three ignored historical EXEs under `dist`, user files, legacy scripts, and the local viewer folder remain untouched; see [the cleanup plan](CLEANUP_PLAN.md).

## Milestones

| Milestone | Status | Evidence / remaining work |
|---|---|---|
| 1. Inspection and architecture | Complete | Supplied archives reviewed; findings and architecture are in `docs/guides/`. |
| 2. Desktop foundation | Implemented; window check pending | Existing WPF dashboard, scan controls, details, metadata cache, and logging build. The app window was not launched because Computer Use returned `Computer Use was not approved to use achievementsync`. Do not work around this desktop permission block. |
| 3. Achievement processing | Initial adapters complete | JSON, `.db`, Goldberg-style catalog, viewer catalog, and partial INI support; emulator-specific fixture coverage remains incomplete. |
| 4. Metadata and artwork | Initial integration complete | Steam Store names/header artwork cache and achievement icons; metadata availability depends on public data/network access. |
| 5. GitHub synchronization | Implemented; live verification blocked on app sign-in | Device Flow and refresh handling, Credential Manager storage, `public_repo` check, owner/repository/branch validation, exact-path preview, merge protection, and non-forced commit code are present. The one-shot synthetic live verifier now builds and fails closed if app authentication is missing. It has not uploaded anything. |
| 6. Public release | Preparation in progress | MIT license, stable output publisher, README, cleanup plan, app assets, and release build exist. Installer/signing, current app-window verification, and verified live sync remain. |

## Work completed in this session

1. Inspected the previously unfinished live-sync verifier and fixed its compile errors by adding local GitHub JSON response readers and properly handling the nullable remote-file result.
2. Built the full Release solution successfully: 0 warnings, 0 errors.
3. Ran all 50 functional checks successfully.
4. Ran the isolated Credential Manager test successfully; its synthetic test credential was removed.
5. Ran the opt-in live GitHub verifier. It stopped before making a network upload because the app's OAuth credential is absent: `Connect your GitHub account in AchievementSync before running the live check.`
6. Updated this status and the README with the exact live-test invocation and safeguards. No app executable or user achievement data was changed.

## Exact next steps

1. Manually open `C:\AchievementSync\output\AchievementSync.exe`, sign into GitHub through Device Flow, and confirm Settings shows the authenticated `pozpo` account and `pozpo/achievement-viewer` branch `user`. Keep automatic sync off. Do not share device codes or credentials in chat. This is required because the desktop-control permission gate rejected app launch from this session.
2. Once sign-in is complete, run the explicit synthetic-only live check from the README. It will choose a numeric AppID directory indexed by the viewer but missing an achievements file on `user`, preview exactly one synthetic file, verify the commit and website raw readback, then remove only that exact synthetic file. Review any cleanup error before proceeding.
3. Continue UI-level scan and game-detail verification when desktop control is available. Do not report end-to-end synchronization until a real synthetic test commit and readback have both been verified.
4. Review `docs/CLEANUP_PLAN.md` before archiving any ignored build artifacts. Do not remove user files or achievement data.
