using System.Text.Json.Serialization;

namespace AchievementSync.Core.Platforms;

/// <summary>One achievement read from a platform source before it is converted to viewer JSON.</summary>
public sealed record PlatformAchievement(
    string Id,
    string? Name = null,
    string? Description = null,
    bool Earned = false,
    long EarnedTimeUnix = 0,
    string? IconPath = null,
    string? IconGrayPath = null,
    string? TrophyType = null,
    double? RarityPercent = null,
    bool Hidden = false,
    bool EarnedKnown = true);

/// <summary>A complete snapshot of one game's achievements on one platform.</summary>
public sealed record PlatformGameSnapshot(
    string Platform,
    string GameId,
    string? Title,
    string SourcePath,
    IReadOnlyList<PlatformAchievement> Achievements,
    string? Version = null)
{
    [JsonIgnore] public int UnlockedCount => Achievements.Count(item => item.EarnedKnown && item.Earned);

    [JsonIgnore] public bool HasKnownStates => Achievements.Count > 0;
}

/// <summary>Outcome of collecting one platform, including human-readable diagnostics.</summary>
public sealed record PlatformCollectResult(
    string Platform,
    IReadOnlyList<PlatformGameSnapshot> Games,
    IReadOnlyList<string> Messages)
{
    public bool Succeeded => Games.Count > 0;
}

/// <summary>Environment-tokenised paths and user-configured credentials a platform adapter may inspect.</summary>
public sealed class PlatformSourceOptions
{
    public string? SteamRoot { get; set; }
    public string? XblenRoot { get; set; }
    public string? XboxPcRoot { get; set; }
    public string? Xbox360Root { get; set; }
    public string? GfwlRoot { get; set; }
    public string? Rpcs3Root { get; set; }
    public string? Ps4Root { get; set; }
    public string? Ps5Root { get; set; }
    public string? XeniaRoot { get; set; }
    public string? EpicRoot { get; set; }
    public string? GogRoot { get; set; }
    public string? UplayRoot { get; set; }
    public string? UplayCacheRoot { get; set; }
    /// <summary>Opt-in RUNE Ubisoft Connect root (intentionally not populated by CreateDefault).</summary>
    public string? UplayRuneRoot { get; set; }
    public bool LumaPlayWatcherEnabled { get; set; }
    public string? EaRoot { get; set; }
    public string? RecompRoot { get; set; }
    public string? LadxhdRoot { get; set; }

    public string? RetroAchievementsUser { get; set; }
    public string? RetroAchievementsApiKey { get; set; }

    // User-configured Microsoft Entra application & runtime Xbox Network token fields (held in Credential Manager).
    public string? XboxEntraClientId { get; set; }
    public string? XboxEntraTenantId { get; set; }
    public string? XboxXstsToken { get; set; }
    public string? XboxUhs { get; set; }
    public string? XboxXuid { get; set; }

    // User-configured Epic Account Services OAuth client & runtime token fields (held in Credential Manager).
    public string? EpicClientId { get; set; }
    public string? EpicClientSecret { get; set; }
    public string? EpicAccessToken { get; set; }
    public string? EpicAccountId { get; set; }

    /// <summary>Builds the defaults for this Windows user from well-known locations.</summary>
    public static PlatformSourceOptions CreateDefault()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var systemDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        return new PlatformSourceOptions
        {
            SteamRoot = Path.Combine(programFilesX86, "Steam"),
            XblenRoot = Path.Combine(localAppData, "XLiveLessNess"),
            XboxPcRoot = Path.Combine(systemDrive, "XboxGames"),
            Xbox360Root = Path.Combine(localAppData, "Xenia"),
            GfwlRoot = Path.Combine(localAppData, "Microsoft", "XLive"),
            Rpcs3Root = Path.Combine(roaming, "rpcs3"),
            Ps4Root = Path.Combine(roaming, "shadPS4"),
            Ps5Root = Path.Combine(localAppData, "kyty"),
            XeniaRoot = Path.Combine(localAppData, "Xenia"),
            EpicRoot = Path.Combine(localAppData, "EpicGamesLauncher"),
            GogRoot = Path.Combine(localAppData, "GOG.com", "Galaxy", "Applications"),
            UplayRoot = Path.Combine(localAppData, "Ubisoft Game Launcher", "spool"),
            UplayCacheRoot = Path.Combine(localAppData, "Ubisoft Game Launcher", "cache", "achievements"),
            UplayRuneRoot = null,
            LumaPlayWatcherEnabled = false,
            EaRoot = Path.Combine(localAppData, "Electronic Arts", "EA Desktop", "Logs"),
            LadxhdRoot = Path.Combine(localAppData, "Zelda_LA", "achievements"),
            XboxEntraTenantId = "consumers"
        };
    }
}
