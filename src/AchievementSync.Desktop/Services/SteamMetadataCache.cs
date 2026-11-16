using System.IO;
using System.Net.Http;
using System.Text.Json;
using AchievementSync.Core.Metadata;

namespace AchievementSync.Desktop.Services;

public sealed class SteamMetadataCache
{
    private static readonly HttpClient Client = CreateClient();
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromDays(30);
    private readonly string _cacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AchievementSync", "Metadata");

    public async Task<CachedSteamMetadata?> GetAsync(string appId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(appId) || !appId.All(char.IsAsciiDigit)) return null;
        Directory.CreateDirectory(_cacheDirectory);
        var cachePath = Path.Combine(_cacheDirectory, appId + ".json");
        var cache = ReadCache(cachePath);
        var cacheLifetime = cache?.Name is null ? TimeSpan.FromHours(6) : RefreshInterval;
        if (cache is not null && DateTimeOffset.UtcNow - cache.LastCheckedUtc < cacheLifetime
            && (cache.ArtworkPath is null || File.Exists(cache.ArtworkPath)))
        {
            return cache.Name is null && cache.ArtworkPath is null ? null : cache;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            var endpoint = $"https://store.steampowered.com/api/appdetails?appids={Uri.EscapeDataString(appId)}&l=english";
            var json = await Client.GetStringAsync(endpoint, timeout.Token).ConfigureAwait(false);
            var metadata = SteamStoreMetadataParser.Parse(appId, json);
            if (metadata is null)
            {
                WriteCache(cachePath, new CacheFile(appId, null, cache?.ArtworkPath, DateTimeOffset.UtcNow));
                return cache?.Name is null && cache?.ArtworkPath is null ? null : cache;
            }

            var artworkPath = cache?.ArtworkPath is { } existing && File.Exists(existing) ? existing : null;
            if (metadata.HeaderImageUri is not null && artworkPath is null)
            {
                try
                {
                    var bytes = await Client.GetByteArrayAsync(metadata.HeaderImageUri, timeout.Token).ConfigureAwait(false);
                    var destination = Path.Combine(_cacheDirectory, appId + ".cover");
                    var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
                    await File.WriteAllBytesAsync(temporary, bytes, timeout.Token).ConfigureAwait(false);
                    File.Move(temporary, destination, overwrite: true);
                    artworkPath = destination;
                }
                catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException)
                {
                    // The game title remains useful if the public artwork CDN is unavailable.
                }
            }

            var result = new CachedSteamMetadata(appId, metadata.Name, artworkPath, DateTimeOffset.UtcNow);
            WriteCache(cachePath, new CacheFile(result.AppId, result.Name, result.ArtworkPath, result.LastCheckedUtc));
            return result;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or OperationCanceledException)
        {
            if (cache is not null) return cache;
            WriteCache(cachePath, new CacheFile(appId, null, null, DateTimeOffset.UtcNow));
            return null;
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AchievementSync/0.1.1 (+desktop game metadata lookup)");
        return client;
    }

    private static CachedSteamMetadata? ReadCache(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var cache = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(path));
            return cache is null
                ? null
                : new CachedSteamMetadata(cache.AppId, cache.Name, cache.ArtworkPath, cache.LastCheckedUtc);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void WriteCache(string path, CacheFile value)
    {
        try
        {
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporary, JsonSerializer.Serialize(value));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Local metadata is optional; the library remains usable without a cache.
        }
    }

    private sealed record CacheFile(string AppId, string? Name, string? ArtworkPath, DateTimeOffset LastCheckedUtc);
}

public sealed record CachedSteamMetadata(string AppId, string? Name, string? ArtworkPath, DateTimeOffset LastCheckedUtc);
