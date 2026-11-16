# AchievementSync

AchievementSync is a Windows desktop app for finding local game achievement profiles, viewing progress, and optionally syncing viewer-compatible achievement data to a public GitHub repository. Version 0.1.2 adds GitHub Device Flow sign-in, exact remote previews, and explicit content-only pushes. Local scans remain read-only.

**This remains a preview release. GitHub sync requires a publisher-configured OAuth App Client ID and an account-owned public repository. Scanning and previewing never upload data. Manual pushes and the first automatic push require separate confirmation; later automatic pushes are available only after the user deliberately enables them. Durable sync history and an installer are not implemented.**

![AchievementSync sync activity page](docs/screenshots/sync-activity.jpg)

The screenshot shows the empty sync activity page and contains no local game or achievement data.

## What works now

- Recursively scans the configured locations and reports missing folders as unavailable.
- Lets users add, remove, enable, and disable custom scan folders.
- Reads JSON progress objects, JSON-array `.db` profiles, Goldberg-style achievement catalogs, `achievements.ini`, and `achiev.ini` progress sections.
- Reads the viewer's `game-info.json` catalog alongside `achievements.json` progress files.
- Merges profiles by numeric Steam AppID and unions achievements, preserving a known unlock if another source is older or incomplete.
- Marks unknown unlock states as unknown. It does not turn an incomplete file into a fabricated locked state or completion percentage.
- Looks up game names and Steam Store header artwork using only the AppID; caches successful and unsuccessful lookups locally.
- Lets users assign a display name manually when lookup fails.
- Builds a local-only preview of `AppID/<appid>/achievements.json`; unknown unlock states are omitted.
- Supports GitHub Device Flow when the publisher's public OAuth Client ID is configured. Tokens are kept in the current Windows user's Credential Manager, never in settings or logs.
- Verifies a public repository owned by the signed-in account, merges remote and local achievement states, and displays exact changed JSON files before a separately confirmed push.
- Pushes one content-only GitHub commit containing only changed `AppID/<numeric id>/achievements.json` files. Remote unlocks are unioned and retained; invalid JSON or a changed branch stops safely.
- Offers opt-in automatic sync after scans (including the startup scan). Enabling it requires explicit confirmation; the first automatic push still presents the full preview and asks for approval.
- Keeps original local profiles unchanged and writes logs under `%LocalAppData%\AchievementSync\Logs` when the current account permits it.

## Build and run from source

Requirements: Windows 10 or 11, the .NET 10 SDK, and the .NET 10 Desktop Runtime. The solution uses WPF and has no third-party NuGet packages.

```powershell
dotnet build AchievementSync.sln -m:1
dotnet run --project src/AchievementSync.Desktop/AchievementSync.Desktop.csproj
```

Run the package-free functional checks with:

```powershell
dotnet run --project tests/AchievementSync.SmokeTests/AchievementSync.SmokeTests.csproj
```

The Windows desktop project is `src/AchievementSync.Desktop`; UI-independent parsing and scanning code is in `src/AchievementSync.Core`.

Create a self-contained x64 Windows executable with the supplied AchievementSync logo embedded as its Windows application icon:

```powershell
./scripts/publish-windows.ps1
```

The single-file executable is written to `dist/AchievementSync-0.1.2-win-x64/AchievementSync.Desktop.exe`. The `dist` folder is ignored by Git. This is a portable executable, not an installer.

### Configure GitHub sign-in for a public release

The publisher must register a GitHub OAuth App, enable Device Flow, and provide its public **Client ID** when building a release. Do not create or distribute a Client Secret. Set `ACHIEVEMENTSYNC_GITHUB_CLIENT_ID` in the release build environment and run the publish script, or pass `-GitHubOAuthClientId` to it. If the ID is absent, sign-in is disabled while local scanning and preview continue to work. The checked-in preview has no publisher OAuth App Client ID, so sign-in is not enabled in that build.

Sync supports public repositories only and requires the repository owner to match the signed-in account. AchievementSync writes directly to the selected branch through GitHub's Git Data API; no local repository checkout or Git installation is needed. Automatic sync runs only after a completed scan and after the user opts in; interval-based scheduling, durable sync history, and local repository mirroring remain future work. OAuth scope is limited to `public_repo`.

There is no in-app updater yet. Users should download the latest executable from the repository's Releases page, close AchievementSync, and replace the previous portable executable with the new one.

## Scanned locations and supported data

The default location list covers CODEX, RUNE, OnlineFix, EMPRESS, Goldberg SteamEmu and UplayEmu, R1 UplayEmu, GSE, Nemirtingas Epic Emu, anadius Achievement Watcher, SmartSteamEmu, SKIDROW, UniverseLAN, Achievement Watcher caches, CreamAPI, and Steam ProgramData. Missing folders are safe to ignore. Custom roots are stored in `%LocalAppData%\AchievementSync\settings.json`.

File discovery looks for `achievements.json`, `achievements.ini`, `achiev.ini`, and `.db` files. Supported formats vary by source. A location being scanned does not mean every file under it has a supported achievement format. INI files currently provide partial progress, not a full achievement catalog. The archive review in [docs/ARCHIVE_REVIEW.md](docs/ARCHIVE_REVIEW.md) records which supplied formats were directly reusable.

## Privacy and network access

Local achievement files are read only. Their contents, paths, unlock dates, and identifiers are not sent to Steam. For each discovered numeric AppID, the app may request public game name and header-art metadata from the Steam Store and fetch the returned Steam-hosted image. Those results are cached under `%LocalAppData%\AchievementSync\Metadata`. If Steam is unavailable, the app keeps the AppID fallback.

Scanning and building a local preview never contact GitHub. On sign-in, the app uses GitHub Device Flow and stores the token only in Windows Credential Manager. Manual pushes and the first automatic push display every exact destination path and merged JSON, then ask for separate confirmation. After a user explicitly enables automatic sync, later changes may be pushed after a scan without another prompt; the preview remains visible in Sync activity. Only achievement JSON is sent to the selected public repository. Achievement IDs, earned states, and available unlock timestamps become visible to its viewers. Original save files, local paths, emulator configuration, and credentials are never sent. Branch updates are non-forced; stale previews must be rebuilt.

## License and third-party content

The new AchievementSync application code is licensed under MIT; see [LICENSE](LICENSE). .NET is a Microsoft dependency. Steam names and artwork are fetched at runtime and are not bundled. No emulator executables, DLLs, patches, or supplied third-party binaries are distributed.

The supplied archives did not provide enough license provenance to safely redistribute their bundled tools. They were inspected for format and workflow information only. See [docs/ARCHIVE_REVIEW.md](docs/ARCHIVE_REVIEW.md) for details.

## Release status

The 0.1.2 preview can be packaged as a self-contained x64 Windows executable with the supplied logo embedded. It is not an installer. See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for milestone status, release setup, and remaining limitations. No real account or repository has been used in sync tests; behavior is tested against synthetic HTTP responses.
