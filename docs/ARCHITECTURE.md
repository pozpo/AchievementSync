# Architecture and milestone status

## Resume point

This continuation added the supplied logo as a multi-size Windows `.ico`, plus GitHub Device Flow, Windows Credential Manager storage, remote preview/merge, and guarded push. Remote behavior is tested against synthetic HTTP responses; no real account or repository was accessed. A publisher-owned GitHub OAuth App still must be registered with Device Flow enabled and its public Client ID supplied to a release build before sign-in is available. GitHub's device flow uses the registered Client ID and avoids a distributed Client Secret; see [GitHub's OAuth authorization guide](https://docs.github.com/en/apps/oauth-apps/building-oauth-apps/authorizing-oauth-apps) and [OAuth app setup](https://docs.github.com/en/apps/oauth-apps/building-oauth-apps/creating-an-oauth-app).

**Next task:** configure the publisher OAuth App Client ID in a release build and complete live sign-in/repository verification with a dedicated test fork. Then add durable sync history, optional interval scheduling, and an installer. Do not use the supplied local viewer data for sync testing.

At the start of this recovery the workspace root had no `.git` history. A curated local `main` repository is now initialized with baseline commit `dfb2a01`; the working tree was clean after that commit and no remote is configured. The nested `achievement-viewer\.git` metadata did not resolve as a repository in Git commands. The original PowerShell script and viewer data remain separate workspace inputs and are excluded from the public app source.

## Framework choice

AchievementSync uses .NET 10 with WPF. It is a Windows-only desktop app, so WPF provides a native Windows windowing stack and accessible controls without shipping a browser engine. `AchievementSync.Core` contains scanning, parsing, merging, metadata response parsing, and settings models. `AchievementSync.Desktop` contains the WPF UI, local metadata cache, and logging. This keeps parsers testable without starting a window and avoids third-party runtime packages.

## Data flow

1. The UI loads saved location preferences and scans enabled folders asynchronously.
2. The scanner searches only known filenames/extensions, skips reparse points, handles inaccessible paths, and caps traversal work.
3. Parsers normalize data into achievement records. Catalog metadata and player progress are separate facts; an absent state remains unknown. The legacy viewer's `game-info.json` catalog can be joined to `achievements.json` progress.
4. The merger groups by numeric AppID and unions achievement keys. A true unlock from any valid source is retained; the newest valid unlock timestamp wins.
5. Public Steam Store metadata is fetched separately using the AppID and cached in the user's local app-data folder. Steam metadata does not supply local unlock state.
6. GitHub sync uses Device Flow and stores tokens in Windows Credential Manager. The user selects a public repository owned by the connected account and a branch. The app reads matching AppID achievement JSON, unions local and remote states, shows exact proposed content, then requires separate confirmation before a non-forced GitHub Git Data API commit. Invalid remote JSON or a changed branch stops the push.

## Privacy and safety boundaries

- Local profile files are read only.
- AppID is the only local profile data sent to Steam for the current name/art lookup.
- Steam artwork is accepted only from HTTPS Steam-hosted CDN domains and cached locally.
- No GitHub credential or OAuth Client Secret is built into the app. A publisher may supply the public Client ID at release build time. Access tokens are stored in Windows Credential Manager, not settings or logs.
- GitHub uploads are opt-in, restricted to a user's public repository and `AppID/<numeric id>/achievements.json`, and require a preview plus explicit confirmation. Achievement IDs, earned states, and available unlock times are sent.
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
| 5. GitHub synchronization | Manual and opt-in after-scan sync implemented; external setup and workflow remain | Device Flow, secure token storage, account-owned public repository/branch configuration, remote merge preview, path-restricted Git Data API commit, explicit first-upload confirmation, opt-in after-scan pushes, and stale-branch rejection are implemented. Synthetic checks cover polling, merge, preview, push, and stale previews. The publisher OAuth Client ID is not yet supplied; private repos, local checkout mirroring, interval scheduling, and durable history remain. |
| 6. Public release | 0.1.2 portable executable verified | MIT source license, privacy-safe screenshot, documentation, supplied logo, and self-contained x64 publish script are present. The executable builds and launches with an embedded multi-size icon. This is not an installer. Public OAuth registration/client ID, signed release, installer, and live end-to-end verification remain. |

## Before enabling GitHub sync

The app connects to an account-owned public repository through the GitHub API, reads a fixed base commit, unions local known states with remote achievement JSON, and shows exact paths and JSON changes before a commit. It requests `public_repo`, protects tokens with Windows Credential Manager, and never stages, resets, or deletes unrelated files. Manual and first-time automatic pushes require confirmation; users can explicitly opt into later pushes after each scan. The publisher's OAuth App registration and public Client ID remain external release inputs, so sign-in is disabled in builds without that ID.
