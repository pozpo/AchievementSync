# Architecture and milestone status

## Resume point

The last completed work in this session added parsing for the viewer's `game-info.json`, a local `AppID/<appid>/achievements.json` preview, visible recent unlocks, and a tested self-contained x64 Windows build. The preview does not upload or write data. The next implementation task is GitHub device-flow sign-in, then selecting a user's fork and implementing a remote merge/preview/explicit-confirmation workflow. A publisher-owned GitHub OAuth App must be registered with Device Flow enabled and its public Client ID supplied before the built-in login can be enabled. GitHub's device flow uses the registered Client ID and avoids a distributed Client Secret; see [GitHub's OAuth authorization guide](https://docs.github.com/en/apps/oauth-apps/building-oauth-apps/authorizing-oauth-apps) and [OAuth app setup](https://docs.github.com/en/apps/oauth-apps/building-oauth-apps/creating-an-oauth-app).

The workspace root `C:\AchievementSync` currently has no `.git` directory, so Git status and commit history are unavailable. The nested `achievement-viewer\.git` metadata did not resolve as a repository in Git commands; do not assume the source project has a valid commit baseline. The original PowerShell script and viewer data remain separate workspace inputs and are excluded from the public app source.

## Framework choice

AchievementSync uses .NET 10 with WPF. It is a Windows-only desktop app, so WPF provides a native Windows windowing stack and accessible controls without shipping a browser engine. `AchievementSync.Core` contains scanning, parsing, merging, metadata response parsing, and settings models. `AchievementSync.Desktop` contains the WPF UI, local metadata cache, and logging. This keeps parsers testable without starting a window and avoids third-party runtime packages.

## Data flow

1. The UI loads saved location preferences and scans enabled folders asynchronously.
2. The scanner searches only known filenames/extensions, skips reparse points, handles inaccessible paths, and caps traversal work.
3. Parsers normalize data into achievement records. Catalog metadata and player progress are separate facts; an absent state remains unknown. The legacy viewer's `game-info.json` catalog can be joined to `achievements.json` progress.
4. The merger groups by numeric AppID and unions achievement keys. A true unlock from any valid source is retained; the newest valid unlock timestamp wins.
5. Public Steam Store metadata is fetched separately using the AppID and cached in the user's local app-data folder. Steam metadata does not supply local unlock state.
6. No synchronization component is currently connected to the UI. Scanning and metadata lookup do not upload achievement data.

## Privacy and safety boundaries

- Local profile files are read only.
- AppID is the only local profile data sent to Steam for the current name/art lookup.
- Steam artwork is accepted only from HTTPS Steam-hosted CDN domains and cached locally.
- No GitHub credential is built into the app. The legacy script is not used by the GUI.
- Existing achievement files are not deleted or overwritten.

## Current support limits

- JSON progress objects and JSON-array database files support common earned/achieved/unlocked state fields.
- Metadata-only JSON catalogs provide names, descriptions, icons, hidden flags, and totals; they do not provide player unlock state.
- Section-based `achievements.ini` and `achiev.ini` parsing is partial and does not claim a full catalog.
- Folder names are discoverable independently of parser support. CODEX, RUNE, EMPRESS, OnlineFix, SmartSteamEmu, SKIDROW, and Ubisoft/Uplay folders may contain formats that still need fixtures and dedicated adapters.
- Steam's documented `GetSchemaForGame` Web API requires a Steamworks API key. No key is embedded, and the app does not claim to retrieve a complete official achievement catalog for every game.

## Milestones

| Milestone | Status | Notes |
|---|---|---|
| 1. Inspection and architecture | Complete | Archive findings and the framework choice are recorded in this folder. |
| 2. Desktop foundation | Complete for preview | Dark dashboard, recent unlocks, location settings, recursive read-only scan, custom folders, manual scan, details window, and logs are implemented. |
| 3. Achievement processing | Initial adapters complete | JSON progress, viewer `.db`, `game-info.json` catalogs, metadata catalog arrays, and partial INI profiles are normalized and merged. System-specific fixtures are still needed before claiming full support for every listed source. |
| 4. Metadata and artwork | Initial integration complete | Steam Store name/header image lookup, local cache, fallback, manual display-name override, and achievement icon display are implemented. Offline cache and CDN behavior depend on network and filesystem access. |
| 5. GitHub synchronization | Local preview implemented; remote sync not started | Activity builds and displays viewer-compatible JSON files locally, excluding unknown unlock states. A public OAuth application Client ID must be registered by the publisher before implementing account/repository selection, remote union, explicit confirmation, content-only writes, retry/history, and token protection. No upload is possible in this preview. |
| 6. Public release | Preview executable verified | Source documentation, privacy-safe screenshot, MIT license, and a Windows publish script are present. A self-contained x64 single-file executable builds and launches. It is not an installer; release signing, installer packaging, and OAuth-backed sync remain. |

## Before enabling GitHub sync

The app must connect to a user's own fork, read the target branch before proposing writes, union local known states with remote achievement JSON, and show the exact paths and JSON changes before any commit. It must use a least-privilege authorization flow, protect tokens with Windows credential storage, and never stage or reset unrelated repository files. Auto-sync must remain opt-in. The GitHub app registration and exact content scope are external release inputs; until then, upload code stays disabled.
