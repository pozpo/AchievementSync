namespace AchievementSync.Core.Metadata;

/// <summary>Steam Store metadata for one AppID, including the public achievement listing total.</summary>
public sealed record SteamGameMetadata(string AppId, string? Name, Uri? HeaderImageUri, int? AchievementsTotal = null);
