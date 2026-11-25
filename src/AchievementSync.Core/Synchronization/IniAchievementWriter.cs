using System.Globalization;
using System.Text;

namespace AchievementSync.Core.Synchronization;

/// <summary>
/// Writes viewer-compatible achievement JSON back into the section-based INI format the local emulators use.
/// This is the inverse of the INI parsing used by the metadata cache; restore uses it when the manifest
/// records that the achievement data originally lived in an INI file.
/// </summary>
public static class IniAchievementWriter
{
    /// <summary>Converts a viewer-style achievements.json object into an INI byte array.</summary>
    public static byte[] FromViewerJson(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            throw new InvalidDataException("The achievements JSON is not an object keyed by achievement ID.");

        var builder = new StringBuilder();
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(property.Name) || property.Value.ValueKind != System.Text.Json.JsonValueKind.Object) continue;

            var earned = ReadBool(property.Value, "earned");
            if (earned is not { } unlocked) continue;
            var time = ReadInt64(property.Value, "earned_time") ?? 0;

            builder.Append('[').Append(property.Name).AppendLine("]");
            builder.Append("unlocked=").Append(unlocked ? "true" : "false").AppendLine();
            builder.Append("earned_time=").Append(time.ToString(CultureInfo.InvariantCulture)).AppendLine();
        }

        return new UTF8Encoding(false).GetBytes(builder.ToString());
    }

    private static bool? ReadBool(System.Text.Json.JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            if (property.Value.ValueKind is System.Text.Json.JsonValueKind.True) return true;
            if (property.Value.ValueKind is System.Text.Json.JsonValueKind.False) return false;
            if (property.Value.ValueKind == System.Text.Json.JsonValueKind.Number
                && property.Value.TryGetInt32(out var number) && number is 0 or 1) return number == 1;
            return null;
        }

        return null;
    }

    private static long? ReadInt64(System.Text.Json.JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            if (property.Value.ValueKind == System.Text.Json.JsonValueKind.Number
                && property.Value.TryGetInt64(out var value)) return value;
            return null;
        }

        return null;
    }
}