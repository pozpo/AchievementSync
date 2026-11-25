using System.Text;
using System.Text.Json;

namespace AchievementSync.Core.Enabler;

/// <summary>Builds per-game UniverseLAN achievement state from a Steam-style achievement catalog.</summary>
public static class UniverseLanAchievementDefinitions
{
    public static string? Build(string achievementsJsonPath, string? existingIni = null)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(achievementsJsonPath));
            if (document.RootElement.ValueKind != JsonValueKind.Array) return null;

            var oldStates = ParseExistingStates(existingIni);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var output = new StringBuilder();
            foreach (var achievement in document.RootElement.EnumerateArray())
            {
                if (achievement.ValueKind != JsonValueKind.Object
                    || !achievement.TryGetProperty("name", out var nameElement)
                    || nameElement.ValueKind != JsonValueKind.String) continue;
                var key = nameElement.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(key) || key.Contains('[') || key.Contains(']') || !seen.Add(key)) continue;

                var displayName = ReadText(achievement, "displayName");
                var description = ReadText(achievement, "description");
                var hidden = IsHidden(achievement);
                var (unlocked, unlockTime) = oldStates.TryGetValue(key, out var state) ? state : ("0", "0");

                output.Append('[').Append(key).AppendLine("]");
                if (!string.IsNullOrWhiteSpace(displayName)) output.Append("Name = ").AppendLine(Sanitize(displayName));
                if (!string.IsNullOrWhiteSpace(description)) output.Append("Description = ").AppendLine(Sanitize(description));
                output.Append("Unlocked = ").AppendLine(unlocked);
                output.Append("UnlockTime = ").AppendLine(unlockTime);
                output.Append("Visible = ").AppendLine(hidden ? "0" : "1");
                output.AppendLine("VisibleWhileLocked = 1");
                output.AppendLine();
            }

            AppendUnmappedExistingSections(output, existingIni, seen);

            return seen.Count == 0 ? null : output.ToString();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static Dictionary<string, (string Unlocked, string UnlockTime)> ParseExistingStates(string? ini)
    {
        var result = new Dictionary<string, (string Unlocked, string UnlockTime)>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(ini)) return result;
        string? current = null;
        foreach (var rawLine in ini.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            var line = rawLine.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                current = line[1..^1];
                if (current.Length > 0) result[current] = ("0", "0");
                continue;
            }
            if (current is null || !line.Contains('=')) continue;
            var parts = line.Split('=', 2, StringSplitOptions.TrimEntries);
            var state = result[current];
            if (parts[0].Equals("Unlocked", StringComparison.OrdinalIgnoreCase)) state.Unlocked = parts[1];
            if (parts[0].Equals("UnlockTime", StringComparison.OrdinalIgnoreCase)) state.UnlockTime = parts[1];
            result[current] = state;
        }
        return result;
    }

    private static void AppendUnmappedExistingSections(StringBuilder output, string? ini, ISet<string> generatedKeys)
    {
        if (string.IsNullOrWhiteSpace(ini)) return;
        List<string>? sectionLines = null;
        string? sectionName = null;
        void Flush()
        {
            if (sectionLines is null || sectionName is null || generatedKeys.Contains(sectionName)) return;
            foreach (var line in sectionLines) output.AppendLine(line);
            output.AppendLine();
        }

        foreach (var rawLine in ini.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            var line = rawLine.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                Flush();
                sectionName = line[1..^1];
                sectionLines = [rawLine];
            }
            else if (sectionLines is not null)
            {
                sectionLines.Add(rawLine);
            }
        }
        Flush();
    }

    private static string? ReadText(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var value)) return null;
        if (value.ValueKind == JsonValueKind.String) return value.GetString();
        if (value.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in new[] { "en", "english", "default" })
            if (value.TryGetProperty(name, out var localized) && localized.ValueKind == JsonValueKind.String)
                return localized.GetString();
        return value.EnumerateObject().Select(property => property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null)
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));
    }

    private static bool IsHidden(JsonElement item) => item.TryGetProperty("hidden", out var hidden)
        && (hidden.ValueKind == JsonValueKind.True
            || hidden.ValueKind == JsonValueKind.Number && hidden.TryGetInt32(out var number) && number != 0);

    private static string Sanitize(string value) => value.Replace("\r", " ").Replace("\n", " ").Trim();
}
