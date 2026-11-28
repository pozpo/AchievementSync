using System.IO;
using AchievementSync.Core.Metadata;
using AchievementSync.Core.Models;
using AchievementSync.Core.Parsing;
using AchievementSync.Core.Scanning;

namespace AchievementSync.Desktop.Services;

/// <summary>Includes cached AppID progress files in the Game Library. These files are generated
/// by platform adapters and intentionally excluded from the recursive source-folder scanner.</summary>
public sealed class LocalMetadataGameLibrary(string metadataRoot)
{
    private readonly string _metadataRoot = Path.GetFullPath(metadataRoot);
    private readonly JsonAchievementParser _parser = new();

    public IReadOnlyList<GameProfile> MergeProfiles(IEnumerable<GameProfile> sourceProfiles)
    {
        ArgumentNullException.ThrowIfNull(sourceProfiles);
        var profiles = new Dictionary<string, GameProfile>(StringComparer.Ordinal);
        foreach (var group in sourceProfiles.GroupBy(profile => profile.AppId, StringComparer.Ordinal))
        {
            var merged = ProfileMerger.Merge(group.Select(ToParsedProfile).ToArray()).Single();
            profiles[group.Key] = merged with
            {
                SourceFiles = group.SelectMany(profile => profile.SourceFiles)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            };
        }

        if (!Directory.Exists(_metadataRoot)) return profiles.Values.ToArray();

        IEnumerable<string> directories;
        try { directories = Directory.EnumerateDirectories(_metadataRoot).ToArray(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return profiles.Values.ToArray();
        }

        foreach (var directory in directories)
        {
            var appId = Path.GetFileName(directory);
            if (!AppIdValidator.IsValid(appId)) continue;
            var progressPath = Path.Combine(directory, "achievements.json");
            if (!File.Exists(progressPath)) continue;

            try
            {
                var parsed = _parser.Parse(progressPath, appId).Profile;
                if (parsed is null || parsed.Achievements.Count == 0) continue;

                if (profiles.TryGetValue(appId, out var existing))
                {
                    var merged = ProfileMerger.Merge([ToParsedProfile(existing), parsed]).Single();
                    profiles[appId] = merged with { SourceFiles = existing.SourceFiles };
                }
                else
                {
                    profiles[appId] = ToGameProfile(parsed, []);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                               or System.Security.SecurityException or ArgumentException)
            {
                // Invalid or inaccessible cache files should not prevent the rest of the library loading.
            }
        }

        return profiles.Values.OrderBy(profile => profile.AppId, StringComparer.Ordinal).ToArray();
    }

    private static ParsedProfile ToParsedProfile(GameProfile profile) => new(
        profile.AppId,
        "Local achievement profile",
        profile.SourceFiles.FirstOrDefault() ?? $"local://AppID/{profile.AppId}",
        DateTimeOffset.UtcNow,
        profile.Achievements,
        profile.HasCompleteCatalog,
        profile.Title,
        profile.UnlistedAchievementsAreLocked,
        profile.DeclaredAchievementTotal);

    private static GameProfile ToGameProfile(ParsedProfile profile, IReadOnlyList<string> sourceFiles) => new(
        profile.AppId,
        profile.Title,
        sourceFiles,
        profile.Achievements,
        profile.HasCompleteCatalog,
        profile.UnlistedAchievementsAreLocked,
        profile.DeclaredCatalogTotal);
}
