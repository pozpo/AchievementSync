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

/// <summary>Environment-tokenised paths a platform adapter may inspect. All paths are optional.</summary>
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
    public string? RetroAchievementsUser { get; set; }
    public string? RetroAchievementsApiKey { get; set; }

    /// <summary>Builds the defaults for this Windows user from well-known locations.</summary>
    public static PlatformSourceOptions CreateDefault()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        return new PlatformSourceOptions
        {
            SteamRoot = Path.Combine(programFilesX86, "Steam"),
            XblenRoot = Path.Combine(localAppData, "XLiveLessNess"),
            XboxPcRoot = Path.Combine(localAppData, "xbox-pc"),
            Xbox360Root = Path.Combine(localAppData, "Xenia"),
            GfwlRoot = Path.Combine(localAppData, "Microsoft", "XLive"),
            Rpcs3Root = Path.Combine(roaming, "rpcs3"),
            Ps4Root = Path.Combine(localAppData, "shadPS4"),
            Ps5Root = Path.Combine(localAppData, "kyty"),
            XeniaRoot = Path.Combine(localAppData, "Xenia"),
            EpicRoot = Path.Combine(localAppData, "EpicGamesLauncher"),
            GogRoot = Path.Combine(localAppData, "GOG.com", "Galaxy", "Applications"),
            UplayRoot = Path.Combine(programData, "Ubisoft", "Ubisoft Game Launcher", "cache", "achievements")
        };
    }
}
