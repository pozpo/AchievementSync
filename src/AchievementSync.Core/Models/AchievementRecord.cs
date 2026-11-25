namespace AchievementSync.Core.Models;

/// <summary>
/// A normalized achievement entry. A null unlock state means the source did not
/// provide enough information to call it locked or unlocked.
/// </summary>
public sealed record AchievementRecord(
    string ApiName,
    string? DisplayName,
    string? Description,
    bool? IsUnlocked,
    long? UnlockTimeUnix,
    string? IconUri = null,
    bool IsHidden = false,
    string? TrophyType = null,
    string? LockedIconUri = null);
