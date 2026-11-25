using System.Text.Json;
using AchievementSync.Core.Metadata;
using AchievementSync.Core.Models;

namespace AchievementSync.Core.Enabler;

/// <summary>Writes local achievement data to the Roschach96 achievement-viewer format.</summary>
/// <remarks>
/// Output structure (saved to %LOCALAPPDATA%\AchievementSync\Metadata\<AppID>\achievements.json):
/// {
///   "ACHIEVEMENT_API_NAME": { "earned": true,  "earned_time": 1793701998 },
///   "ANOTHER_API_NAME":     { "earned": false, "earned_time": 0 }
/// }
/// Only real API names are written; synthetic display-name identities from public catalogs are omitted.
/// </remarks>
public static class ViewerAchievementsWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Saves achievement records for a game in the viewer-compatible JSON structure.</summary>
    /// <param name="appId">The Steam AppID (used as the metadata folder name).</param>
    /// <param name="achievements">The achievement records to write.</param>
    /// <returns>The path to the written achievements.json file.</returns>
    public static string WriteAchievementsJson(string appId, IReadOnlyList<AchievementRecord> achievements)
    {
        if (string.IsNullOrWhiteSpace(appId))
            throw new ArgumentException("A Steam AppID is required.", nameof(appId));

        var metadataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AchievementSync", "Metadata");
        var appDir = Path.Combine(metadataRoot, appId);
        AppMetadataLayout.RemoveLegacyFiles(metadataRoot, appId);
        Directory.CreateDirectory(appDir);

        var destination = Path.Combine(appDir, "achievements.json");

        var viewerEntries = ReadExistingStates(destination);
        foreach (var achievement in achievements)
        {
            var key = achievement.ApiName?.Trim();
            // Display-name identities are internal placeholders from public pages; the Viewer
            // requires real achievement API names to match its game catalog.
            if (string.IsNullOrWhiteSpace(key)
                || key.StartsWith("name:", StringComparison.OrdinalIgnoreCase)) continue;

            var earned = achievement.IsUnlocked == true;
            var earnedTime = earned ? ValidTimestamp(achievement.UnlockTimeUnix) : 0L;
            if (viewerEntries.TryGetValue(key, out var existing))
            {
                earned |= existing.Earned;
                earnedTime = earned ? Math.Max(earnedTime, ValidTimestamp(existing.EarnedTime)) : 0L;
            }
            viewerEntries[key] = new ViewerAchievementEntry(earned, earnedTime);
        }

        // Do not erase an earlier usable file if the current profile contains no real API names.
        if (viewerEntries.Count == 0 && File.Exists(destination)) return destination;

        var ordered = new SortedDictionary<string, ViewerAchievementEntry>(StringComparer.Ordinal);
        foreach (var (key, value) in viewerEntries.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            ordered[key] = value;
        var json = JsonSerializer.Serialize(ordered, JsonOptions);
        var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, json);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }

        return destination;
    }

    private static Dictionary<string, ViewerAchievementEntry> ReadExistingStates(string path)
    {
        var states = new Dictionary<string, ViewerAchievementEntry>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(path)) return states;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object) return states;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(property.Name) || property.Value.ValueKind != JsonValueKind.Object
                    || !TryReadBoolean(property.Value, "earned", out var earned)) continue;
                var time = TryReadInt64(property.Value, "earned_time", out var parsedTime)
                    ? ValidTimestamp(parsedTime)
                    : 0;
                states[property.Name] = new ViewerAchievementEntry(earned, earned ? time : 0);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // A damaged cache is rebuilt from the current profiles.
        }
        return states;
    }

    private static bool TryReadBoolean(JsonElement element, string propertyName, out bool value)
    {
        if (TryGetProperty(element, propertyName, out var field))
        {
            if (field.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                value = field.GetBoolean();
                return true;
            }
            if (field.ValueKind == JsonValueKind.Number && field.TryGetInt64(out var number) && number is 0 or 1)
            {
                value = number == 1;
                return true;
            }
            if (field.ValueKind == JsonValueKind.String && bool.TryParse(field.GetString(), out value)) return true;
        }
        value = false;
        return false;
    }

    private static bool TryReadInt64(JsonElement element, string propertyName, out long value)
    {
        if (TryGetProperty(element, propertyName, out var field))
        {
            if (field.ValueKind == JsonValueKind.Number && field.TryGetInt64(out value)) return true;
            if (field.ValueKind == JsonValueKind.String && long.TryParse(field.GetString(), out value)) return true;
        }
        value = 0;
        return false;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            value = property.Value;
            return true;
        }
        value = default;
        return false;
    }

    private static long ValidTimestamp(long? value)
    {
        var timestamp = value ?? 0;
        if (timestamp > 10_000_000_000) timestamp /= 1000;
        return timestamp is > 0 and <= 253402300799 ? timestamp : 0;
    }
}
