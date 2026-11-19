using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AchievementSync.Core.Models;
using AchievementSync.Core.Parsing;

namespace AchievementSync.Core.Metadata;

public enum ViewerCatalogStatus
{
    Found,
    NotConfigured,
    InvalidAppId,
    NotFound,
    MissingCatalog,
    MalformedResponse,
    HttpFailure,
    Timeout
}

public sealed record ViewerCatalogResult(ViewerCatalogStatus Status, ParsedProfile? Catalog, string Message)
{
    public bool Success => Status == ViewerCatalogStatus.Found && Catalog is not null;
}

/// <summary>Reads generated game-info catalogs from a configured public viewer fork. It never sends player progress.</summary>
public sealed class ViewerGameInfoClient(HttpClient httpClient, TimeSpan? requestTimeout = null, string? cacheDirectory = null)
{
    private readonly TimeSpan _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(5);
    private readonly string _cacheDirectory = cacheDirectory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AchievementSync", "Metadata", "ViewerCatalog");
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(24);

    public async Task<ViewerCatalogResult> GetCatalogAsync(
        string? owner,
        string? repository,
        string? branch,
        string? appId,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repository) || string.IsNullOrWhiteSpace(branch))
        {
            return Failure(ViewerCatalogStatus.NotConfigured, "Connect a public Achievement Viewer repository to load its generated game catalog.");
        }
        if (owner.Contains('/') || repository.Contains('/') || branch.Contains('/') || owner.Contains('\\') || repository.Contains('\\') || branch.Contains('\\'))
        {
            return Failure(ViewerCatalogStatus.NotConfigured, "The configured viewer owner, repository, or branch is invalid.");
        }
        if (!AppIdValidator.IsValid(appId))
        {
            return Failure(ViewerCatalogStatus.InvalidAppId, "This profile does not have a valid numeric AppID for viewer metadata lookup.");
        }

        var cachePath = GetCachePath(owner, repository, branch, appId);
        var cached = ReadCache(cachePath, owner, repository, branch, appId);
        var cacheAge = cached is null ? TimeSpan.MaxValue : DateTimeOffset.UtcNow - cached.LastCheckedUtc;
        var cacheLifetime = cached?.Catalog is null ? TimeSpan.FromHours(1) : CacheLifetime;
        if (!forceRefresh && cached is not null && cacheAge < cacheLifetime)
        {
            return new ViewerCatalogResult(cached.Status, cached.Catalog, cached.Message);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_requestTimeout);
        try
        {
            var endpoint = new Uri(
                $"https://raw.githubusercontent.com/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}/{Uri.EscapeDataString(branch)}/AppID/{appId}/game-info.json");
            using var response = await httpClient.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                var missing = Failure(ViewerCatalogStatus.NotFound, "The configured viewer branch has no generated catalog for this AppID.");
                WriteCache(cachePath, owner, repository, branch, appId, missing);
                return cached?.Catalog is not null ? new ViewerCatalogResult(ViewerCatalogStatus.Found, cached.Catalog,
                    "The viewer is temporarily missing this catalog; using the previously cached catalog.") : missing;
            }
            if (!response.IsSuccessStatusCode)
            {
                return cached?.Catalog is not null ? new ViewerCatalogResult(ViewerCatalogStatus.Found, cached.Catalog,
                    "The viewer returned an error; using the previously cached catalog.")
                    : Failure(ViewerCatalogStatus.HttpFailure, $"The viewer returned HTTP {(int)response.StatusCode} while loading this game's catalog.");
            }

            var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            var parsed = JsonAchievementParser.ParseViewerGameInfo(json, appId,
                $"viewer://{owner}/{repository}/{branch}/AppID/{appId}/game-info.json");
            if (parsed.Profile is not null)
            {
                var found = new ViewerCatalogResult(ViewerCatalogStatus.Found, parsed.Profile,
                    parsed.Profile.Achievements.Count == 0
                        ? "The viewer catalog is valid and contains no achievements."
                        : $"Loaded {parsed.Profile.Achievements.Count:N0} achievements from the configured viewer catalog.");
                WriteCache(cachePath, owner, repository, branch, appId, found);
                return found;
            }

            var status = parsed.Error?.Contains("not valid JSON", StringComparison.OrdinalIgnoreCase) == true
                ? ViewerCatalogStatus.MalformedResponse
                : ViewerCatalogStatus.MissingCatalog;
            var failure = Failure(status, parsed.Error ?? "The viewer response did not contain a usable catalog.");
            return cached?.Catalog is not null ? new ViewerCatalogResult(ViewerCatalogStatus.Found, cached.Catalog,
                "The viewer catalog could not be refreshed; using the previously cached catalog.") : failure;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return cached?.Catalog is not null ? new ViewerCatalogResult(ViewerCatalogStatus.Found, cached.Catalog,
                "The viewer catalog request timed out; using the previously cached catalog.")
                : Failure(ViewerCatalogStatus.Timeout, "The viewer catalog request timed out.");
        }
        catch (HttpRequestException)
        {
            return cached?.Catalog is not null ? new ViewerCatalogResult(ViewerCatalogStatus.Found, cached.Catalog,
                "The viewer could not be reached; using the previously cached catalog.")
                : Failure(ViewerCatalogStatus.HttpFailure, "The configured public viewer could not be reached.");
        }
        catch (JsonException)
        {
            return cached?.Catalog is not null ? new ViewerCatalogResult(ViewerCatalogStatus.Found, cached.Catalog,
                "The viewer returned malformed catalog JSON; using the previously cached catalog.")
                : Failure(ViewerCatalogStatus.MalformedResponse, "The viewer returned malformed catalog JSON.");
        }
    }

    private static string GetCachePath(string owner, string repository, string branch, string appId)
    {
        var key = $"{owner.ToLowerInvariant()}/{repository.ToLowerInvariant()}/{branch.ToLowerInvariant()}/{appId}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return hash + ".json";
    }

    private CacheEntry? ReadCache(string filename, string owner, string repository, string branch, string appId)
    {
        try
        {
            var path = Path.Combine(_cacheDirectory, filename);
            if (!File.Exists(path)) return null;
            var entry = JsonSerializer.Deserialize<CacheEntry>(File.ReadAllText(path));
            return entry is not null
                && string.Equals(entry.Owner, owner, StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.Repository, repository, StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.Branch, branch, StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.AppId, appId, StringComparison.Ordinal)
                    ? entry
                    : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private void WriteCache(string filename, string owner, string repository, string branch, string appId, ViewerCatalogResult result)
    {
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            var path = Path.Combine(_cacheDirectory, filename);
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            var value = new CacheEntry(owner, repository, branch, appId, DateTimeOffset.UtcNow, result.Catalog, result.Status, result.Message);
            File.WriteAllText(temporary, JsonSerializer.Serialize(value));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A cache failure does not make public game metadata unavailable.
        }
    }

    private static ViewerCatalogResult Failure(ViewerCatalogStatus status, string message) => new(status, null, message);

    private sealed record CacheEntry(
        string Owner,
        string Repository,
        string Branch,
        string AppId,
        DateTimeOffset LastCheckedUtc,
        ParsedProfile? Catalog,
        ViewerCatalogStatus Status,
        string Message);
}
