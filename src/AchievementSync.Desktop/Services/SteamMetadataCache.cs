using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Media.Imaging;
using AchievementSync.Core.Metadata;

namespace AchievementSync.Desktop.Services;

public sealed class SteamMetadataCache
{
    private static readonly HttpClient DefaultClient = CreateClient();
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromDays(30);
    private readonly HttpClient _client;
    private readonly string _cacheDirectory;

    public SteamMetadataCache(string? cacheDirectory = null, HttpClient? httpClient = null)
    {
        _client = httpClient ?? DefaultClient;
        _cacheDirectory = cacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AchievementSync", "Metadata");
    }

    public async Task<CachedSteamMetadata?> GetAsync(string appId, CancellationToken cancellationToken = default)
    {
        if (!AppIdValidator.IsValid(appId)) return null;
        var appDirectory = Path.Combine(_cacheDirectory, appId);
        Directory.CreateDirectory(appDirectory);
        var cachePath = Path.Combine(appDirectory, "steam.json");
        var cache = ReadCache(cachePath) ?? ReadLegacyCache(appId, appDirectory);
        if (cache?.ArtworkPath is { } cachedArtwork && File.Exists(cachedArtwork) && !IsValidArtworkFile(cachedArtwork))
        {
            cache = cache with { ArtworkPath = null };
        }
        var cacheLifetime = cache?.Name is null ? TimeSpan.FromHours(6)
            : cache.AchievementsTotal is null ? TimeSpan.FromHours(6)
            : RefreshInterval;
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
            var json = await _client.GetStringAsync(endpoint, timeout.Token).ConfigureAwait(false);
            var metadata = SteamStoreMetadataParser.Parse(appId, json);
            if (metadata is null)
            {
                WriteCache(cachePath, new CacheFile(appId, null, cache?.ArtworkPath, DateTimeOffset.UtcNow, 0));
                return cache?.Name is null && cache?.ArtworkPath is null ? null : cache;
            }

            return await StoreAsync(metadata, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or OperationCanceledException)
        {
            if (cache is not null) return cache;
            WriteCache(cachePath, new CacheFile(appId, null, null, DateTimeOffset.UtcNow, 0));
            return null;
        }
    }

    public async Task<CachedSteamMetadata?> StoreAsync(SteamGameMetadata metadata, CancellationToken cancellationToken = default)
    {
        if (!AppIdValidator.IsValid(metadata.AppId)) return null;
        var appDirectory = Path.Combine(_cacheDirectory, metadata.AppId);
        try
        {
            Directory.CreateDirectory(appDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new CachedSteamMetadata(metadata.AppId, metadata.Name, null, DateTimeOffset.UtcNow, 0);
        }
        var cachePath = Path.Combine(appDirectory, "steam.json");
        var cache = ReadCache(cachePath) ?? ReadLegacyCache(metadata.AppId, appDirectory);
        var artworkPath = cache?.ArtworkPath is { } existing && File.Exists(existing) && IsValidArtworkFile(existing) ? existing : null;
        if (metadata.HeaderImageUri is { Scheme: "https" } imageUri
            && (imageUri.Host.EndsWith(".steamstatic.com", StringComparison.OrdinalIgnoreCase)
                || string.Equals(imageUri.Host, "steamcdn-a.akamaihd.net", StringComparison.OrdinalIgnoreCase))
            && artworkPath is null)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var bytes = await _client.GetByteArrayAsync(imageUri, timeout.Token).ConfigureAwait(false);
                if (IsValidImage(bytes))
                {
                    var destination = Path.Combine(appDirectory, metadata.AppId + ".cover");
                    var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
                    await File.WriteAllBytesAsync(temporary, bytes, timeout.Token).ConfigureAwait(false);
                    File.Move(temporary, destination, overwrite: true);
                    artworkPath = destination;
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException or UnauthorizedAccessException)
            {
                // A missing cover does not prevent identifying the game by AppID.
            }
        }

        var result = new CachedSteamMetadata(metadata.AppId, metadata.Name ?? cache?.Name, artworkPath, DateTimeOffset.UtcNow, metadata.AchievementsTotal ?? 0);
        WriteCache(cachePath, new CacheFile(result.AppId, result.Name, result.ArtworkPath, result.LastCheckedUtc, result.AchievementsTotal));
        return result;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AchievementSync/0.1.2 (+desktop game metadata lookup)");
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
                : new CachedSteamMetadata(cache.AppId, cache.Name, cache.ArtworkPath, cache.LastCheckedUtc, cache.AchievementsTotal);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private CachedSteamMetadata? ReadLegacyCache(string appId, string appDirectory)
    {
        var legacyPath = Path.Combine(_cacheDirectory, appId + ".json");
        var legacy = ReadCache(legacyPath);
        if (legacy is null) return null;

        var artworkPath = legacy.ArtworkPath;
        if (artworkPath is { } existing && File.Exists(existing) && IsValidArtworkFile(existing))
        {
            var destination = Path.Combine(appDirectory, appId + ".cover");
            try
            {
                if (!File.Exists(destination))
                {
                    var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
                    try
                    {
                        File.Copy(existing, temporary, overwrite: false);
                        File.Move(temporary, destination, overwrite: false);
                    }
                    finally
                    {
                        try { if (File.Exists(temporary)) File.Delete(temporary); }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
                    }
                }

                artworkPath = File.Exists(destination) && IsValidArtworkFile(destination) ? destination : existing;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                artworkPath = existing;
            }
        }

        var migrated = legacy with { ArtworkPath = artworkPath };
        WriteCache(Path.Combine(appDirectory, "steam.json"),
            new CacheFile(migrated.AppId, migrated.Name, migrated.ArtworkPath, migrated.LastCheckedUtc));
        return migrated;
    }

    private static bool IsValidArtworkFile(string path)
    {
        try { return IsValidImage(File.ReadAllBytes(path)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsValidImage(byte[] bytes)
    {
        if (bytes.Length == 0) return false;
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image.PixelWidth > 0 && image.PixelHeight > 0;
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException or FormatException
            or ArgumentException or InvalidOperationException or COMException)
        {
            return false;
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

    private sealed record CacheFile(string AppId, string? Name, string? ArtworkPath, DateTimeOffset LastCheckedUtc, int? AchievementsTotal = null);
}

public sealed record CachedSteamMetadata(string AppId, string? Name, string? ArtworkPath, DateTimeOffset LastCheckedUtc, int? AchievementsTotal = null);
