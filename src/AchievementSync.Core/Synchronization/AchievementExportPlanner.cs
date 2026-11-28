using System.Text.Json;
using System.Text.Json.Serialization;
using AchievementSync.Core.Metadata;
using AchievementSync.Core.Models;
using AchievementSync.Core.Platforms;

namespace AchievementSync.Core.Synchronization;

/// <summary>Creates local-only previews and GitHub exports in the viewer's AppID format.</summary>
public static class AchievementExportPlanner
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static IReadOnlyList<AchievementExportFile> Build(IEnumerable<GameProfile> games)
    {
        var files = new List<AchievementExportFile>();
        foreach (var game in games.OrderBy(game => game.AppId, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(game.AppId) || !game.AppId.All(char.IsAsciiDigit)) continue;
            if (game.UnlockedCount == 0) continue;

            var known = game.Achievements
                .Where(item => item.IsUnlocked.HasValue)
                .GroupBy(item => item.ApiName, StringComparer.Ordinal)
                .Select(group => group.OrderByDescending(item => item.IsUnlocked == true)
                    .ThenByDescending(item => item.UnlockTimeUnix ?? 0).First())
                .OrderBy(item => item.ApiName, StringComparer.Ordinal)
                .ToArray();
            if (known.Length == 0) continue;

            var states = new SortedDictionary<string, ViewerAchievementState>(StringComparer.Ordinal);
            foreach (var achievement in known)
            {
                var unlocked = achievement.IsUnlocked == true;
                states[achievement.ApiName] = new ViewerAchievementState(
                    unlocked,
                    unlocked && achievement.UnlockTimeUnix is > 0 ? achievement.UnlockTimeUnix.Value : 0);
            }

            files.Add(new AchievementExportFile(
                $"AppID/{game.AppId}/achievements.json",
                game.AppId,
                states.Count,
                JsonSerializer.Serialize(states, JsonOptions)));
        }

        return files;
    }

    /// <summary>
    /// Accepts resolved Steam AppID mappings and generates the exact contract:
    /// <c>{ "&lt;api_name&gt;": { "earned": bool, "earned_time": long } }</c>,
    /// adding <c>&lt;Platform&gt;.platform</c> markers for non-Steam sources and optional <c>blacklist</c> marker.
    /// Merges achievements across multiple sources using a union, ensuring unlocks are never lost,
    /// timestamps/statuses are never downgraded, and larger valid files are never overwritten by smaller ones.
    /// </summary>
    public static IReadOnlyList<AchievementExportFile> BuildFromResolutions(
        IEnumerable<SteamAppIdResolution> resolutions,
        IReadOnlyDictionary<string, string>? existingAppIdJson = null,
        bool includePlatformMarkers = true,
        bool includeBlacklistMarker = true)
    {
        ArgumentNullException.ThrowIfNull(resolutions);

        var validResolutions = resolutions
            .Where(r => AppIdValidator.IsValid(r.ResolvedSteamAppId))
            .GroupBy(r => r.ResolvedSteamAppId!, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal);

        var files = new List<AchievementExportFile>();

        foreach (var group in validResolutions)
        {
            var appId = group.Key;
            var mergedStates = new Dictionary<string, (bool Earned, long EarnedTime)>(StringComparer.OrdinalIgnoreCase);

            // 1. If existing JSON is supplied (from disk or repository), load and seed it so unlocks are never lost
            if (existingAppIdJson is not null && existingAppIdJson.TryGetValue(appId, out var existingJson) && !string.IsNullOrWhiteSpace(existingJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(existingJson);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in doc.RootElement.EnumerateObject())
                        {
                            if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                            var earned = prop.Value.TryGetProperty("earned", out var e) && (e.ValueKind == JsonValueKind.True || (e.ValueKind == JsonValueKind.Number && e.GetInt64() != 0));
                            var earnedTime = prop.Value.TryGetProperty("earned_time", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetInt64(out var ts) ? Math.Max(0, ts) : 0;
                            mergedStates[prop.Name] = (earned, earned ? earnedTime : 0);
                        }
                    }
                }
                catch (JsonException)
                {
                    // If existing JSON was corrupt, we do not abort; freshly resolved states take over
                }
            }

            // 2. Union all achievements from all resolutions in this AppID group
            var isBlacklisted = false;

            foreach (var resolution in group)
            {
                if (resolution.IsBlacklisted)
                    isBlacklisted = true;

                foreach (var ach in resolution.NormalizedAchievements.Where(item => item.EarnedKnown))
                {
                    if (string.IsNullOrWhiteSpace(ach.Id)) continue;

                    var incomingEarned = ach.Earned;
                    var incomingTime = incomingEarned ? Math.Max(0, ach.EarnedTimeUnix) : 0L;

                    if (mergedStates.TryGetValue(ach.Id, out var existing))
                    {
                        var mergedEarned = existing.Earned || incomingEarned;
                        var mergedTime = mergedEarned
                            ? Math.Max(existing.EarnedTime, incomingTime)
                            : 0L;
                        mergedStates[ach.Id] = (mergedEarned, mergedTime);
                    }
                    else
                    {
                        mergedStates[ach.Id] = (incomingEarned, incomingTime);
                    }
                }
            }

            if (mergedStates.Count > 0)
            {
                var sortedStates = new SortedDictionary<string, ViewerAchievementState>(StringComparer.Ordinal);
                foreach (var (apiName, state) in mergedStates)
                {
                    sortedStates[apiName] = new ViewerAchievementState(state.Earned, state.Earned ? state.EarnedTime : 0);
                }

                files.Add(new AchievementExportFile(
                    $"AppID/{appId}/achievements.json",
                    appId,
                    sortedStates.Count,
                    JsonSerializer.Serialize(sortedStates, JsonOptions)));
            }

            // 3. Platform marker files for non-Steam sources: <Platform>.platform
            if (includePlatformMarkers)
            {
                foreach (var resolution in group
                             .Where(item => !string.Equals(item.Platform, "steam", StringComparison.OrdinalIgnoreCase)
                                            && !string.IsNullOrWhiteSpace(item.Platform))
                             .GroupBy(item => GetMarkerPlatform(item.Platform, item.SourceVersion), StringComparer.OrdinalIgnoreCase)
                             .Select(item => item.Key)
                             .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    var markerName = FormatPlatformMarkerName(resolution);
                    files.Add(new AchievementExportFile(
                        $"AppID/{appId}/{markerName}.platform",
                        appId,
                        0,
                        string.Empty));
                }
            }

            // 4. Blacklist marker file if marked
            if (includeBlacklistMarker && isBlacklisted)
            {
                files.Add(new AchievementExportFile(
                    $"AppID/{appId}/blacklist",
                    appId,
                    0,
                    string.Empty));
            }
        }

        return files;
    }

    public static string FormatPlatformMarkerName(string platform) => platform.Trim().ToLowerInvariant() switch
    {
        "epic" => "Epic",
        "uplay" => "Uplay",
        "ubisoft" => "Ubisoft",
        "xbox" or "xboxpc" => "Xbox",
        "playstation" or "ps4" or "ps5" or "rpcs3" => "PlayStation",
        "ea" => "EA",
        "gog" => "GOG",
        "retroachievements" or "ra" => "RetroAchievements",
        "recomp" or "ladxhd" => "Recomp",
        "ps3recomp" => "PS3Recomp",
        "ps4recomp" => "PS4Recomp",
        "ladxhd-recomp" => "LADXHD",
        "xenia" or "xbox360" => "Xbox360",
        "gfwl" => "GFWL",
        _ => platform.Length > 0 ? char.ToUpperInvariant(platform[0]) + platform[1..] : "Unknown"
    };

    private static string GetMarkerPlatform(string platform, string? sourceVersion) =>
        sourceVersion?.Trim().ToLowerInvariant() switch
        {
            "ps3recomp" => "ps3recomp",
            "ps4recomp" => "ps4recomp",
            "ladxhd" => "ladxhd-recomp",
            _ => platform
        };

    private sealed record ViewerAchievementState(
        [property: JsonPropertyName("earned")] bool Earned,
        [property: JsonPropertyName("earned_time")] long EarnedTime);
}

public sealed record AchievementExportFile(string RelativePath, string AppId, int AchievementCount, string Json);
