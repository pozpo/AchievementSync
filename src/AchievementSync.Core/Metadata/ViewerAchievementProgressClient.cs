using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AchievementSync.Core.Models;
using AchievementSync.Core.Parsing;

namespace AchievementSync.Core.Metadata;

public enum ViewerProgressStatus
{
    Found,
    NotConfigured,
    InvalidAppId,
    NotFound,
    MalformedResponse,
    HttpFailure,
    Timeout
}

public sealed record ViewerProgressResult(ViewerProgressStatus Status, ParsedProfile? Progress, string Message)
{
    public bool Success => Status == ViewerProgressStatus.Found && Progress is not null;
}

/// <summary>Reads only the selected public viewer repository's per-game progress JSON; it never uploads local data.</summary>
public sealed class ViewerAchievementProgressClient(HttpClient httpClient, TimeSpan? requestTimeout = null, string? cacheDirectory = null)
{
    private readonly TimeSpan _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(5);
    private readonly string? _cacheDirectory = cacheDirectory;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(24);

    public async Task<ViewerProgressResult> GetProgressAsync(
        string? owner,
        string? repository,
        string? branch,
        string? appId,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repository) || string.IsNullOrWhiteSpace(branch)
            || owner.Contains('/') || repository.Contains('/') || branch.Contains('/')
            || owner.Contains('\\') || repository.Contains('\\') || branch.Contains('\\'))
        {
            return Failure(ViewerProgressStatus.NotConfigured, "Connect a public Achievement Viewer repository to read its saved progress.");
        }
        if (!AppIdValidator.IsValid(appId))
        {
            return Failure(ViewerProgressStatus.InvalidAppId, "This profile does not have a valid numeric AppID for viewer progress lookup.");
        }

        string? cachePath = _cacheDirectory is not null ? GetCachePath(owner, repository, branch, appId) : null;
        var cached = cachePath is not null ? ReadCache(cachePath, owner, repository, branch, appId) : null;
        var cacheAge = cached is null ? TimeSpan.MaxValue : DateTimeOffset.UtcNow - cached.LastCheckedUtc;
        var cacheLifetime = cached?.Progress is null ? TimeSpan.FromHours(1) : CacheLifetime;
        if (!forceRefresh && cached is not null && cacheAge < cacheLifetime)
        {
            return new ViewerProgressResult(cached.Status, cached.Progress, cached.Message);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_requestTimeout);
        try
        {
            var endpoint = new Uri(
                $"https://raw.githubusercontent.com/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}/{Uri.EscapeDataString(branch)}/AppID/{appId}/achievements.json");
            using var response = await httpClient.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                var missing = Failure(ViewerProgressStatus.NotFound, "The configured viewer branch has no saved progress file for this AppID.");
                if (cachePath is not null) WriteCache(cachePath, owner, repository, branch, appId, missing);
                return cached?.Progress is not null
                    ? new ViewerProgressResult(ViewerProgressStatus.Found, cached.Progress, "The viewer is temporarily missing this progress file; using previously cached progress.")
                    : missing;
            }
            if (!response.IsSuccessStatusCode)
            {
                return cached?.Progress is not null
                    ? new ViewerProgressResult(ViewerProgressStatus.Found, cached.Progress, "The viewer returned an error; using previously cached progress.")
                    : Failure(ViewerProgressStatus.HttpFailure, $"The viewer returned HTTP {(int)response.StatusCode} while loading this game's saved progress.");
            }

            var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            var source = $"viewer://{owner}/{repository}/{branch}/AppID/{appId}/achievements.json";
            var parsed = JsonAchievementParser.ParseViewerAchievementProgress(json, appId, source);
            if (parsed.Profile is not null)
            {
                var found = new ViewerProgressResult(ViewerProgressStatus.Found, parsed.Profile,
                    $"Loaded {parsed.Profile.Achievements.Count:N0} saved achievement states from the public viewer.");
                if (cachePath is not null) WriteCache(cachePath, owner, repository, branch, appId, found);
                return found;
            }

            return Failure(ViewerProgressStatus.MalformedResponse, parsed.Error ?? "The viewer progress file is not in a supported format.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return cached?.Progress is not null
                ? new ViewerProgressResult(ViewerProgressStatus.Found, cached.Progress, "The viewer progress request timed out; using previously cached progress.")
                : Failure(ViewerProgressStatus.Timeout, "The viewer progress request timed out.");
        }
        catch (HttpRequestException)
        {
            return cached?.Progress is not null
                ? new ViewerProgressResult(ViewerProgressStatus.Found, cached.Progress, "The configured public viewer could not be reached; using previously cached progress.")
                : Failure(ViewerProgressStatus.HttpFailure, "The configured public viewer could not be reached.");
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
        if (_cacheDirectory is null) return null;
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

    private void WriteCache(string filename, string owner, string repository, string branch, string appId, ViewerProgressResult result)
    {
        if (_cacheDirectory is null) return;
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            var path = Path.Combine(_cacheDirectory, filename);
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            var value = new CacheEntry(owner, repository, branch, appId, DateTimeOffset.UtcNow, result.Progress, result.Status, result.Message);
            File.WriteAllText(temporary, JsonSerializer.Serialize(value));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static ViewerProgressResult Failure(ViewerProgressStatus status, string message) => new(status, null, message);

    private sealed record CacheEntry(
        string Owner,
        string Repository,
        string Branch,
        string AppId,
        DateTimeOffset LastCheckedUtc,
        ParsedProfile? Progress,
        ViewerProgressStatus Status,
        string Message);
}
