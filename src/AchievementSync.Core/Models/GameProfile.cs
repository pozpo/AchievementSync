namespace AchievementSync.Core.Models;

public sealed record GameProfile(
    string AppId,
    string? Title,
    IReadOnlyList<string> SourceFiles,
    IReadOnlyList<AchievementRecord> Achievements,
    bool HasCompleteCatalog)
{
    public int UnlockedCount => Achievements.Count(item => item.IsUnlocked == true);

    public int? TotalCount => HasCompleteCatalog ? Achievements.Count : null;

    public double? CompletionPercent => TotalCount is > 0 && Achievements.All(item => item.IsUnlocked.HasValue)
        ? Math.Round(UnlockedCount * 100d / TotalCount.Value, 1)
        : null;

    public AchievementRecord? LatestUnlocked => Achievements
        .Where(item => item.IsUnlocked == true && item.UnlockTimeUnix is > 0)
        .OrderByDescending(item => item.UnlockTimeUnix)
        .FirstOrDefault();
}
