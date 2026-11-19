# Supplied archive review

The outer `C:\Games\project.zip` contained the three requested archives. The archives were inspected before implementation. Their contents were not blindly merged into the application.

## `AchievementSync.zip`

- Contains the existing `update_achievements.ps1`.
- It discovers achievement files recursively, derives a game ID from numeric directory names, reads JSON/DB data or converts supported INI sections, and writes data into `AppID/<appid>/achievements.json` before committing and pushing a Git branch.
- The workspace copy differs from the archived script by a Goldberg UplayEmu AppData scan path. Both copies remain untouched.
- Useful behavior retained in the new Core layer: recursive discovery, numeric AppID detection, JSON object and array parsing, INI progress parsing, duplicate-profile merging, and compatible AppID directory naming.
- The old Git flow fetches and hard-resets a branch, stages the whole repository, and pushes without an upload preview. That flow is not reused.

## `generate_emu_config.rar`

- Contains 1,417 entries. It is mostly an emulator/configuration bundle with executables, DLLs, generated files, and helper scripts.
- Its documentation describes generating emulator configuration and achievement metadata. Some operations need Steam data access; the documented login-file option stores a username and password as plaintext.
- The relevant reusable format is an achievement catalog with `name`, localized `displayName` and `description`, `icon`, `icon_gray`, and `hidden` fields. Achievement catalog entries without player progress are retained with an unknown unlock state.
- The bundled binaries, DLLs, logins, and emulator components are not incorporated or redistributed. No clear license grant for redistribution was found in the supplied material.

## `GSE Uplay R2 semi-auto AW Joker combined V11.zip`

- Contains 12 files, including PowerShell/Python helpers, a batch setup script, and sample configuration.
- Reusable ideas: enumerate game executables, read Steam launch-manifest VDF files, honor a nearby `steam_appid.txt`, handle BOM/CRLF text, and translate supported achievement metadata into the viewer's schema.
- Its setup script downloads or copies emulator DLLs, patches game folders, writes configuration, and removes temporary files. Those actions are outside this read-only manager and were not integrated.
- No redistribution license was established for the scripts or included components.

## Existing viewer data

- The supplied local viewer copy uses `AppID/<appid>/achievements.json` and `game-info.json`. Achievement progress is a JSON object keyed by achievement API name with `earned` and `earned_time` fields. The current remote README also documents JSON-array Achievement Watcher `.db` files.
- The app's parser and merger are compatible with the achievement object form. The workspace viewer data is excluded from the new public application source because it contains user-specific progress and unrelated repository artifacts.
- The remote repository's default branch includes an MIT `LICENSE`; the local copy did not include it. No viewer UI source is copied into this application.
- The remote README describes the `user` branch as the personal `AppID` data branch and says GitHub Pages is public by default. Any future sync must target a user's own fork and preview exactly which achievement data will be public.
- The connected GitHub repository currently has an MIT license on its default branch. Its latest commits returned in this audit were dated 2026-11-09, including “Steam API Key is no longer needed.” The desktop app uses public Steam Store metadata and does not embed a Steam API key.
- References checked: [viewer README](https://github.com/pozpo/achievement-viewer/blob/main/README.md), [viewer MIT license](https://github.com/pozpo/achievement-viewer/blob/main/LICENSE), [viewer user-branch README](https://github.com/pozpo/achievement-viewer/blob/user/README.md).

## License decision

The new app code is MIT licensed. No code or binaries from the two third-party tool bundles are redistributed. Their files were used to identify supported formats and integration hazards. Steam artwork is fetched from Steam at runtime and cached locally; it is not included in the source distribution.
