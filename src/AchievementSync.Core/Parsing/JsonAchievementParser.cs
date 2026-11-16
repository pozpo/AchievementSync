using System.Globalization;
using System.Text.Json;
using AchievementSync.Core.Models;

namespace AchievementSync.Core.Parsing;

/// <summary>
/// Reads the progress JSON used by Goldberg/GSE and the JSON-array .db format
/// consumed by the viewer. It deliberately rejects metadata-only schemas.
/// </summary>
public sealed class JsonAchievementParser : IAchievementParser
{
    private static readonly HashSet<string> JsonNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "achievements.json",
        "game-info.json"
    };

    public string FormatName => "JSON achievement profile";

    public bool SupportsFile(string path) => JsonNames.Contains(Path.GetFileName(path))
        || string.Equals(Path.GetExtension(path), ".db", StringComparison.OrdinalIgnoreCase);

    public ParseResult Parse(string path, string appId)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 64
            });

            var isViewerGameInfo = string.Equals(Path.GetFileName(path), "game-info.json", StringComparison.OrdinalIgnoreCase);
            var title = isViewerGameInfo ? ReadString(document.RootElement, "name") : null;
            var entries = document.RootElement.ValueKind switch
            {
                JsonValueKind.Object when isViewerGameInfo && TryGetProperty(document.RootElement, "achievements", out var catalog)
                    => ReadSchemaObject(catalog),
                JsonValueKind.Object => ReadObject(document.RootElement),
                JsonValueKind.Array => ReadArray(document.RootElement),
                _ => new List<AchievementRecord>()
            };

            if (entries.Count == 0)
            {
                return ParseResult.Unsupported("The JSON file does not contain recognizable unlock-state fields.");
            }

            var fileTime = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
            return ParseResult.Parsed(new ParsedProfile(
                appId,
                FormatName,
                path,
                fileTime,
                entries,
                HasCompleteCatalog: true,
                Title: title));
        }
        catch (JsonException)
        {
            return ParseResult.Unsupported("The file is not valid JSON.");
        }
        catch (IOException)
        {
            return ParseResult.Unsupported("The file could not be read.");
        }
        catch (UnauthorizedAccessException)
        {
            return ParseResult.Unsupported("Access to the file was denied.");
        }
    }

    private static List<AchievementRecord> ReadSchemaObject(JsonElement root)
    {
        var entries = new List<AchievementRecord>();
        if (root.ValueKind != JsonValueKind.Object) return entries;

        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object) continue;
            var item = property.Value;
            var icon = ReadString(item, "icon");
            if (string.IsNullOrWhiteSpace(icon)) icon = ReadString(item, "icongray") ?? ReadString(item, "icon_gray");
            var displayName = ReadString(item, "displayName") ?? ReadString(item, "name");
            var description = ReadString(item, "description");
            var hidden = ReadBoolean(item, "hidden") ?? false;
            if (string.IsNullOrWhiteSpace(displayName) && string.IsNullOrWhiteSpace(description)
                && string.IsNullOrWhiteSpace(icon) && !hidden)
            {
                continue;
            }

            entries.Add(new AchievementRecord(
                property.Name,
                displayName,
                description,
                IsUnlocked: null,
                UnlockTimeUnix: null,
                IconUri: icon,
                IsHidden: hidden));
        }

        return entries;
    }

    private static List<AchievementRecord> ReadObject(JsonElement root)
    {
        var entries = new List<AchievementRecord>();
        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var value = property.Value;
            var unlock = ReadBoolean(value, "earned")
                ?? ReadBoolean(value, "achieved")
                ?? ReadBoolean(value, "unlocked")
                ?? ReadBoolean(value, "state");

            var displayName = ReadString(value, "displayName") ?? ReadString(value, "name");
            var description = ReadString(value, "description");
            var icon = ReadString(value, "icon");
            var hidden = ReadBoolean(value, "hidden") ?? false;
            if (!unlock.HasValue && string.IsNullOrWhiteSpace(displayName)
                && string.IsNullOrWhiteSpace(description) && string.IsNullOrWhiteSpace(icon) && !hidden)
            {
                continue;
            }

            var earnedTime = ReadInt64(value, "earned_time")
                ?? ReadInt64(value, "unlocktime")
                ?? ReadInt64(value, "unlock_time")
                ?? ReadInt64(value, "timestamp");

            entries.Add(new AchievementRecord(
                property.Name,
                displayName,
                description,
                unlock,
                earnedTime,
                icon,
                hidden));
        }

        return entries;
    }

    private static List<AchievementRecord> ReadArray(JsonElement root)
    {
        var entries = new List<AchievementRecord>();
        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var apiName = ReadString(item, "apiname") ?? ReadString(item, "apiName") ?? ReadString(item, "name");
            var unlock = ReadBoolean(item, "earned")
                ?? ReadBoolean(item, "achieved")
                ?? ReadBoolean(item, "unlocked");

            var displayName = ReadLocalizedString(item, "displayName") ?? ReadString(item, "display_name");
            var description = ReadLocalizedString(item, "description");
            var icon = ReadString(item, "icon") ?? ReadString(item, "icon_gray");
            var hidden = ReadBoolean(item, "hidden") ?? false;
            if (string.IsNullOrWhiteSpace(apiName) || (!unlock.HasValue
                && string.IsNullOrWhiteSpace(displayName) && string.IsNullOrWhiteSpace(description)
                && string.IsNullOrWhiteSpace(icon) && !hidden))
            {
                continue;
            }

            entries.Add(new AchievementRecord(
                apiName,
                displayName,
                description,
                unlock,
                ReadInt64(item, "earned_time") ?? ReadInt64(item, "unlocktime") ?? ReadInt64(item, "unlock_time"),
                icon,
                hidden));
        }

        return entries;
    }

    private static string? ReadLocalizedString(JsonElement item, string propertyName)
    {
        if (!TryGetProperty(item, propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return value.ValueKind == JsonValueKind.Object
            ? ReadString(value, "english") ?? ReadString(value, "en")
            : null;
    }

    private static string? ReadString(JsonElement item, string propertyName)
    {
        if (!TryGetProperty(item, propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static bool? ReadBoolean(JsonElement item, string propertyName)
    {
        if (!TryGetProperty(item, propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return value.GetBoolean();
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            return number != 0;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()?.Trim();
            if (bool.TryParse(text, out var parsed))
            {
                return parsed;
            }

            if (string.Equals(text, "1", StringComparison.Ordinal)) return true;
            if (string.Equals(text, "0", StringComparison.Ordinal)) return false;
        }

        return null;
    }

    private static long? ReadInt64(JsonElement item, string propertyName)
    {
        if (!TryGetProperty(item, propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String
            && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
            ? number
            : null;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}
