using System.Globalization;
using System.Text.Json;

namespace AchievementSync.Core.Platforms;

/// <summary>RetroAchievements reader using the official web API. Requires a username and API key
/// configured in Settings; no request is made until both are present.</summary>
public sealed class RetroAchievementsDataAdapter(HttpClient? httpClient = null)
    : PlatformDataAdapter("retroachievements", "RetroAchievements")
{
    private const string ApiRoot = "https://retroachievements.org/API";
    private readonly HttpClient? _httpClient = httpClient;

    public override bool IsConfigured => false;

    public override bool IsConfiguredFor(PlatformSourceOptions options) =>
        _httpClient is not null
        && !string.IsNullOrWhiteSpace(options.RetroAchievementsUser)
        && !string.IsNullOrWhiteSpace(options.RetroAchievementsApiKey);

    public override string? ConfigurationHint =>
        "Add your RetroAchievements username and API key on the Platforms page.";

    public override async Task<PlatformCollectResult> CollectAsync(PlatformSourceOptions options, CancellationToken cancellationToken = default)
    {
        if (_httpClient is null || string.IsNullOrWhiteSpace(options.RetroAchievementsUser)
            || string.IsNullOrWhiteSpace(options.RetroAchievementsApiKey))
        {
            return new PlatformCollectResult(Platform, [],
                ["RetroAchievements needs a username and API key before it can run."]);
        }

        var messages = new List<string>();
        var games = new List<PlatformGameSnapshot>();
        var progressUrl = $"{ApiRoot}/API_GetUserCompletionProgress.php?u={Uri.EscapeDataString(options.RetroAchievementsUser)}"
                          + $"&k={Uri.EscapeDataString(options.RetroAchievementsApiKey)}&c=100";
        try
        {
            using var progressDocument = JsonDocument.Parse(await GetStringAsync(progressUrl, cancellationToken).ConfigureAwait(false));
            if (progressDocument.RootElement.TryGetProperty("Results", out var results)
                && results.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in results.EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!item.TryGetProperty("GameID", out var idValue) || !idValue.TryGetInt32(out var gameId)) continue;
                    var numAwarded = item.TryGetProperty("NumAwarded", out var awarded) && awarded.TryGetInt32(out var value) ? value : 0;
                    if (numAwarded <= 0) continue;
                    var title = item.TryGetProperty("Title", out var titleValue) ? titleValue.GetString() : null;
                    try
                    {
                        var snapshot = await FetchGameAsync(gameId, title, options, cancellationToken).ConfigureAwait(false);
                        if (snapshot is not null) games.Add(snapshot);
                    }
                    catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
                    {
                        messages.Add($"RetroAchievements: game {gameId} could not be read ({exception.GetType().Name}).");
                    }
                }
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            messages.Add($"RetroAchievements: the API request failed ({exception.GetType().Name}).");
        }

        return new PlatformCollectResult(Platform, games, messages);
    }

    private async Task<PlatformGameSnapshot?> FetchGameAsync(int gameId, string? title,
        PlatformSourceOptions options, CancellationToken cancellationToken)
    {
        var url = $"{ApiRoot}/API_GetGameInfoAndUserProgress.php?u={Uri.EscapeDataString(options.RetroAchievementsUser!)}"
                  + $"&k={Uri.EscapeDataString(options.RetroAchievementsApiKey!)}&g={gameId}";
        using var document = JsonDocument.Parse(await GetStringAsync(url, cancellationToken).ConfigureAwait(false));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Achievements", out var achievements)
            || achievements.ValueKind != JsonValueKind.Object) return null;
        var effectiveTitle = title;
        if (string.IsNullOrWhiteSpace(effectiveTitle) && root.TryGetProperty("Title", out var titleValue))
            effectiveTitle = titleValue.GetString();

        var entries = new List<PlatformAchievement>();
        foreach (var property in achievements.EnumerateObject())
        {
            var achievement = property.Value;
            if (achievement.ValueKind != JsonValueKind.Object) continue;
            var dateEarned = achievement.TryGetProperty("DateEarned", out var dateValue) ? dateValue.GetString() : null;
            var earned = !string.IsNullOrWhiteSpace(dateEarned);
            long earnedTime = 0;
            if (earned && DateTimeOffset.TryParse(dateEarned, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
                earnedTime = parsed.ToUnixTimeSeconds();
            var rarity = achievement.TryGetProperty("Rarity", out var rarityValue) && rarityValue.TryGetDouble(out var percent)
                ? percent : (double?)null;
            entries.Add(new PlatformAchievement(
                property.Name,
                achievement.TryGetProperty("Title", out var nameValue) ? nameValue.GetString() : null,
                achievement.TryGetProperty("Description", out var descriptionValue) ? descriptionValue.GetString() : null,
                earned,
                earnedTime,
                RarityPercent: rarity));
        }

        return entries.Count == 0
            ? null
            : new PlatformGameSnapshot(Platform, gameId.ToString(CultureInfo.InvariantCulture), effectiveTitle, url, entries);
    }

    private async Task<string> GetStringAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await _httpClient!.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }
}
