# AchievementSync

AchievementSync is a Windows desktop app for finding local game achievement profiles and viewing their progress. The current 0.1.1 preview scans read-only, resolves public Steam game names and cover images when available, and keeps metadata cached on the PC.

**This is an early preview. A local viewer-compatible file preview is implemented; GitHub sign-in, remote upload, automatic sync, and an installer are not. Nothing is uploaded.**

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

Create a self-contained x64 Windows executable with:

```powershell
./scripts/publish-windows.ps1
```

The single-file executable is written to `dist/AchievementSync-0.1.1-win-x64/AchievementSync.Desktop.exe`. The `dist` folder is ignored by Git; attach the executable to a tagged GitHub release when publishing. This is a portable executable, not an installer.

There is no in-app updater yet. Users should download the latest executable from the repository's Releases page, close AchievementSync, and replace the previous portable executable with the new one.

## Scanned locations and supported data

The default location list covers CODEX, RUNE, OnlineFix, EMPRESS, Goldberg SteamEmu and UplayEmu, R1 UplayEmu, GSE, Nemirtingas Epic Emu, anadius Achievement Watcher, SmartSteamEmu, SKIDROW, UniverseLAN, Achievement Watcher caches, CreamAPI, and Steam ProgramData. Missing folders are safe to ignore. Custom roots are stored in `%LocalAppData%\AchievementSync\settings.json`.

File discovery looks for `achievements.json`, `achievements.ini`, `achiev.ini`, and `.db` files. Supported formats vary by source. A location being scanned does not mean every file under it has a supported achievement format. INI files currently provide partial progress, not a full achievement catalog. The archive review in [docs/ARCHIVE_REVIEW.md](docs/ARCHIVE_REVIEW.md) records which supplied formats were directly reusable.

## Privacy and network access

Local achievement files are read only. Their contents, paths, unlock dates, and identifiers are not sent to Steam. For each discovered numeric AppID, the app may request public game name and header-art metadata from the Steam Store and fetch the returned Steam-hosted image. Those results are cached under `%LocalAppData%\AchievementSync\Metadata`. If Steam is unavailable, the app keeps the AppID fallback.

The current preview has no GitHub authentication or upload code. The Activity page builds local JSON previews only; the local library remains on the PC.

## License and third-party content

The new AchievementSync application code is licensed under MIT; see [LICENSE](LICENSE). .NET is a Microsoft dependency. Steam names and artwork are fetched at runtime and are not bundled. No emulator executables, DLLs, patches, or supplied third-party binaries are distributed.

The supplied archives did not provide enough license provenance to safely redistribute their bundled tools. They were inspected for format and workflow information only. See [docs/ARCHIVE_REVIEW.md](docs/ARCHIVE_REVIEW.md) for details.

## Release status

The 0.1.0 preview can be packaged as a self-contained x64 Windows executable. It is not an installer. See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the milestone status and the GitHub OAuth app registration required before a public user can connect a fork securely. Account/repository selection, remote sync history, and end-to-end release verification remain before a public sync-enabled release.
