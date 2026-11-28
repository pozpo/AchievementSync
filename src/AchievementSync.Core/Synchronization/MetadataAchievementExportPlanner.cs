using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

using AchievementSync.Core.Metadata;

namespace AchievementSync.Core.Synchronization;

/// <summary>
/// The file at metadata/&lt;AppID&gt;/achievements.json is the only file that may be uploaded to the configured
/// viewer repository. This produces the export list from those files, so pushes always match what the
/// user has on disk instead of data assembled in memory.
/// </summary>
public sealed record MetadataAchievementExport(string AppId, string RelativePath, string Json, int KnownStates);

/// <summary>Viewer-compatible achievement state used when writing the cached achievements.json.</summary>
public sealed record ViewerAchievementState(
    [property: JsonPropertyName("earned")] bool Earned,
    [property: JsonPropertyName("earned_time")] long EarnedTime);

/// <summary>A metadata achievements.json that could not be exported.</summary>
public sealed record MetadataAchievementFailure(string AppId, string Reason);

public static class MetadataAchievementExportPlanner
{
    private const long MaximumExportBytes = 4 * 1024 * 1024;

    /// <summary>Returns one export file per cached AppID with at least one confirmed unlocked achievement.</summary>
    public static (IReadOnlyList<MetadataAchievementExport> Files, IReadOnlyList<MetadataAchievementFailure> Failures)
        Build(string metadataRoot, IReadOnlySet<string>? appIds = null)
    {
        var files = new List<MetadataAchievementExport>();
        var failures = new List<MetadataAchievementFailure>();
        if (string.IsNullOrWhiteSpace(metadataRoot) || !Directory.Exists(metadataRoot))
        {
            return (files, failures);
        }

        foreach (var directory in Directory.EnumerateDirectories(metadataRoot))
        {
            var appId = Path.GetFileName(directory);
            if (!AppIdValidator.IsValid(appId) || (appIds is not null && !appIds.Contains(appId))) continue;

            var path = Path.Combine(directory, "achievements.json");
            if (!File.Exists(path)) continue;

            try
            {
                var info = new FileInfo(path);
                if (info.Length > MaximumExportBytes)
                {
                    failures.Add(new MetadataAchievementFailure(appId, "achievements.json is larger than 4 MB."));
                    continue;
                }

                var json = File.ReadAllText(path);
                using var document = JsonDocument.Parse(json,
                    new JsonDocumentOptions { MaxDepth = 64, CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    failures.Add(new MetadataAchievementFailure(appId, "achievements.json is not a JSON object keyed by achievement ID."));
                    continue;
                }

                var count = 0;
                var unlockedCount = 0;
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.Object) continue;
                    count++;
                    if (IsEarned(property.Value)) unlockedCount++;
                }

                if (count == 0)
                {
                    failures.Add(new MetadataAchievementFailure(appId, "achievements.json contains no achievement states."));
                    continue;
                }

                if (unlockedCount == 0) continue;

                files.Add(new MetadataAchievementExport(appId, $"AppID/{appId}/achievements.json", json, count));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or JsonException)
            {
                failures.Add(new MetadataAchievementFailure(appId, $"Could not read the local achievement file ({exception.GetType().Name})."));
            }
        }

        return (files, failures);
    }

    internal static bool ContainsUnlockedAchievement(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json,
                new JsonDocumentOptions { MaxDepth = 64, CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.EnumerateObject().Any(property =>
                    property.Value.ValueKind == JsonValueKind.Object && IsEarned(property.Value));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsEarned(JsonElement state) =>
        state.TryGetProperty("earned", out var earned)
        && (earned.ValueKind == JsonValueKind.True
            || (earned.ValueKind == JsonValueKind.Number && earned.TryGetDouble(out var number) && number != 0));
}
