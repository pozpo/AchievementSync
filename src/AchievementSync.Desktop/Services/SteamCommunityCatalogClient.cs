using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using AchievementSync.Core.Metadata;
using AchievementSync.Core.Models;

namespace AchievementSync.Desktop.Services;

public sealed record SteamCommunityCatalogResult(ParsedProfile Catalog, int? DeclaredTotal);

/// <summary>Reads public achievement definitions. The only persistent result is Metadata/&lt;AppID&gt;/catalog.json.</summary>
public sealed class SteamCommunityCatalogClient
{
    private const int MaximumPageBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(30);
    private static readonly TimeSpan FailedRefreshInterval = TimeSpan.FromHours(6);
    private static readonly SemaphoreSlim RequestSlots = new(2, 2);
    private static readonly Dictionary<string, DateTimeOffset> FailedFetches = new(StringComparer.Ordinal);
    private static readonly object FailedFetchGate = new();
    private static readonly Regex ImageTagRegex = new("<img\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex PageTitleRegex = new("<title\\b[^>]*>(?<title>[\\s\\S]*?)</title>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex CountRegex = new("\\b(?<count>\\d{1,5})\\s+achievements\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex HeadingTagRegex = new("<h[1-6]\\b[^>]*>(?<text>[\\s\\S]*?)</h[1-6]>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex DescriptionTagRegex = new("<h5\\b[^>]*>(?<text>[\\s\\S]*?)</h5>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex DescriptionClassRegex = new("<(?:div|p|span)\\b[^>]*class=[\\\"'][^\\\"']*(?:description|desc)[^\\\"']*[\\\"'][^>]*>(?<text>[\\s\\S]*?)</(?:div|p|span)>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex SubtitleSpanRegex = new("<span\\b[^>]*>(?<text>[\\s\\S]*?)</span>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex TextTagRegex = new("<[^>]+>", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly HttpClient Http = CreateHttpClient();
    private readonly string _metadataRoot;
    private readonly LocalAchievementCatalogCache _catalogCache;

    public SteamCommunityCatalogClient(string? metadataRoot = null)
    {
        _metadataRoot = Path.GetFullPath(metadataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AchievementSync", "Metadata"));
        _catalogCache = new LocalAchievementCatalogCache(_metadataRoot);
    }

    public async Task<SteamCommunityCatalogResult?> GetCatalogAsync(
        string appId,
        string? expectedTitle = null,
        CancellationToken cancellationToken = default)
    {
        if (!AppIdValidator.IsValid(appId)) return null;
        AppMetadataLayout.RemoveLegacyFiles(_metadataRoot, appId);
        var cached = _catalogCache.LoadCatalog(appId);
        var now = DateTimeOffset.UtcNow;
        var cacheUsable = IsUsableCommunityCache(cached, expectedTitle);
        if (IsFreshCommunityCache(cached, expectedTitle) && now - cached!.CommunityFetchedAtUtc!.Value < CacheLifetime)
            return ToResult(cached);
        if (IsFailureCoolingDown(appId, now) && IsFreshCommunityCache(cached, expectedTitle))
            return ToResult(cached!);

        await RequestSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another game-load task may have populated this AppID while we waited.
            cached = _catalogCache.LoadCatalog(appId);
            cacheUsable = IsUsableCommunityCache(cached, expectedTitle);
            now = DateTimeOffset.UtcNow;
            if (IsFreshCommunityCache(cached, expectedTitle) && now - cached!.CommunityFetchedAtUtc!.Value < CacheLifetime)
                return ToResult(cached);
            if (IsFailureCoolingDown(appId, now) && IsFreshCommunityCache(cached, expectedTitle))
                return ToResult(cached!);

            var page = await FetchPageAsync($"https://steamcommunity.com/achievements/{appId}/?l=english", cancellationToken)
                .ConfigureAwait(false);
            var result = page is null ? null : ParsePage(appId, page);
            if (result is null || result.Achievements.Count == 0)
            {
                page = await FetchPageAsync($"https://steamcommunity.com/stats/{appId}/achievements/?l=english", cancellationToken)
                    .ConfigureAwait(false);
                result = page is null ? null : ParsePage(appId, page);
            }

            if (result is null || result.Achievements.Count == 0
                || !IsTitleCompatible(expectedTitle, result.Title))
            {
                AppLog.Info(result is null || result.Achievements.Count == 0
                    ? $"Steam Community returned no readable achievement rows for AppID {appId}."
                    : $"Steam Community catalog title did not match AppID {appId}; expected '{expectedTitle}', received '{result.Title}'.");
                SetFailureCooldown(appId);
                return cacheUsable ? ToResult(cached!) : null;
            }

            lock (FailedFetchGate) FailedFetches.Remove(appId);
            var catalog = new ParsedProfile(
                appId,
                "Steam Community achievement catalog",
                Path.Combine(_metadataRoot, appId, "catalog.json"),
                DateTimeOffset.UtcNow,
                result.Achievements,
                result.DeclaredTotal is > 0 && result.Achievements.Count >= result.DeclaredTotal.Value,
                result.Title,
                DeclaredCatalogTotal: result.DeclaredTotal);
            AppLog.Info($"Steam Community catalog parsed for AppID {appId}; entries={result.Achievements.Count}; declaredTotal={result.DeclaredTotal?.ToString() ?? "unknown"}.");
            return new SteamCommunityCatalogResult(catalog, result.DeclaredTotal);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException
                                           or JsonException or InvalidOperationException or TaskCanceledException)
        {
            AppLog.Info($"Steam Community achievement data was unavailable for AppID {appId}: {exception.GetType().Name}.");
            SetFailureCooldown(appId);
            return cacheUsable ? ToResult(cached!) : null;
        }
        finally
        {
            RequestSlots.Release();
        }
    }

    private static bool IsUsableCommunityCache(ParsedProfile? cached, string? expectedTitle) =>
        cached?.CommunityFetchedAtUtc is not null
        && cached.Achievements.Count > 0
        && IsTitleCompatible(expectedTitle, cached.Title);

    private static bool IsFreshCommunityCache(ParsedProfile? cached, string? expectedTitle) =>
        IsUsableCommunityCache(cached, expectedTitle)
        && cached!.Achievements.All(achievement =>
            (achievement.IsHidden || !string.IsNullOrWhiteSpace(achievement.Description))
            && LocalAchievementCatalogCache.IsValidMetadataImageUri(achievement.IconUri));

    private static bool IsFailureCoolingDown(string appId, DateTimeOffset now)
    {
        lock (FailedFetchGate)
            return FailedFetches.TryGetValue(appId, out var failedAt) && now - failedAt < FailedRefreshInterval;
    }

    private static void SetFailureCooldown(string appId)
    {
        lock (FailedFetchGate) FailedFetches[appId] = DateTimeOffset.UtcNow;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(6),
            AutomaticDecompression = System.Net.DecompressionMethods.GZip
                | System.Net.DecompressionMethods.Deflate
                | System.Net.DecompressionMethods.Brotli
        })
        {
            Timeout = TimeSpan.FromSeconds(12)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AchievementSync/1.3.0 (+achievement catalog cache)");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        return client;
    }

    private static async Task<string?> FetchPageAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > MaximumPageBytes) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[32 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (output.Length + read > MaximumPageBytes) return null;
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return System.Text.Encoding.UTF8.GetString(output.ToArray());
    }

    private static ParsedCommunityCatalog? ParsePage(string appId, string html)
    {
        var titleMatch = PageTitleRegex.Match(html);
        var pageTitle = titleMatch.Success ? DecodeText(titleMatch.Groups["title"].Value) : null;
        var gameTitle = ExtractGameTitle(pageTitle);
        var totalMatch = CountRegex.Match(html);
        int? declaredTotal = totalMatch.Success && int.TryParse(totalMatch.Groups["count"].Value, out var total)
            ? total
            : null;

        var structuredAchievements = ParseStructuredAchievements(appId, html);
        if (structuredAchievements.Count > 0)
        {
            declaredTotal ??= structuredAchievements.Count;
            return new ParsedCommunityCatalog(gameTitle, declaredTotal, structuredAchievements);
        }

        var achievements = new List<AchievementRecord>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match imageMatch in ImageTagRegex.Matches(html))
        {
            var imageTag = imageMatch.Value;
            var imageUrl = ReadAttribute(imageTag, "src") ?? ReadAttribute(imageTag, "data-src");
            var iconUri = BuildCommunityIconUri(appId, imageUrl);
            if (iconUri is null) continue;
            var altName = DecodeText(ReadAttribute(imageTag, "alt") ?? string.Empty);

            var contentStart = imageMatch.Index + imageMatch.Length;
            var nextImageStart = html.IndexOf("<img", contentStart, StringComparison.OrdinalIgnoreCase);
            var contentEnd = nextImageStart >= 0 ? nextImageStart : html.Length;
            if (contentEnd - contentStart > 6000) contentEnd = contentStart + 6000;
            var content = html[contentStart..contentEnd];
            var heading = HeadingTagRegex.Match(content);
            var name = heading.Success ? DecodeText(heading.Groups["text"].Value) : altName;
            if (string.IsNullOrWhiteSpace(name)) continue;
            var normalizedName = AchievementIdentity.NormalizeDisplayName(name);
            if (normalizedName.Length == 0 || !seenNames.Add(normalizedName)) continue;

            var description = string.Empty;
            if (heading.Success)
            {
                var afterHeading = content[(heading.Index + heading.Length)..];
                var descriptionMatch = DescriptionTagRegex.Match(afterHeading);
                if (!descriptionMatch.Success) descriptionMatch = DescriptionClassRegex.Match(afterHeading);
                if (!descriptionMatch.Success) descriptionMatch = SubtitleSpanRegex.Match(afterHeading);
                if (descriptionMatch.Success) description = DecodeText(descriptionMatch.Groups["text"].Value);
            }

            achievements.Add(new AchievementRecord(
                AchievementIdentity.FromDisplayName(name),
                name,
                string.IsNullOrWhiteSpace(description) ? null : description,
                IsUnlocked: null,
                UnlockTimeUnix: null,
                IconUri: iconUri.AbsoluteUri));
        }

        return achievements.Count == 0 ? null : new ParsedCommunityCatalog(gameTitle, declaredTotal, achievements);
    }

    private static IReadOnlyList<AchievementRecord> ParseStructuredAchievements(string appId, string html)
    {
        // Steam's current Community page embeds the full achievement schema in its server-rendered data.
        // The visible <img> tags expose only the locked icon and omit the real API identifier.
        var decoded = html.Replace("\\\"", "\"", StringComparison.Ordinal);
        var match = Regex.Match(decoded, "\\\"achievements\\\"\\s*:\\s*\\[",
            RegexOptions.CultureInvariant);
        if (!match.Success) return [];
        var arrayStart = decoded.IndexOf('[', match.Index + match.Length - 1);
        if (arrayStart < 0 || !TryFindJsonArrayEnd(decoded, arrayStart, out var arrayEnd)) return [];

        try
        {
            using var document = JsonDocument.Parse(decoded[arrayStart..(arrayEnd + 1)]);
            var achievements = new List<AchievementRecord>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var displayName = ReadJsonString(item, "name");
                var apiName = ReadJsonString(item, "api_name");
                if (string.IsNullOrWhiteSpace(displayName)) continue;
                if (string.IsNullOrWhiteSpace(apiName)) apiName = AchievementIdentity.FromDisplayName(displayName);

                var earnedIcon = BuildCommunityIconUri(appId, ReadJsonString(item, "icon_achieved"));
                var lockedIcon = BuildCommunityIconUri(appId, ReadJsonString(item, "icon_unachieved"));
                var icon = earnedIcon ?? lockedIcon;
                var hidden = item.TryGetProperty("hidden", out var hiddenValue)
                    && hiddenValue.ValueKind is JsonValueKind.True;
                achievements.Add(new AchievementRecord(
                    apiName,
                    System.Net.WebUtility.HtmlDecode(displayName),
                    System.Net.WebUtility.HtmlDecode(ReadJsonString(item, "description") ?? string.Empty),
                    IsUnlocked: null,
                    UnlockTimeUnix: null,
                    IconUri: icon?.AbsoluteUri,
                    IsHidden: hidden,
                    LockedIconUri: lockedIcon?.AbsoluteUri));
            }
            return achievements;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static bool TryFindJsonArrayEnd(string json, int arrayStart, out int arrayEnd)
    {
        arrayEnd = -1;
        if (arrayStart < 0 || arrayStart >= json.Length || json[arrayStart] != '[') return false;

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var index = arrayStart; index < json.Length; index++)
        {
            var character = json[index];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (character == '\\') escaped = true;
                else if (character == '"') inString = false;
                continue;
            }

            if (character == '"') inString = true;
            else if (character == '[') depth++;
            else if (character == ']' && --depth == 0)
            {
                arrayEnd = index;
                return true;
            }
        }

        return false;
    }

    private static Uri? BuildCommunityIconUri(string appId, string? image)
    {
        if (string.IsNullOrWhiteSpace(image)) return null;
        var candidate = image.StartsWith("//", StringComparison.Ordinal)
            ? "https:" + image
            : image.StartsWith("/", StringComparison.Ordinal)
                ? "https://shared.fastly.steamstatic.com" + image
                : Uri.TryCreate(image, UriKind.Absolute, out var absolute)
                    && absolute.Scheme is "http" or "https"
                    ? image
                    : $"https://shared.fastly.steamstatic.com/community_assets/images/apps/{appId}/{Path.GetFileName(image)}";
        return TryGetCommunityIconUri(appId, candidate, out var uri) ? uri : null;
    }

    private static string? ReadJsonString(JsonElement item, string propertyName) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool TryGetCommunityIconUri(string appId, string? imageUrl, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps) return false;
        var validHost = parsed.Host.EndsWith(".steamstatic.com", StringComparison.OrdinalIgnoreCase)
            || parsed.Host.EndsWith(".akamaihd.net", StringComparison.OrdinalIgnoreCase);
        var path = parsed.AbsolutePath;
        var isCommunityAchievementAsset = path.StartsWith(
                $"/community_assets/images/apps/{appId}/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith($"/steamcommunity/public/images/apps/{appId}/", StringComparison.OrdinalIgnoreCase);
        if (!validHost || !isCommunityAchievementAsset) return false;
        uri = parsed;
        return true;
    }

    private static string? ReadAttribute(string tag, string attributeName)
    {
        var match = Regex.Match(tag, $"\\b{Regex.Escape(attributeName)}\\s*=\\s*([\"'])(?<value>.*?)\\1",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["value"].Value : null;
    }

    private static string DecodeText(string value)
    {
        var withoutTags = TextTagRegex.Replace(value, " ");
        return System.Net.WebUtility.HtmlDecode(withoutTags).Trim();
    }

    private static string? ExtractGameTitle(string? pageTitle)
    {
        if (string.IsNullOrWhiteSpace(pageTitle)) return null;
        var parts = pageTitle.Split("::", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 ? parts[^2] : pageTitle;
    }

    private static bool IsTitleCompatible(string? expectedTitle, string? sourceTitle)
    {
        if (string.IsNullOrWhiteSpace(expectedTitle) || AppIdValidator.IsValid(expectedTitle)) return true;
        if (string.IsNullOrWhiteSpace(sourceTitle)) return false;
        var expected = AchievementIdentity.NormalizeDisplayName(expectedTitle);
        var source = AchievementIdentity.NormalizeDisplayName(sourceTitle);
        return expected.Length < 4 || source.Length < 4 || expected.Contains(source, StringComparison.Ordinal)
            || source.Contains(expected, StringComparison.Ordinal);
    }

    private static SteamCommunityCatalogResult ToResult(ParsedProfile catalog) =>
        new(catalog, catalog.DeclaredCatalogTotal);

    private sealed record ParsedCommunityCatalog(string? Title, int? DeclaredTotal, IReadOnlyList<AchievementRecord> Achievements);
}
