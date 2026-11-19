# AchievementSync

**Your game achievements, scanned, tracked, and synced — beautifully.**

![AchievementSync sync activity](docs/screenshots/sync-activity.jpg)

AchievementSync is a Windows desktop achievement manager that scans your local game folders, shows progress in a Game Library dashboard, tracks playtime, and optionally pushes reviewed achievement JSON to your own public GitHub repository — original files are never modified.

---

## ?? What it does

| Capability | Details |
|---|---|
| **Game Library** | Recursively scans configured folders, deduplicates profiles by AppID, and shows Games Found, known states, unlocked count, and measurable completion above search, filters, sorting, and a game grid. |
| **Playtime tracking** | Link each game's real executable; sessions and last-played times are saved locally. |
| **Unlock notifications** | Tray-based toast that reports confirmed locked?unlocked transitions only. |
| **GitHub sync** | OAuth Device Flow sign-in, preview exact JSON changes before upload, then a single non-forced commit to `AppID/<numeric id>/achievements.json`. |
| **Metadata & artwork** | Automatic Steam Store names + header artwork, cached locally, with display-name overrides. |

---

## ?? Download

Portable Windows x64 executable (no installer):

`C:\AchievementSync\output\AchievementSync.exe`

Requirements: Windows 10 or 11 + .NET 10 runtime (self-contained build included).

---

## ?? Quick start

1. Launch `AchievementSync.exe`.
2. Add the folders you want scanned (Steam/EMPRESS roots or custom paths).
3. Review your Game Library and edit game names if needed.
4. (Optional) Connect GitHub via Device Flow, select your public viewer fork, build a preview, review the JSON changes, then confirm Push.

---

## ?? License

Proprietary — all rights reserved. See [LICENSE](LICENSE). Not open source.
