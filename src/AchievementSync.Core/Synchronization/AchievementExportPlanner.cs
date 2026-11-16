using System.Text.Json;
using System.Text.Json.Serialization;
using AchievementSync.Core.Models;

namespace AchievementSync.Core.Synchronization;

/// <summary>Creates local-only previews in the legacy viewer's AppID JSON format.</summary>
public static class AchievementExportPlanner
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static IReadOnlyList<AchievementExportFile> Build(IEnumerable<GameProfile> games)
    {
        var files = new List<AchievementExportFile>();
        foreach (var game in games.OrderBy(game => game.AppId, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(game.AppId) || !game.AppId.All(char.IsAsciiDigit)) continue;

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

    private sealed record ViewerAchievementState(
        [property: JsonPropertyName("earned")] bool Earned,
        [property: JsonPropertyName("earned_time")] long EarnedTime);
}

public sealed record AchievementExportFile(string RelativePath, string AppId, int AchievementCount, string Json);
