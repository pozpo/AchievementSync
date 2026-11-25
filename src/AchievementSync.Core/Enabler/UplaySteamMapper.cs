using System.Text.Json;
using System.Text.Json.Nodes;

namespace AchievementSync.Core.Enabler;

/// <summary>Maps Ubisoft Connect (Uplay) game IDs to Steam AppIDs using the embedded mapping table.</summary>
public static class UplaySteamMapper
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> _map = new(LoadMap);

    /// <summary>Looks up a Steam AppID for a given Uplay ID.</summary>
    /// <param name="uplayId">The Ubisoft Connect game ID.</param>
    /// <returns>The matching Steam AppID string, or null if no mapping exists.</returns>
    public static string? LookupSteamAppId(string uplayId)
    {
        if (string.IsNullOrWhiteSpace(uplayId)) return null;
        return _map.Value.TryGetValue(uplayId.Trim(), out var steamAppId) ? steamAppId : null;
    }

    private static IReadOnlyDictionary<string, string> LoadMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var assembly = typeof(UplaySteamMapper).Assembly;
        using var stream = assembly.GetManifestResourceStream(
            "AchievementSync.Core.Resources.uplay-steam.json");
        if (stream is null) return map;

        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        if (JsonNode.Parse(json) is not JsonArray arr) return map;

        foreach (var item in arr)
        {
            if (item is not JsonObject obj) continue;
            var uplayId = obj["uplay_id"]?.ToString();
            var steamAppId = obj["steam_appid"]?.ToString();
            if (!string.IsNullOrWhiteSpace(uplayId) && !string.IsNullOrWhiteSpace(steamAppId) &&
                steamAppId != "null")
            {
                map[uplayId] = steamAppId;
            }
        }

        return map;
    }
}
