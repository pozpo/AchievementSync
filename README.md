# AchievementSync

Windows desktop achievement manager and GitHub synchronizer.

AchievementSync scans configured local folders for supported achievement profiles, displays progress, and can optionally sync a reviewed set of viewer-compatible JSON files to a public repository owned by the signed-in GitHub user. Local profile files are read only.

![AchievementSync sync activity page](docs/screenshots/sync-activity.jpg)

This reference screenshot is from an earlier preview and contains no local game or achievement data. It will be refreshed after the current desktop window can be inspected.

## Current capabilities

- Recursively scans known and custom folders; missing folders appear as unavailable and do not stop a scan.
- Reads supported JSON and database profiles, Goldberg-style catalogs, viewer `game-info.json`, and partial INI progress.
- Merges duplicate profiles by numeric AppID while preserving confirmed unlocks and unknown states.
- Looks up Steam Store game names and header artwork by AppID, caches results, and supports manual title overrides.
- Shows dashboard progress, recent unlocks, game details, achievement descriptions, and known timestamps.
- Previews compatible `AppID/<appid>/achievements.json` exports without uploading.
- Uses GitHub OAuth Device Flow; tokens and refresh tokens are kept in the current Windows user's Credential Manager.
- Confirms the signed-in account, checks that the selected repository is public and pushable, validates the branch, and shows exact JSON changes before upload.
- Commits only reviewed `AppID/<numeric id>/achievements.json` files. Existing remote unlocks are retained, and branch updates are non-forced.
- Keeps automatic sync disabled until the user explicitly enables it. The first automatic upload still requires a separate preview and confirmation.

Some formats are only partially supported. A scan location being listed does not mean every format used there is understood. See [the archive review](docs/guides/ARCHIVE_REVIEW.md) and [the architecture guide](docs/guides/ARCHITECTURE.md).

## Install and update

The current portable Windows x64 build is `output/AchievementSync.exe`. It is self-contained and embeds the AchievementSync application icon. Close the app before replacing it with a later manually downloaded release. There is currently no installer or in-app updater.

Development builds always target the same path, `C:\AchievementSync\output\AchievementSync.exe`. The publisher builds and checks a temporary staged executable before atomically replacing the existing file. Assembly and file version metadata stay at their existing values during ordinary development; public release versions change only for an explicitly requested release. Older ignored builds under `dist` are being kept intact pending cleanup review.

## Build from source

Requirements: Windows 10 or 11 and the .NET 10 SDK. WPF and the package-free functional checks use no third-party NuGet packages.

```powershell
dotnet build AchievementSync.sln -m:1
dotnet run --project tests/AchievementSync.SmokeTests/AchievementSync.SmokeTests.csproj
dotnet run --project src/AchievementSync.Desktop/AchievementSync.Desktop.csproj
```

Check the Windows Credential Manager implementation using an isolated synthetic target (the test entry is removed afterward):

```powershell
dotnet build AchievementSync.sln --configuration Release -m:1
./scripts/verify-credential-store.ps1
```

Create the self-contained executable after the build and functional checks pass:

```powershell
$env:ACHIEVEMENTSYNC_GITHUB_CLIENT_ID = '<your public GitHub OAuth App Client ID>'
./scripts/publish-windows.ps1
```

The script stages the publish under `output/.staging`, then updates only `output/AchievementSync.exe`. It leaves the old executable in place if build, checks, or publish fail. Do not set a Client Secret or access token; Device Flow does not require a distributed client secret.

The project layout is described in [docs/guides/ARCHITECTURE.md](docs/guides/ARCHITECTURE.md). Current progress and the exact next task are tracked in [docs/DEVELOPMENT_STATUS.md](docs/DEVELOPMENT_STATUS.md). Potential artifact cleanup is listed in [docs/CLEANUP_PLAN.md](docs/CLEANUP_PLAN.md).

## GitHub setup and privacy

The app repository is [pozpo/AchievementSync](https://github.com/pozpo/AchievementSync). This is separate from the achievement data repository. A user must connect their own public fork of `pozpo/achievement-viewer` or another compatible public repository, owned by the account they signed into. The default target branch is `user`; the account, owner, repository, and branch can be reviewed in Settings/Sync Activity.

Scanning and preview do not send achievement data to GitHub. A sync sends only the changed achievement identifiers, earned states, and available timestamps in the reviewed JSON paths. It never sends original save files, local paths, emulator configuration, credentials, or other repository files. GitHub uploads always require explicit review and confirmation, except later automatic uploads only after the user opted in; the first automatic upload still requires confirmation.

The OAuth publisher must register a GitHub OAuth App, enable Device Flow, and supply the public Client ID via `ACHIEVEMENTSYNC_GITHUB_CLIENT_ID` when publishing. No Client Secret is packaged. The app requests `public_repo` only and checks the granted permission after sign-in. Refresh tokens are rotated when expiring access tokens are enabled. OAuth consent must be completed by the user in GitHub.

For local name/art lookup, AppIDs may be sent to Steam's public Store metadata endpoint and the returned Steam-hosted header image may be cached. No unlock state or local path is sent to Steam. Metadata is cached under `%LocalAppData%\AchievementSync\Metadata`.

## License

The AchievementSync application source and documentation are licensed under MIT; see [LICENSE](LICENSE). The supplied emulator archives and user achievement data are not included in the distribution. No third-party emulator executables, DLLs, patches, or copyrighted artwork are bundled.
