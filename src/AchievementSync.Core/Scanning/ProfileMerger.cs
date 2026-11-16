using AchievementSync.Core.Models;

namespace AchievementSync.Core.Scanning;

internal static class ProfileMerger
{
    public static IReadOnlyList<GameProfile> Merge(IEnumerable<ParsedProfile> profiles)
    {
        return profiles
            .GroupBy(profile => profile.AppId, StringComparer.Ordinal)
            .Select(MergeGame)
            .OrderBy(game => game.Title ?? game.AppId, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static GameProfile MergeGame(IGrouping<string, ParsedProfile> group)
    {
        var sourceProfiles = group.OrderBy(profile => profile.LastWriteTimeUtc).ToArray();
        var achievements = sourceProfiles
            .SelectMany(profile => profile.Achievements)
            .GroupBy(item => item.ApiName, StringComparer.OrdinalIgnoreCase)
            .Select(MergeAchievement)
            .OrderBy(item => item.DisplayName ?? item.ApiName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        return new GameProfile(
            group.Key,
            Title: sourceProfiles
                .Where(profile => !string.IsNullOrWhiteSpace(profile.Title))
                .OrderByDescending(profile => profile.LastWriteTimeUtc)
                .Select(profile => profile.Title)
                .FirstOrDefault(),
            sourceProfiles.Select(profile => profile.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            achievements,
            sourceProfiles.Any(profile => profile.HasCompleteCatalog));
    }

    private static AchievementRecord MergeAchievement(IGrouping<string, AchievementRecord> group)
    {
        var records = group.ToArray();
        var detail = records
            .OrderByDescending(DetailScore)
            .ThenByDescending(item => item.UnlockTimeUnix ?? 0)
            .First();
        var unlockedRecords = records.Where(item => item.IsUnlocked == true).ToArray();
        bool? unlocked = unlockedRecords.Length > 0
            ? true
            : records.Any(item => item.IsUnlocked == false)
                ? false
                : null;
        var unlockTime = unlockedRecords
            .Select(item => item.UnlockTimeUnix)
            .Where(time => time is > 0)
            .Max();

        return detail with
        {
            IsUnlocked = unlocked,
            UnlockTimeUnix = unlockTime ?? (unlocked == true ? records.FirstOrDefault(item => item.IsUnlocked == true)?.UnlockTimeUnix : detail.UnlockTimeUnix)
        };
    }

    private static int DetailScore(AchievementRecord item) =>
        (string.IsNullOrWhiteSpace(item.DisplayName) ? 0 : 2)
        + (string.IsNullOrWhiteSpace(item.Description) ? 0 : 2)
        + (string.IsNullOrWhiteSpace(item.IconUri) ? 0 : 1);
}
