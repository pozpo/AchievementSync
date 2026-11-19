using AchievementSync.Core.Models;

namespace AchievementSync.Core.Scanning;

public static class ProfileMerger
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
        var catalogProfiles = sourceProfiles.Where(profile => profile.HasCompleteCatalog).ToArray();
        var catalogIds = catalogProfiles
            .SelectMany(profile => profile.Achievements)
            .Select(item => item.ApiName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hasCompleteCatalog = catalogProfiles.Length > 0 && sourceProfiles
            .Where(profile => !profile.HasCompleteCatalog)
            .SelectMany(profile => profile.Achievements)
            .All(item => catalogIds.Contains(item.ApiName));
        var achievements = sourceProfiles
            .SelectMany(profile => profile.Achievements)
            .GroupBy(item => item.ApiName, StringComparer.OrdinalIgnoreCase)
            .Select(group => MergeAchievement(group, sourceProfiles))
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
            hasCompleteCatalog,
            sourceProfiles.Any(profile => profile.UnlistedAchievementsAreLocked));
    }

    public static GameProfile EnrichWithCatalog(GameProfile game, ParsedProfile catalog)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(catalog);
        if (!string.Equals(game.AppId, catalog.AppId, StringComparison.Ordinal))
            throw new ArgumentException("The catalog AppID does not match the game profile.", nameof(catalog));

        var existing = new ParsedProfile(
            game.AppId,
            "Merged local profiles",
            string.Join(";", game.SourceFiles),
            DateTimeOffset.UtcNow,
            game.Achievements,
            game.HasCompleteCatalog,
            game.Title,
            game.UnlistedAchievementsAreLocked);
        var enriched = Merge([existing, catalog]).Single();
        return enriched with { SourceFiles = game.SourceFiles };
    }

    private static AchievementRecord MergeAchievement(
        IGrouping<string, AchievementRecord> group,
        IReadOnlyList<ParsedProfile> profiles)
    {
        var records = group.ToArray();
        var apiName = group.Key;
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
        if (!unlocked.HasValue && profiles.Any(profile =>
                profile.UnlistedAchievementsAreLocked
                && !profile.Achievements.Any(item => string.Equals(item.ApiName, apiName, StringComparison.OrdinalIgnoreCase))))
        {
            unlocked = false;
        }
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
