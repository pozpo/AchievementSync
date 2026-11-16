using System.Text.Json;

namespace AchievementSync.Core.Metadata;

/// <summary>Reads the public Steam Store appdetails response without treating it as an achievement catalog.</summary>
public static class SteamStoreMetadataParser
{
    public static SteamGameMetadata? Parse(string appId, string json)
    {
        if (string.IsNullOrWhiteSpace(appId) || !appId.All(char.IsAsciiDigit)) return null;

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            if (!TryProperty(document.RootElement, appId, out var app)
                || !TryProperty(app, "success", out var success)
                || success.ValueKind != JsonValueKind.True
                || !TryProperty(app, "data", out var data)) return null;

            var name = TryProperty(data, "name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString()
                : null;
            Uri? headerImage = null;
            if (TryProperty(data, "header_image", out var imageElement)
                && imageElement.ValueKind == JsonValueKind.String
                && Uri.TryCreate(imageElement.GetString(), UriKind.Absolute, out var uri)
                && uri.Scheme == Uri.UriSchemeHttps
                && uri.Host.EndsWith(".steamstatic.com", StringComparison.OrdinalIgnoreCase))
            {
                headerImage = uri;
            }

            return string.IsNullOrWhiteSpace(name) && headerImage is null
                ? null
                : new SteamGameMetadata(appId, name, headerImage);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryProperty(JsonElement element, string name, out JsonElement value)
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
