using AchievementSync.Core.Enabler;
using AchievementSync.Core.Models;
using AchievementSync.Core.Platforms;

namespace AchievementSync.Core.Synchronization;

public sealed record PlatformAchievementExportPlan(
    IReadOnlyList<AchievementExportFile> Files,
    IReadOnlyList<SteamAppIdResolution> Resolutions);

/// <summary>Combines the normal AppID metadata export with locally collected platform progress.</summary>
public static class PlatformAchievementExportBuilder
{
    public static async Task<PlatformAchievementExportPlan> BuildAsync(
        IEnumerable<AchievementExportFile> metadataFiles,
        IEnumerable<PlatformGameSnapshot> sourceSnapshots,
        SteamAppIdResolver resolver,
        IReadOnlyDictionary<string, string>? userOverrides = null,
        IReadOnlySet<string>? blacklistedKeys = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadataFiles);
        ArgumentNullException.ThrowIfNull(sourceSnapshots);
        ArgumentNullException.ThrowIfNull(resolver);

        var filesByPath = metadataFiles.ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase);
        var existingByAppId = filesByPath.Values
            .Where(file => file.RelativePath.EndsWith("/achievements.json", StringComparison.OrdinalIgnoreCase))
            .GroupBy(file => file.AppId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Json, StringComparer.Ordinal);

        var resolutions = new List<SteamAppIdResolution>();
        foreach (var snapshot in sourceSnapshots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            resolutions.Add(await resolver.ResolveAsync(snapshot, userOverrides, blacklistedKeys,
                cancellationToken: cancellationToken).ConfigureAwait(false));
        }

        foreach (var file in AchievementExportPlanner.BuildFromResolutions(resolutions, existingByAppId))
            filesByPath[file.RelativePath] = file;

        var appIdsWithUnlocks = filesByPath.Values
            .Where(file => file.RelativePath.EndsWith("/achievements.json", StringComparison.OrdinalIgnoreCase)
                && MetadataAchievementExportPlanner.ContainsUnlockedAchievement(file.Json))
            .Select(file => file.AppId)
            .ToHashSet(StringComparer.Ordinal);

        return new PlatformAchievementExportPlan(
            filesByPath.Values
                .Where(file => appIdsWithUnlocks.Contains(file.AppId))
                .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
                .ToArray(),
            resolutions);
    }
}
