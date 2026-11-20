using System.Globalization;
using System.IO;
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
            var json = File.ReadAllText(path);
            if (string.Equals(Path.GetFileName(path), "game-info.json", StringComparison.OrdinalIgnoreCase))
            {
                return ParseViewerGameInfo(json, appId, path);
            }

            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 64
            });

            var entries = document.RootElement.ValueKind switch
            {
                JsonValueKind.Object => ReadObject(document.RootElement, path, appId),
                JsonValueKind.Array => ReadArray(document.RootElement, path, appId),
                _ => new List<AchievementRecord>()
            };

            if (entries.Count == 0
                && !(document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.EnumerateObject().Count() == 0
                    && string.Equals(Path.GetFileName(path), "achievements.json", StringComparison.OrdinalIgnoreCase)))
            {
                return ParseResult.Unsupported("The JSON file does not contain recognizable unlock-state fields.");
            }

            var hasOnlyExplicitStates = entries.Count > 0 && entries.All(item => item.IsUnlocked.HasValue);
            var isDatabaseSnapshot = string.Equals(Path.GetExtension(path), ".db", StringComparison.OrdinalIgnoreCase);
            var completeCatalog = entries.Count > 0 &&
                ((hasOnlyExplicitStates && isDatabaseSnapshot)
                    || entries.All(item => !string.IsNullOrWhiteSpace(item.DisplayName)
                        || !string.IsNullOrWhiteSpace(item.Description)
                        || !string.IsNullOrWhiteSpace(item.IconUri)
                        || item.IsHidden));

            var fileTime = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
            return ParseResult.Parsed(new ParsedProfile(
                appId,
                FormatName,
                path,
                fileTime,
                entries,
                HasCompleteCatalog: completeCatalog));
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

    public static ParseResult ParseViewerGameInfo(string json, string appId, string filePath = "game-info.json")
    {
        if (!IsAppId(appId)) return ParseResult.Unsupported("The AppID is missing or invalid.");

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !TryGetProperty(document.RootElement, "achievements", out var catalog)
                || catalog.ValueKind != JsonValueKind.Object)
            {
                return ParseResult.Unsupported("Viewer game metadata does not contain an achievement catalog.");
            }

            if (TryGetProperty(document.RootElement, "appid", out var fileAppId)
                && fileAppId.ValueKind == JsonValueKind.String
                && !string.Equals(fileAppId.GetString(), appId, StringComparison.Ordinal))
            {
                return ParseResult.Unsupported("Viewer game metadata belongs to a different AppID.");
            }

            var entries = ReadSchemaObject(catalog, filePath, appId);
            var title = ReadString(document.RootElement, "name");
            return ParseResult.Parsed(new ParsedProfile(
                appId,
                "Achievement Viewer game-info catalog",
                filePath,
                DateTimeOffset.UtcNow,
                entries,
                HasCompleteCatalog: true,
                Title: title));
        }
        catch (JsonException)
        {
            return ParseResult.Unsupported("Viewer game metadata is not valid JSON.");
        }
    }

    /// <summary>Parses the public viewer's per-game progress file. Missing catalog entries are locked by viewer contract.</summary>
    public static ParseResult ParseViewerAchievementProgress(string json, string appId, string filePath)
    {
        if (!IsAppId(appId)) return ParseResult.Unsupported("The AppID is missing or invalid.");

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return ParseResult.Unsupported("Viewer achievement progress must be a JSON object keyed by achievement identifier.");
            }

            var entries = ReadObject(document.RootElement, filePath, appId);
            if (entries.Count == 0 && document.RootElement.EnumerateObject().Any())
            {
                return ParseResult.Unsupported("Viewer achievement progress does not contain recognizable state fields.");
            }

            return ParseResult.Parsed(new ParsedProfile(
                appId,
                "Achievement Viewer public progress",
                filePath,
                DateTimeOffset.UtcNow,
                entries,
                HasCompleteCatalog: false,
                UnlistedAchievementsAreLocked: true));
        }
        catch (JsonException)
        {
            return ParseResult.Unsupported("Viewer achievement progress is not valid JSON.");
        }
    }

    private static List<AchievementRecord> ReadSchemaObject(JsonElement root, string? path, string appId)
    {
        var entries = new List<AchievementRecord>();
        if (root.ValueKind != JsonValueKind.Object) return entries;

        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object) continue;
            var item = property.Value;
            var icon = AchievementIconResolver.Resolve(ReadString(item, "icon"), path, appId);
            if (string.IsNullOrWhiteSpace(icon))
                icon = AchievementIconResolver.Resolve(ReadString(item, "icongray") ?? ReadString(item, "icon_gray"), path, appId);
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

    private static List<AchievementRecord> ReadObject(JsonElement root, string? path, string appId)
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
            var icon = AchievementIconResolver.Resolve(ReadString(value, "icon"), path, appId);
            if (string.IsNullOrWhiteSpace(icon))
                icon = AchievementIconResolver.Resolve(ReadString(value, "icon_gray") ?? ReadString(value, "icongray"), path, appId);
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

    private static List<AchievementRecord> ReadArray(JsonElement root, string? path, string appId)
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
            var icon = AchievementIconResolver.Resolve(ReadString(item, "icon"), path, appId);
            if (string.IsNullOrWhiteSpace(icon))
                icon = AchievementIconResolver.Resolve(ReadString(item, "icon_gray") ?? ReadString(item, "icongray"), path, appId);
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

        if (value.ValueKind != JsonValueKind.Object) return null;
        var preferred = ReadString(value, "english") ?? ReadString(value, "en");
        if (!string.IsNullOrWhiteSpace(preferred)) return preferred;
        foreach (var property in value.EnumerateObject())
        {
            if (string.Equals(property.Name, "token", StringComparison.OrdinalIgnoreCase)
                || property.Value.ValueKind != JsonValueKind.String) continue;
            var localized = property.Value.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(localized)) return localized;
        }
        return null;
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

    private static bool IsAppId(string value) =>
        value.Length is >= 1 and <= 10 && value.All(char.IsAsciiDigit) && value.Any(character => character != '0');

}
