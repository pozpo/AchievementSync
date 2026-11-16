# Architecture

## Framework and project layout

AchievementSync is a Windows-only .NET 10 WPF application. WPF uses the native Windows desktop stack and avoids shipping a browser engine. The app has one stable desktop project, one UI-independent core library, and one dependency-free functional-check project.

```text
assets/                         Shared Windows icon and source artwork
docs/guides/                    Architecture and supplied-archive review
docs/screenshots/               Privacy-safe app screenshots
output/                         The stable AchievementSync.exe destination
scripts/                        Build and artwork utilities
src/AchievementSync.Core/       Parsers, scanning, merge, settings, GitHub API
src/AchievementSync.Desktop/    WPF UI, metadata cache, Credential Manager, logs
tests/AchievementSync.SmokeTests/ Functional checks
tests/Fixtures/                 Synthetic test inputs only
AchievementSync.sln             The existing solution
```

`achievement-viewer/` is a local data checkout, not a project. It is ignored, is not a valid nested Git working tree in this workspace, and must not be staged or altered by app development. The AchievementSync source remote is separate from the user's achievement data remote.

## Data flow

1. The UI loads scan-location preferences and reads only known achievement filenames and supported extensions.
2. The scanner walks enabled paths, skips reparse points, records missing or inaccessible locations, and passes candidate files to isolated JSON or INI parsers.
3. Parser output normalizes into `AchievementRecord` and `ParsedProfile`. Catalog metadata and player unlock state remain separate; unknown is not treated as locked.
4. The profile merger groups records by numeric AppID and unions identifiers while preserving confirmed unlocks.
5. Steam Store metadata uses only AppID and is cached locally. Achievement state is never sent to Steam.
6. The export planner produces viewer-compatible JSON. The GitHub client reads only the exact remote AppID paths, validates and unions achievement states, displays a preview, then creates a non-forced content-only commit containing only reviewed JSON paths.

## Authentication and token handling

Device Flow uses `POST https://github.com/login/device/code` and `POST https://github.com/login/oauth/access_token`, requests the `public_repo` scope, respects the server polling interval and `slow_down`, and handles denial and device-code expiry. If the OAuth App returns expiring credentials, the app rotates the access and refresh tokens using the OAuth refresh grant. Tokens are held in the current Windows user's Credential Manager, not app settings or logs. Older access-token-only Credential Manager entries are read for compatibility and are replaced after a successful fresh sign-in.

After sign-in, `/user` identifies the account and the granted `X-OAuth-Scopes` header is checked for `public_repo`. A configured owner must match that login. Repository metadata must identify the requested public repository and report push access. The configured branch is resolved through GitHub's ref API before any preview. A 401 clears the saved credential and requires sign-in again.

## Privacy and synchronization boundaries

- Scanning reads local profiles and never edits or deletes them.
- No GitHub request occurs during a local scan or local export preview.
- Upload targets are limited to `AppID/<numeric id>/achievements.json`.
- No entire save folder, credentials, local paths, or unrelated repository files are uploaded.
- Remote confirmed unlocks are preserved. Invalid JSON, changed branch heads, unknown path shapes, and inaccessible repositories stop the preview or push safely.
- Automatic sync is disabled by default and needs explicit opt-in. The first automatic upload still requires per-preview confirmation.

## Supported formats and limits

JSON achievement progress and catalogs, JSON-array `.db` profiles, viewer `game-info.json`, Goldberg-style catalog data, and partial INI progress are normalized. Format support is adapter-specific; a detected folder does not imply every profile there is supported. See [the archive review](ARCHIVE_REVIEW.md) for the inspected tool archives and provenance limits.

Steam Store metadata may not provide achievement catalogs. A complete official Steam achievement schema requires a Steamworks API key, which is not embedded. The current app does not claim a complete catalog for every source.

## Build identity

The existing desktop project stays the only app. Its stable executable identity is `AchievementSync.exe`, published to `C:\AchievementSync\output\AchievementSync.exe`. The publisher script first builds and runs functional checks, publishes to `output/.staging`, and replaces the existing executable only after those steps succeed. Assembly and file versions remain unchanged during normal development; only a requested public release changes them.

The existing MIT license applies to the app source and documentation. The emulator/tool archives and artwork are not redistributed. Runtime Steam art is fetched from Steam and cached by the app.
