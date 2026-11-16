# Development status

Updated: 2026-11-16

## Current state

- Working directory: `C:\AchievementSync`, existing `main` branch. The working tree was clean before this session's edits.
- `origin` is already configured as `https://github.com/pozpo/AchievementSync.git`. The remote's initial README and MIT license were inspected; no changes have been pushed. Local and remote histories are unrelated, so they need an additive merge before publication.
- `achievement-viewer/` contains 795 files and a nested `.git` directory whose `HEAD` is invalid. Git resolves the folder to the parent app repository. The folder is ignored and has not been changed. Public GitHub confirms `pozpo/achievement-viewer` is a public fork owned by `pozpo`; branch `user` exists at `e14739c20f3ea878faafa9ad11056e247cbe5d88`, with connector-confirmed push/admin permission.
- The existing GitHub connector reports the authenticated login as `pozpo`. This is not AchievementSync OAuth Device Flow authentication and does not verify the app's OAuth client.
- `C:\AchievementSync\output\AchievementSync.exe` is now the sole active build target. It is self-contained x64, has the public OAuth Client ID embedded, reports stable file version `0.1.2.0` and product version `0.1.2-preview`, and the output staging folder is clean.
- The publisher's Release build passed with 0 warnings and 0 errors; all 48 functional checks passed. The existing app was not launched because the Computer Use gate rejected the app launch (`Computer Use was not approved to use achievementsync`). Do not work around that permission block.
- Local user settings are configured for `pozpo/achievement-viewer` on `user`, with automatic sync off. No achievement data has been uploaded.
- Three ignored historical executables remain under `dist`; user data and legacy inputs remain untouched. See [cleanup plan](CLEANUP_PLAN.md).

## Milestones

| Milestone | Status | Evidence / remaining work |
|---|---|---|
| 1. Inspection and architecture | Complete | Supplied archive review and architecture guidance are in `docs/guides/`. |
| 2. Desktop foundation | Complete for preview | Existing WPF dashboard, scan-location controls, details, metadata cache, and logging. Build is verified; window-level verification is blocked by the desktop control permission. |
| 3. Achievement processing | Initial adapters complete | JSON, `.db`, viewer catalog, Goldberg-style catalog, and partial INI support. Per-emulator fixture coverage remains incomplete. |
| 4. Metadata and artwork | Initial integration complete | Steam Store name/header art caching and achievement icon display. Metadata coverage depends on public data and network access. |
| 5. GitHub synchronization | Code implemented; live verification pending | Device Flow, Credential Manager, owner/public-repository checks, exact-path preview, and non-forced content-only commits exist. This session added refresh rotation, the documented `token_expired` response, granted-scope checks, and clearer revoked-token handling. Requires the user to complete device authorization, followed by authenticated account/repository/branch checks and a synthetic-only upload/readback. |
| 6. Public release | Preparation in progress | MIT source license, privacy-safe screenshot, preserved app artwork, stable output publisher, and docs are present. Installer, signing, app-window verification, and live end-to-end sync remain. |

## This session

1. Rechecked Git status/history/remotes, tracked and ignored files, source inventory, viewer metadata, output artifacts, README, architecture notes, and current build setup before moving project files.
2. Confirmed the baseline solution build and existing 41 functional checks pass.
3. Moved the existing ICO/PNG assets to root `assets/`, moved guides to `docs/guides/`, added the cleanup plan and this status document, and configured the stable output path. The viewer checkout and ignored legacy/user files were left in place.
4. Hardened GitHub OAuth: parse expiry/rotation fields, refresh expiring tokens, honor `slow_down`, handle both device expiry spellings, verify `public_repo`, and clear rejected credentials after HTTP 401.
5. Updated the existing publisher to build and run functional checks in Release, publish to a temporary staging folder, and atomically replace only `output/AchievementSync.exe` after success. Assembly/file metadata remain unchanged.
6. Added synthetic OAuth regression checks and a clearly labelled synthetic fixture. Latest checks: 48 passed; Release publish succeeded; the OAuth Client ID is embedded in the executable.
7. Set local app target to `pozpo/achievement-viewer` branch `user` with auto-sync off. Attempted WPF launch through Computer Use, but its security gate blocked that action; no app UI or user authentication was automated.

## Exact next tasks

1. User handoff: manually open `C:\AchievementSync\output\AchievementSync.exe`, click GitHub sign-in, and complete the one-time GitHub authorization in their browser. Keep automatic sync off and do not share the code or token in chat. This is required because the Computer Use gate rejected launching the app from this session.
2. After authorization, verify the account login and `public_repo` grant with the app's token, then preview the configured public fork and confirm that the `user` branch resolves. Do not preview locally discovered real profiles.
3. Use only `tests/Fixtures/GitHubSynthetic/AppID/9999999999/achievements.json` for the live upload test. First verify that exact remote path is absent, then preview/push it, record the resulting commit, and verify both GitHub and website readback. Remove only that confirmed synthetic file after verification.
4. Merge the existing source remote's README/LICENSE history into local `main` without losing the inspected remote content; inspect the exact staged paths and commit, then push the source repository.
5. Resume app-window verification if the desktop-control permission becomes available. Do not report real end-to-end synchronization until the actual test commit and readback are confirmed.
6. Revisit `docs/CLEANUP_PLAN.md`; preserve ignored builds and user data until each item is verified safe to archive or remove.
