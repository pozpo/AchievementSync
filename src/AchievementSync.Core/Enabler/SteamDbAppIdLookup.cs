using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AchievementSync.Core.Enabler;

/// <summary>Looks up a Steam AppID by game name using the public Steam store and community search APIs.</summary>
public static class SteamDbAppIdLookup
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>Searches the Steam store for a game by name and returns the best-matching AppID.</summary>
    /// <param name="gameName">The game name to search for (typically the folder name or product name).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The matching Steam AppID, or null if no match was found.</returns>
    public static async Task<string?> LookupAppIdAsync(string gameName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameName)) return null;

        var term = gameName.Trim();

        // 1. Primary: Steam Store Search API (store.steampowered.com/api/storesearch/)
        try
        {
            var url = $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(term)}&cc=us&l=en";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AchievementSync/1.0");
            using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(json) && JsonNode.Parse(json) is JsonObject obj)
                {
                    JsonArray? arr = null;
                    if (obj.TryGetPropertyValue("items", out var itemsNode) && itemsNode is JsonArray itemsArr)
                        arr = itemsArr;
                    else if (obj.TryGetPropertyValue("results", out var resultsNode) && resultsNode is JsonArray resultsArr)
                        arr = resultsArr;

                    if (arr is not null)
                    {
                        foreach (var item in arr)
                        {
                            if (item is JsonObject resultObj)
                            {
                                if ((resultObj.TryGetPropertyValue("id", out var idNode) ||
                                     resultObj.TryGetPropertyValue("appid", out idNode) ||
                                     resultObj.TryGetPropertyValue("app_id", out idNode))
                                    && idNode is JsonValue val)
                                {
                                    if (val.TryGetValue(out int intId) && intId > 0)
                                        return intId.ToString();
                                    if (val.TryGetValue(out string? strId) && int.TryParse(strId, out int parsedId) && parsedId > 0)
                                        return parsedId.ToString();
                                }
                            }
                        }
                    }
                }
            }
        }
        catch { }

        // 2. Secondary fallback: Steam Community Search (steamcommunity.com/actions/SearchApps/)
        try
        {
            var communityUrl = $"https://steamcommunity.com/actions/SearchApps/{Uri.EscapeDataString(term)}";
            using var commReq = new HttpRequestMessage(HttpMethod.Get, communityUrl);
            commReq.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AchievementSync/1.0");
            using var commResp = await Http.SendAsync(commReq, cancellationToken).ConfigureAwait(false);
            if (commResp.IsSuccessStatusCode)
            {
                var commJson = await commResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(commJson) && JsonNode.Parse(commJson) is JsonArray commArr)
                {
                    foreach (var item in commArr)
                    {
                        if (item is JsonObject cObj &&
                            (cObj.TryGetPropertyValue("appid", out var cIdNode) ||
                             cObj.TryGetPropertyValue("id", out cIdNode)) &&
                            cIdNode is JsonValue cVal)
                        {
                            if (cVal.TryGetValue(out int cInt) && cInt > 0) return cInt.ToString();
                            if (cVal.TryGetValue(out string? cStr) && int.TryParse(cStr, out int parsed) && parsed > 0) return parsed.ToString();
                        }
                    }
                }
            }
        }
        catch { }

        return null;
    }
}
