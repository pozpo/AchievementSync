# Architecture

## Framework and project layout

AchievementSync is a Windows-only .NET 10 WPF application. WPF uses the native Windows desktop stack and avoids shipping a browser engine. The app has one stable desktop project, one UI-independent core library, and one functional-check project with no direct NuGet references.

The desktop project references `System.Management` 10.0.0 for filtered Windows process-start/stop events and enables the built-in Windows Forms `NotifyIcon` for its tray menu. There is no separate tray package and no Windows App SDK dependency. Windows App SDK notifications were evaluated; for this unpackaged single-file WPF app, their bootstrapper/framework runtime and registration requirements are disproportionate. Unlock notices use a small WPF popup instead, so they do not appear in Action Center history. See Microsoft's [unpackaged Windows App SDK deployment guidance](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/deploy-unpackaged-apps) and [`AppNotificationManager.Register` requirements](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.windows.appnotifications.appnotificationmanager.register?view=windows-app-sdk-2.0).

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

## Playtime and background monitoring

Executable associations and playtime sessions are stored in the app-managed `%LocalAppData%\AchievementSync\playtime.json`. Only AppIDs currently present in the Game Library are eligible for tracking. The user can link the game's actual executable through the Windows file picker; process matches require the exact executable path. There is no automatic Steam-manifest-to-executable mapping yet, because an installation directory does not reliably distinguish a game executable from launchers and helpers.

`WindowsGameProcessMonitor` subscribes to `Win32_ProcessStartTrace` and `Win32_ProcessStopTrace` events filtered to linked executable names. It verifies each observed full path before recording a session; protected processes whose path is unreadable for OS reasons are matched by process name instead, and a single log line notes the first use of the fallback. Where WMI subscriptions are unavailable (this environment denies WMI access), it reconciles only linked executable names every 30 seconds and still verifies full paths when they are readable. Active sessions are checkpointed and recovered without double counting.

Closing the main window hides it while the `NotifyIcon` menu provides Open, Pause/Resume Monitoring, and Exit. Exit stops watchers and process monitoring, checkpoints sessions, disposes the tray icon, and shuts down. Optional Windows startup writes the current executable to the current user's Run key and is disabled by default.

## Achievement unlock notifications

After a normal library scan, `LocalAchievementFileMonitor` watches only directories containing already-discovered achievement source files and processes only those exact known paths. File changes are debounced; rename/replacement events are handled, and watcher overflow triggers reconciliation of known files rather than a recursive scan. Parsing is asynchronous from the UI and retries temporarily locked files.

`AchievementUnlockTracker` stores known/unknown states and baselined AppIDs atomically in `%LocalAppData%\AchievementSync\unlock-state.json`. It notifies only on a confirmed false-to-true transition, and only after the resulting state is durably saved. Existing unlocks, unknown-to-true transitions, duplicate events and repeated observations after restart do not notify. The Test notification setting displays a local-only preview and does not modify achievement state.

`achievement-viewer/` is a local data checkout, not a project. It is ignored, is not a valid nested Git working tree in this workspace, and must not be staged or altered by app development. The AchievementSync source remote is separate from the user's achievement data remote.

## Data flow

1. The UI loads scan-location preferences and reads only known achievement filenames and supported extensions.
2. The scanner walks enabled paths, skips reparse points, records missing or inaccessible locations, and passes candidate files to isolated JSON or INI parsers.
3. Parser output normalizes into `AchievementRecord` and `ParsedProfile`. Catalog metadata and player unlock state remain separate; unknown is not treated as locked.
4. The profile merger groups records by numeric AppID and unions identifiers while preserving confirmed unlocks.
5. For profiles whose local progress is incomplete, a configured public viewer fork may supply generated `game-info.json` catalog metadata and its per-AppID `achievements.json` progress. The desktop applies the viewer's documented missing-entry-is-locked rule only to that viewer progress file; absent entries in arbitrary local partial formats remain unknown. Confirmed unlocks are preserved when sources merge.
6. Steam Store metadata uses only AppID and is cached locally. Achievement state is never sent to Steam.
7. The export planner produces viewer-compatible JSON. The GitHub client reads only the exact remote AppID paths, validates and unions achievement states, displays a preview, then creates a non-forced content-only commit containing only reviewed JSON paths.

## Authentication and token handling

Device Flow uses `POST https://github.com/login/device/code` and `POST https://github.com/login/oauth/access_token`, requests the `public_repo` scope, respects the server polling interval and `slow_down`, and handles denial and device-code expiry. If the OAuth App returns expiring credentials, the app rotates the access and refresh tokens using the OAuth refresh grant. Tokens are held in the current Windows user's Credential Manager, not app settings or logs. Older access-token-only Credential Manager entries are read for compatibility and are replaced after a successful fresh sign-in.

After sign-in, `/user` identifies the account and the granted `X-OAuth-Scopes` header is checked for `public_repo`. A configured owner must match that login. Repository metadata must identify the requested public repository and report push access. The configured branch is resolved through GitHub's ref API before any preview. A 401 clears the saved credential and requires sign-in again.

## Privacy and synchronization boundaries

- Scanning reads local profiles and never edits or deletes them.
- A local scan can issue unauthenticated read-only GET requests to the configured public fork's exact `game-info.json` and `achievements.json` paths for incomplete profiles. These requests include AppID/repository coordinates and never include local achievement data. Local export preview makes no GitHub request.
- Reading public viewer data is separate from synchronization: scans never upload, change branches, create commits, or write fetched progress back.
- Upload targets are limited to `AppID/<numeric id>/achievements.json`.
- No entire save folder, credentials, local paths, or unrelated repository files are uploaded.
- Remote confirmed unlocks are preserved. Invalid JSON, changed branch heads, unknown path shapes, and inaccessible repositories stop the preview or push safely.
- Automatic sync is disabled by default and needs explicit opt-in. The first automatic upload still requires per-preview confirmation.

## Supported formats and limits

JSON achievement progress and catalogs, JSON-array `.db` profiles, viewer `game-info.json`, Goldberg-style catalog data, and partial INI progress are normalized. Format support is adapter-specific; a detected folder does not imply every profile there is supported. See [the archive review](ARCHIVE_REVIEW.md) for the inspected tool archives and provenance limits.

Steam Store metadata may not provide achievement catalogs. A complete official Steam achievement schema requires a Steamworks API key, which is not embedded. The app uses viewer-generated catalogs and saved progress only when a public viewer fork is configured; local incomplete formats otherwise retain unknown states and do not claim measurable completion.

## Build identity

The existing desktop project stays the only app. Its stable executable identity is `AchievementSync.exe`, published to `C:\AchievementSync\output\AchievementSync.exe`. The publisher script first builds and runs functional checks, publishes to `output/.staging`, and replaces the existing executable only after those steps succeed. Assembly and file versions remain unchanged during normal development; only a requested public release changes them.

The existing MIT license applies to the app source and documentation. The emulator/tool archives and artwork are not redistributed. Runtime Steam art is fetched from Steam and cached by the app.
