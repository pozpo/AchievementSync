using System.Text.Json;
using System.Text.Json.Serialization;

namespace AchievementSync.Core.Synchronization;

/// <summary>Validates and unions Achievement Viewer progress without losing confirmed unlocks.</summary>
public static class AchievementJsonMerge
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string Merge(string localJson, string? existingJson)
    {
        var local = Parse(localJson, "local");
        var existing = existingJson is null
            ? new Dictionary<string, State>(StringComparer.OrdinalIgnoreCase)
            : Parse(existingJson, "repository");
        var merged = new SortedDictionary<string, State>(StringComparer.Ordinal);

        foreach (var key in existing.Keys.Union(local.Keys, StringComparer.OrdinalIgnoreCase))
        {
            var remoteEntry = existing.FirstOrDefault(pair => string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase));
            var localEntry = local.FirstOrDefault(pair => string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase));
            var name = remoteEntry.Key ?? localEntry.Key;
            var remote = remoteEntry.Value;
            var current = localEntry.Value;
            var unlocked = (remote?.Earned ?? false) || (current?.Earned ?? false);
            var timestamp = unlocked
                ? Math.Max(ValidTimestamp(remote?.EarnedTime), ValidTimestamp(current?.EarnedTime))
                : 0;
            merged[name] = new State(unlocked, timestamp);
        }

        return JsonSerializer.Serialize(merged, JsonOptions);
    }

    public static bool SemanticallyEqual(string leftJson, string rightJson)
    {
        var left = Parse(leftJson, "left");
        var right = Parse(rightJson, "right");
        if (left.Count != right.Count) return false;
        foreach (var (key, state) in left)
        {
            var matching = right.FirstOrDefault(pair => string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase));
            if (matching.Key is null || matching.Value != state) return false;
        }
        return true;
    }

    public static int CountNewUnlocks(string? existingJson, string mergedJson)
    {
        var existing = existingJson is null
            ? new Dictionary<string, State>(StringComparer.OrdinalIgnoreCase)
            : Parse(existingJson, "repository");
        var merged = Parse(mergedJson, "merged");
        return merged.Count(pair => pair.Value.Earned
            && (!existing.TryGetValue(pair.Key, out var previous) || !previous.Earned));
    }

    private static Dictionary<string, State> Parse(string json, string source)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"The {source} achievement JSON must be an object keyed by achievement ID.");

            var result = new Dictionary<string, State>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(property.Name) || property.Name.Contains('/') || property.Name.Contains('\\'))
                    throw new InvalidDataException($"The {source} achievement JSON contains an invalid achievement ID.");
                if (property.Value.ValueKind != JsonValueKind.Object
                    || !property.Value.TryGetProperty("earned", out var earnedElement)
                    || !TryGetEarned(earnedElement, out var earned))
                    throw new InvalidDataException($"Achievement '{property.Name}' in the {source} JSON has no valid earned state.");

                long timestamp = 0;
                if (property.Value.TryGetProperty("earned_time", out var time))
                {
                    if (time.ValueKind != JsonValueKind.Number || !time.TryGetInt64(out timestamp) || timestamp < 0)
                        throw new InvalidDataException($"Achievement '{property.Name}' in the {source} JSON has an invalid earned_time.");
                }

                if (!result.TryAdd(property.Name, new State(earned, timestamp)))
                    throw new InvalidDataException($"The {source} JSON contains duplicate achievement IDs ignoring case.");
            }
            return result;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"The {source} achievement JSON is invalid and was not changed.", exception);
        }
    }

    private static bool TryGetEarned(JsonElement element, out bool value)
    {
        if (element.ValueKind is JsonValueKind.True) { value = true; return true; }
        if (element.ValueKind is JsonValueKind.False) { value = false; return true; }
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var number))
        {
            value = number != 0;
            return true;
        }
        if (element.ValueKind == JsonValueKind.String)
        {
            var text = element.GetString()?.Trim();
            if (bool.TryParse(text, out var parsed)) { value = parsed; return true; }
            if (string.Equals(text, "1", StringComparison.Ordinal)) { value = true; return true; }
            if (string.Equals(text, "0", StringComparison.Ordinal)) { value = false; return true; }
        }
        value = false;
        return false;
    }

    private static long ValidTimestamp(long? value) => value is > 0 and <= 253402300799 ? value.Value : 0;

    private sealed record State(
        [property: JsonPropertyName("earned")] bool Earned,
        [property: JsonPropertyName("earned_time")] long EarnedTime);
}
