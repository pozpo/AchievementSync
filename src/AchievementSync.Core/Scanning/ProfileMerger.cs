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
        var declaredTotal = sourceProfiles
            .Where(profile => profile.DeclaredCatalogTotal is > 0)
            .Select(profile => profile.DeclaredCatalogTotal!.Value)
            .DefaultIfEmpty(0)
            .Max();

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
            sourceProfiles.Any(profile => profile.UnlistedAchievementsAreLocked),
            declaredTotal > 0 ? declaredTotal : null);
    }

    public static GameProfile EnrichWithCatalog(GameProfile game, ParsedProfile catalog)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(catalog);
        if (!string.Equals(game.AppId, catalog.AppId, StringComparison.Ordinal))
            throw new ArgumentException("The catalog AppID does not match the game profile.", nameof(catalog));

        var catalogByApiName = catalog.Achievements
            .Where(item => !string.IsNullOrWhiteSpace(item.ApiName))
            .GroupBy(item => item.ApiName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(DetailScore).First(), StringComparer.OrdinalIgnoreCase);
        var catalogByDisplayName = catalog.Achievements
            .Where(item => !string.IsNullOrWhiteSpace(item.DisplayName))
            .GroupBy(item => AchievementIdentity.NormalizeDisplayName(item.DisplayName), StringComparer.Ordinal)
            .Where(group => group.Key.Length > 0)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(DetailScore).First(), StringComparer.Ordinal);

        var usedCatalogEntries = new HashSet<AchievementRecord>();
        // When a game has any saved state, a catalog row that is missing from that
        // save is an unearned achievement. This is also the viewer/Goldberg contract.
        var missingStatesAreLocked = game.UnlistedAchievementsAreLocked || game.KnownStateCount > 0;
        var achievements = game.Achievements.Select(item =>
        {
            AchievementRecord? details = null;
            if (!string.IsNullOrWhiteSpace(item.ApiName)) catalogByApiName.TryGetValue(item.ApiName, out details);
            if (details is null && !string.IsNullOrWhiteSpace(item.DisplayName))
                catalogByDisplayName.TryGetValue(AchievementIdentity.NormalizeDisplayName(item.DisplayName), out details);
            if (details is null && !string.IsNullOrWhiteSpace(item.ApiName))
                catalogByDisplayName.TryGetValue(AchievementIdentity.NormalizeDisplayName(item.ApiName), out details);
            if (details is not null && usedCatalogEntries.Contains(details)) details = null;
            if (details is null)
                details = AchievementCatalogMatcher.FindMatch(item.ApiName, item.DisplayName, catalog.Achievements, usedCatalogEntries);
            if (details is null) return item;

            usedCatalogEntries.Add(details);
            return item with
            {
                DisplayName = HasUsefulDisplayName(item) ? item.DisplayName : details.DisplayName,
                Description = !string.IsNullOrWhiteSpace(details.Description) ? details.Description : item.Description,
                IsUnlocked = item.IsUnlocked ?? (missingStatesAreLocked ? false : null),
                IconUri = PreferMetadataIcon(item.IconUri, details.IconUri),
                LockedIconUri = PreferMetadataIcon(item.LockedIconUri, details.LockedIconUri),
                TrophyType = !string.IsNullOrWhiteSpace(item.TrophyType) ? item.TrophyType : details.TrophyType,
                IsHidden = item.IsHidden || details.IsHidden
            };
        }).ToList();

        foreach (var detail in catalog.Achievements)
        {
            if (usedCatalogEntries.Contains(detail)) continue;
            var alreadyPresent = achievements.Any(item =>
                (!string.IsNullOrWhiteSpace(detail.ApiName)
                    && string.Equals(item.ApiName, detail.ApiName, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(detail.DisplayName)
                    && string.Equals(AchievementIdentity.NormalizeDisplayName(item.DisplayName),
                        AchievementIdentity.NormalizeDisplayName(detail.DisplayName), StringComparison.Ordinal)));
            if (alreadyPresent) continue;
            achievements.Add(detail with
            {
                DisplayName = HasUsefulDisplayName(detail) ? detail.DisplayName : AchievementIdentity.HumanizeApiName(detail.ApiName),
                IsUnlocked = missingStatesAreLocked ? false : null,
                UnlockTimeUnix = null
            });
        }

        var hasAllDeclaredEntries = catalog.DeclaredCatalogTotal is > 0
            && achievements.Count >= catalog.DeclaredCatalogTotal.Value;

        return game with
        {
            Achievements = achievements.OrderBy(item => item.DisplayName ?? item.ApiName, StringComparer.CurrentCultureIgnoreCase).ToArray(),
            HasCompleteCatalog = game.HasCompleteCatalog || catalog.HasCompleteCatalog || hasAllDeclaredEntries,
            Title = string.IsNullOrWhiteSpace(game.Title) ? catalog.Title : game.Title,
            DeclaredAchievementTotal = catalog.DeclaredCatalogTotal ?? game.DeclaredAchievementTotal
        };
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
        (HasUsefulDisplayName(item) ? 2 : 0)
        + (string.IsNullOrWhiteSpace(item.Description) ? 0 : 2)
        + (string.IsNullOrWhiteSpace(item.IconUri) ? 0 : 1);

    private static bool HasUsefulDisplayName(AchievementRecord item) =>
        !AchievementIdentity.IsIdentifierLikeDisplayName(item.ApiName, item.DisplayName);

    private static string? PreferMetadataIcon(string? current, string? catalog) =>
        AchievementIdentity.IsMetadataImageUri(catalog) ? catalog
        : AchievementIdentity.IsMetadataImageUri(current) ? current
        : null;
}
