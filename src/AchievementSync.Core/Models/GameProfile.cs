namespace AchievementSync.Core.Models;

public sealed record GameProfile(
    string AppId,
    string? Title,
    IReadOnlyList<string> SourceFiles,
    IReadOnlyList<AchievementRecord> Achievements,
    bool HasCompleteCatalog,
    bool UnlistedAchievementsAreLocked = false,
    int? DeclaredAchievementTotal = null)
{
    public int UnlockedCount => Achievements.Count(item => item.IsUnlocked == true);

    public int KnownStateCount => Achievements.Count(item => item.IsUnlocked.HasValue);

    public int? TotalCount => HasCompleteCatalog
        ? Achievements.Count
        : DeclaredAchievementTotal is > 0
            ? Math.Max(DeclaredAchievementTotal.Value, Achievements.Count)
            : null;

    // A catalog alone must not imply the player has zero unlocks. Show a
    // percentage only when the available player states cover every listed row.
    public double? CompletionPercent => TotalCount is > 0
        && KnownStateCount == Achievements.Count
        ? Math.Round(UnlockedCount * 100d / TotalCount.Value, 1)
        : null;

    public AchievementRecord? LatestUnlocked => Achievements
        .Where(item => item.IsUnlocked == true && item.UnlockTimeUnix is > 0)
        .OrderByDescending(item => item.UnlockTimeUnix)
        .FirstOrDefault();
}
