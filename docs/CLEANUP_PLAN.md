# Cleanup plan

This is an inventory and deferred plan. Nothing listed here was deleted or moved during the project recovery. Do not use these generated/ignored files as source or release output.

## Retain pending verification

- `dist/AchievementSync-0.1.0-win-x64/AchievementSync.Desktop.exe` — 64,894,385 bytes.
- `dist/AchievementSync-0.1.1-win-x64/AchievementSync.Desktop.exe` — 64,894,437 bytes.
- `dist/AchievementSync-0.1.2-win-x64/AchievementSync.Desktop.exe` — 65,450,260 bytes.
- `update_achievements.ps1` and `update_achievements.zip` — original/legacy PowerShell inputs; preserved and Git-ignored.
- `.solution-build.log` (about 4.2 MB), `sync_log.txt` (about 400 KB), and `.dotnet-cli/` — local generated/tooling outputs, Git-ignored.
- `New Text Document.txt` — ignored user file, preserved. `New Text Document (2).txt` was not present in the workspace inventory; it remains explicitly ignored if later added.
- `achievement-viewer/` — separate user data checkout, 795 files. Its `.git` folder is malformed for this checkout and Git currently resolves the parent app repository. It is ignored, and no content was modified.

## Safe future cleanup sequence

1. Confirm the new single output executable works and keep it at `output/AchievementSync.exe`.
2. Inspect old `dist` binaries for any unique data/value before archiving or removing them. They are currently preserved as an audit trail and are not committed.
3. Inspect generated CLI/build logs for debugging value and any sensitive content before considering cleanup. Never publish logs by default.
4. Keep original PowerShell files and the viewer folder outside the application source tree unless their ownership, need, and contents are reviewed separately.
5. Do not recursively delete the viewer checkout, user text files, credentials, or any achievement profile as part of application cleanup.
