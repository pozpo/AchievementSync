using System.Collections.Concurrent;
using AchievementSync.Core.Metadata;
using AchievementSync.Core.Platforms;

namespace AchievementSync.Core.Synchronization;

/// <summary>Maps discovered native AppData save folders to Steam AppIDs without requiring the game to be installed.</summary>
public static class NativeSaveAppIdResolver
{
    private const int MaximumTitleLookupsPerBackup = 24;

    public static async Task<IReadOnlyList<DiscoveredSaveFile>> ResolveAsync(
        IReadOnlyList<DiscoveredSaveFile> saves,
        Func<string, CancellationToken, Task<string?>> titleLookupAsync,
        IReadOnlyDictionary<string, string>? userOverrides = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(saves);
        ArgumentNullException.ThrowIfNull(titleLookupAsync);

        var candidates = saves
            .Where(save => save.AppId.StartsWith("native-", StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(save.GameTitle))
            .ToArray();
        var overridesByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var save in candidates)
        {
            if (TryGetOverride(userOverrides, save.RootKey, save.GameTitle!, out var appId))
                overridesByPath[save.SourcePath] = appId;
        }

        var titleGroups = candidates
            .Where(save => !overridesByPath.ContainsKey(save.SourcePath))
            .GroupBy(save => NormalizeTitle(save.GameTitle!), StringComparer.Ordinal)
            .Where(group => group.Key.Length > 0)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Take(MaximumTitleLookupsPerBackup)
            .ToArray();

        var appIdsByTitle = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        await Parallel.ForEachAsync(titleGroups,
            new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = cancellationToken },
            async (group, token) =>
            {
                var title = group.First().GameTitle!;
                var appId = await titleLookupAsync(title, token).ConfigureAwait(false);
                if (AppIdValidator.IsValid(appId)) appIdsByTitle[group.Key] = appId!;
            }).ConfigureAwait(false);

        return saves.Select(save =>
        {
            if (!save.AppId.StartsWith("native-", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(save.GameTitle)) return save;

            var appId = overridesByPath.GetValueOrDefault(save.SourcePath);
            if (appId is null) appIdsByTitle.TryGetValue(NormalizeTitle(save.GameTitle), out appId);
            if (!AppIdValidator.IsValid(appId)) return save;

            var relative = save.RelativePath
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/');
            var repoPath = $"AppID/{appId}/save/{save.RootKey}/{relative}";
            return save with { AppId = appId!, RepoPath = repoPath, BackupFolder = null };
        }).ToArray();
    }

    private static bool TryGetOverride(
        IReadOnlyDictionary<string, string>? overrides,
        string rootKey,
        string title,
        out string appId)
    {
        appId = string.Empty;
        if (overrides is null) return false;
        var key = SteamAppIdResolver.BuildOverrideKey(rootKey, title);
        if (overrides.TryGetValue(key, out var mapped) && AppIdValidator.IsValid(mapped))
        {
            appId = mapped;
            return true;
        }
        if (overrides.TryGetValue(title, out mapped) && AppIdValidator.IsValid(mapped))
        {
            appId = mapped;
            return true;
        }
        return false;
    }

    private static string NormalizeTitle(string title) =>
        new(title.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
