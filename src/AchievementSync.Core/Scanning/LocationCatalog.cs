using AchievementSync.Core.Settings;

namespace AchievementSync.Core.Scanning;

public static class LocationCatalog
{
    public static IReadOnlyList<ScanLocation> Build(AppSettings settings)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var commonDocuments = Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var definitions = new (string Id, string Name, string Path)[]
        {
            ("steam-codex-public", "Steam CODEX (Public Documents)", Path.Combine(commonDocuments, "Steam", "CODEX")),
            ("steam-rune-public", "Steam RUNE (Public Documents)", Path.Combine(commonDocuments, "Steam", "RUNE")),
            ("onlinefix-public", "OnlineFix (Public Documents)", Path.Combine(commonDocuments, "OnlineFix")),
            ("empress-public", "EMPRESS (Public Documents)", Path.Combine(commonDocuments, "EMPRESS")),
            ("goldberg-steam", "Goldberg SteamEmu", Path.Combine(appData, "Goldberg SteamEmu Saves")),
            ("goldberg-uplay", "Goldberg UplayEmu", Path.Combine(appData, "Goldberg UplayEmu Saves")),
            ("r1-uplay", "R1 UplayEmu", Path.Combine(appData, "R1 UplayEmu Saves")),
            ("gse", "GSE Saves", Path.Combine(appData, "GSE Saves")),
            ("empress", "EMPRESS", Path.Combine(appData, "EMPRESS")),
            ("nemirtingas-epic", "Nemirtingas Epic Emulator", Path.Combine(appData, "NemirtingasEpicEmu")),
            ("anadius-watcher", "anadius Achievement Watcher", Path.Combine(localAppData, "anadius", "LSX emu", "achievement_watcher")),
            ("steam-codex-appdata", "Steam CODEX (AppData)", Path.Combine(appData, "Steam", "CODEX")),
            ("smartsteamemu", "SmartSteamEmu", Path.Combine(appData, "SmartSteamEmu")),
            ("skidrow", "SKIDROW", Path.Combine(localAppData, "SKIDROW")),
            ("universelan", "UniverseLAN", Path.Combine(localAppData, "UniverseLAN")),
            ("achievement-watcher-data", "Achievement Watcher data", Path.Combine(appData, "Achievement Watcher", "steam_cache", "data")),
            ("achievement-watcher-user", "Achievement Watcher user cache", Path.Combine(appData, "Achievement Watcher", "steam_cache", "user")),
            ("creamapi", "CreamAPI", Path.Combine(appData, "CreamAPI")),
            ("empress-public-root", "EMPRESS (Public Documents root)", Path.Combine(commonDocuments, "EMPRESS")),
            ("programdata-steam", "Steam (ProgramData)", Path.Combine(programData, "steam"))
        };

        var locations = new List<ScanLocation>();
        foreach (var (id, name, path) in definitions)
        {
            if (locations.Any(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            locations.Add(new ScanLocation(
                id,
                name,
                path,
                settings.EnabledLocations.TryGetValue(id, out var enabled) ? enabled : true,
                IsCustom: false,
                Directory.Exists(path)));
        }

        foreach (var custom in settings.CustomLocations)
        {
            if (string.IsNullOrWhiteSpace(custom.Path)) continue;
            locations.Add(new ScanLocation(
                "custom-" + custom.Id,
                custom.Name,
                custom.Path,
                custom.Enabled,
                IsCustom: true,
                Directory.Exists(custom.Path)));
        }

        return locations;
    }
}
