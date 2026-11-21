
# AchievementSync

<p align="center">
  <strong>Track, unlock, and sync your game achievements — all from one portable Windows app.</strong>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011%20x64-blue?style=for-the-badge&logo=windows" alt="Platform" />
  <img src="https://img.shields.io/badge/Runtime-.NET%2010%20WPF-purple?style=for-the-badge&logo=dotnet" alt="Runtime" />
  <img src="https://img.shields.io/badge/License-Proprietary%20Freeware-red?style=for-the-badge" alt="License" />
  <img src="https://img.shields.io/badge/Single--File-Yes-brightgreen?style=for-the-badge" alt="Single File" />
</p>

---

## What is AchievementSync?

**AchievementSync** is a closed-source, portable Windows desktop application that helps you track, organize, enable, and back up game achievements across your local game library — no installer, no external runtimes, and no Steam login required inside the app itself.

It combines an **Achievement Enabler** (Steam Goldberg emulator, ColdClient, Ubisoft Connect, Epic Games, GOG, EA/Origin) with a **built-in schema generator**, **GitHub sync**, **save backups**, and **custom unlock toasts**.

> ⚠️ **Closed source** — This project is proprietary freeware. The source is not public. Only the compiled executable is distributed via GitHub Releases.

---

## 🎯 Features

| Feature | What it does |
|---|---|
| **Achievement Enabler** | Crackes a game and enable achievements for Steam (Goldberg emulator), ColdClient, Ubisoft Connect R1/R2, Epic Games (Nemirtingas), GOG (UniverseLAN), and EA/Origin games. |
| **Built-In Schema Generator** | Generate Steam achievement catalogs directly inside the app — no external tools to download. |
| **Steam Login via Credential Manager** | Sign in once; your refresh token is stored in Windows Credential Manager, never in plain text. |
| **Plan-and-Apply Workflow** | Preview every file change before applying. Originals are backed up with `.achievementsync-backup`. |
| **GitHub Synchronization** | Push unlocked achievements and save backups to your own viewer fork via GitHub Device Flow OAuth. |
| **Save Backups** | Sanitized save backups stored per AppID (`AppID/<id>/save/`) with system path tokens to keep your username private. |
| **Custom Unlock Toasts** | Notifications on locked→unlocked transitions, with pickable screen corner and custom sound (Steam, Xbox, PlayStation, or your own `.wav`). |
| **System Tray & Auto-Start** | Runs silently in the tray and can launch with Windows. |
| **Single-File Executable** | One `AchievementSync.exe` — download, run, go. Self-contained (.NET included). |

---

## 🚀 Quick Start

### Step 1 — Set up your achievement-viewer hub (required for GitHub sync)

Before using AchievementSync's GitHub sync, **follow [Roschach96's achievement-viewer guide](https://github.com/Roschach96/achievement-viewer)** to fork/create your own hub page. This is the page where your unlocked achievements and save backups will appear.

> 👉 Fork Roschach96's [achievement-viewer](https://github.com/Roschach96/achievement-viewer) and set up your GitHub Pages hub following their guide: https://roschach96.github.io/achievement-viewer/

### Step 2 — Download and run

1. Download `AchievementSync.exe` from the **[Releases page](https://github.com/pozpo/AchievementSync/releases/latest)**.
2. Place it in any folder (e.g., `C:\Games\AchievementSync\` or your Desktop).
3. Double-click `AchievementSync.exe` — no installer, no .NET runtime needed.

### Step 3 — Crack a game and enable achievements for a game

1. Open AchievementSync and go to the **Achievement Enabler** tab.
2. Sign in to Steam (saved in Windows Credential Manager).
3. **Browse** and select your game's folder (the one containing `steam_api64.dll` / `steam_api.dll`).
4. Enter the game's **Steam AppID** (or click Detect).
5. Pick the emulator target.
6. Click **Generate plan** → review → **Apply plan**.

---

## 🙏 Acknowledgments

AchievementSync is built on and inspired by the excellent work of **Roschach96**:

- **achievement-viewer hub** — https://roschach96.github.io/achievement-viewer/ — follow their guide to fork/create your own hub page for displaying synced achievements.
- **Achievement-Enabler** — https://github.com/Roschach96/Achievement-Enabler — the emulator adapter foundation used by this project's Achievement Enabler feature.

Thank you, Roschach96, for sharing these tools with the community.

---

## 📥 Download

AchievementSync is distributed as a **portable, self-contained single-file executable**.

- **Latest release**: [AchievementSync.exe](https://github.com/pozpo/AchievementSync/releases/latest)
- **Supported OS**: Windows 10 (64-bit) & Windows 11 (64-bit)
- **Requirements**: None — .NET is bundled inside the EXE.

---

## 🔒 License & Intellectual Property

Copyright (c) 2026 Pozo. All rights reserved.

This software is **proprietary freeware**. It is provided free of charge for personal use. Reverse engineering, decompilation, unauthorized modification, or redistribution of modified source code is strictly prohibited without explicit written permission from the copyright owner.

See [LICENSE.md](LICENSE.md) for full terms.

---

<p align="center">
  Built with ❤️ for the achievement-hunting community.
</p>
