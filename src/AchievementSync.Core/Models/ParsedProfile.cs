namespace AchievementSync.Core.Models;

public sealed record ParsedProfile(
    string AppId,
    string SourceFormat,
    string FilePath,
    DateTimeOffset LastWriteTimeUtc,
    IReadOnlyList<AchievementRecord> Achievements,
    bool HasCompleteCatalog,
    string? Title = null,
    bool UnlistedAchievementsAreLocked = false);
