using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Windows.Media.Imaging;
using AchievementSync.Core.Metadata;

namespace AchievementSync.Desktop.Services;

public sealed class SteamMetadataCache
{
    private static readonly HttpClient DefaultClient = CreateClient();
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromDays(30);
    private static readonly TimeSpan MissingArtworkRetryInterval = TimeSpan.FromMinutes(15);
    private readonly HttpClient _client;
    private readonly string _cacheDirectory;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _artworkRetryAfterUtc = new(StringComparer.Ordinal);

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
        var catalogPath = Path.Combine(appDirectory, "catalog.json");
        var catalog = AppCatalogFileStore.Read(catalogPath, appId, persistRepairs: true);
        if (catalog is null)
        {
            await MigrateLegacyCacheAsync(appId, cancellationToken).ConfigureAwait(false);
            catalog = AppCatalogFileStore.Read(catalogPath, appId, persistRepairs: true);
        }
        AppMetadataLayout.RemoveLegacyFiles(_cacheDirectory, appId);
        var coverPath = Path.Combine(appDirectory, appId + ".cover");
        var artwork = File.Exists(coverPath) && IsValidArtworkFile(coverPath) ? coverPath : null;
        var checkedAt = catalog?.SteamMetadataCheckedAtUtc ?? DateTimeOffset.MinValue;
        var cache = catalog is null && artwork is null
            ? null
            : new CachedSteamMetadata(appId, catalog?.Name, artwork, checkedAt, catalog?.AchievementsTotal);
        var cacheLifetime = cache?.Name is null ? TimeSpan.FromHours(6)
            : cache.AchievementsTotal is null ? TimeSpan.FromHours(6)
            : RefreshInterval;
        var now = DateTimeOffset.UtcNow;
        if (cache is not null && cache.ArtworkPath is null
            && now - cache.LastCheckedUtc < MissingArtworkRetryInterval)
        {
            return cache.Name is null ? null : cache;
        }

        if (cache is not null && cache.ArtworkPath is null
            && _artworkRetryAfterUtc.TryGetValue(appId, out var retryAfter)
            && now < retryAfter)
        {
            return cache.Name is null ? null : cache;
        }

        if (cache is not null && now - cache.LastCheckedUtc < cacheLifetime
            && cache.ArtworkPath is { } validArtwork && File.Exists(validArtwork))
            return cache;

        if (cache?.ArtworkPath is null)
            _artworkRetryAfterUtc[appId] = now.Add(MissingArtworkRetryInterval);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var endpoint = $"https://store.steampowered.com/api/appdetails?appids={Uri.EscapeDataString(appId)}&l=english";
            var json = await _client.GetStringAsync(endpoint, timeout.Token).ConfigureAwait(false);
            var metadata = SteamStoreMetadataParser.Parse(appId, json);
            if (metadata is null)
            {
                var fallback = await StoreAsync(
                    new SteamGameMetadata(appId, cache?.Name, null, cache?.AchievementsTotal),
                    cancellationToken).ConfigureAwait(false);
                if (fallback is not null) return fallback;
                return cache?.Name is null && cache?.ArtworkPath is null ? null : cache;
            }

            return await StoreAsync(metadata, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or OperationCanceledException)
        {
            if (cache?.ArtworkPath is null)
            {
                try
                {
                    var fallback = await StoreAsync(
                        new SteamGameMetadata(appId, cache?.Name, null, cache?.AchievementsTotal),
                        cancellationToken).ConfigureAwait(false);
                    if (fallback?.ArtworkPath is not null
                        || !string.IsNullOrWhiteSpace(fallback?.Name)
                        || fallback?.AchievementsTotal is not null)
                        return fallback;
                }
                catch (Exception fallbackException) when (fallbackException is HttpRequestException or IOException or JsonException or OperationCanceledException or UnauthorizedAccessException)
                {
                    // Keep the library usable when both Steam metadata and artwork are unavailable.
                }
            }

            if (cache is not null) return cache;
            return null;
        }
    }

    private async Task MigrateLegacyCacheAsync(string appId, CancellationToken cancellationToken)
    {
        var legacyMetadataPath = Path.Combine(_cacheDirectory, appId + ".json");
        if (!File.Exists(legacyMetadataPath)) return;

        try
        {
            var legacy = JsonSerializer.Deserialize<LegacySteamMetadata>(
                await File.ReadAllTextAsync(legacyMetadataPath, cancellationToken).ConfigureAwait(false),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (legacy is null) return;

            var appDirectory = Path.Combine(_cacheDirectory, appId);
            Directory.CreateDirectory(appDirectory);
            var targetCover = Path.Combine(appDirectory, appId + ".cover");
            var legacyArtworkPath = !string.IsNullOrWhiteSpace(legacy.ArtworkPath)
                ? Path.IsPathRooted(legacy.ArtworkPath)
                    ? legacy.ArtworkPath
                    : Path.Combine(_cacheDirectory, legacy.ArtworkPath)
                : Path.Combine(_cacheDirectory, appId + ".cover");
            var artworkPath = (File.Exists(legacyArtworkPath) && IsValidArtworkFile(legacyArtworkPath))
                ? legacyArtworkPath
                : File.Exists(Path.Combine(_cacheDirectory, appId + ".cover"))
                    && IsValidArtworkFile(Path.Combine(_cacheDirectory, appId + ".cover"))
                        ? Path.Combine(_cacheDirectory, appId + ".cover")
                        : null;

            if (artworkPath is not null)
            {
                if (!File.Exists(targetCover) || !IsValidArtworkFile(targetCover))
                {
                    var temporary = targetCover + ".tmp-" + Guid.NewGuid().ToString("N");
                    try
                    {
                        File.Copy(artworkPath, temporary, overwrite: true);
                        File.Move(temporary, targetCover, overwrite: true);
                    }
                    finally
                    {
                        try { if (File.Exists(temporary)) File.Delete(temporary); }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
                    }
                }
                artworkPath = targetCover;
            }

            await AppCatalogFileStore.WriteAsync(Path.Combine(appDirectory, "catalog.json"), new AppCatalogFile(
                appId,
                legacy.Name,
                legacy.AchievementsTotal,
                legacy.LastCheckedUtc,
                null,
                DateTimeOffset.UtcNow,
                false,
                "legacy-steam-metadata",
                null,
                []), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
                                           or ArgumentException or NotSupportedException)
        {
            // A legacy cache is optional; normal online metadata lookup can rebuild it.
        }
    }

    public async Task<CachedSteamMetadata?> StoreAsync(SteamGameMetadata metadata, CancellationToken cancellationToken = default)
    {
        if (!AppIdValidator.IsValid(metadata.AppId)) return null;
        var appDirectory = Path.Combine(_cacheDirectory, metadata.AppId);
        AppMetadataLayout.RemoveLegacyFiles(_cacheDirectory, metadata.AppId);
        try
        {
            Directory.CreateDirectory(appDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new CachedSteamMetadata(metadata.AppId, metadata.Name, null, DateTimeOffset.UtcNow, 0);
        }
        var catalogPath = Path.Combine(appDirectory, "catalog.json");
        var catalog = AppCatalogFileStore.Read(catalogPath, metadata.AppId, persistRepairs: true);
        var coverPath = Path.Combine(appDirectory, metadata.AppId + ".cover");
        var artworkPath = File.Exists(coverPath) && IsValidArtworkFile(coverPath) ? coverPath : null;
        if (artworkPath is null)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                foreach (var imageUri in GetArtworkCandidates(metadata))
                {
                    try
                    {
                        var bytes = await _client.GetByteArrayAsync(imageUri, timeout.Token).ConfigureAwait(false);
                        if (!IsValidImage(bytes)) continue;

                        var destination = coverPath;
                        var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
                        try
                        {
                            await File.WriteAllBytesAsync(temporary, bytes, timeout.Token).ConfigureAwait(false);
                            File.Move(temporary, destination, overwrite: true);
                            artworkPath = destination;
                        }
                        finally
                        {
                            try { if (File.Exists(temporary)) File.Delete(temporary); }
                            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
                        }
                        break;
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException)
                    {
                        // Try the next official Steam artwork URL.
                    }
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException or UnauthorizedAccessException)
            {
                // A missing cover does not prevent identifying the game by AppID.
            }
        }

        var result = new CachedSteamMetadata(metadata.AppId, metadata.Name ?? catalog?.Name, artworkPath, DateTimeOffset.UtcNow,
            metadata.AchievementsTotal ?? catalog?.AchievementsTotal);
        await AppCatalogFileStore.WriteAsync(catalogPath, new AppCatalogFile(
            result.AppId,
            result.Name,
            result.AchievementsTotal,
            result.LastCheckedUtc,
            null,
            DateTimeOffset.UtcNow,
            false,
            null,
            null,
            []), cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static IEnumerable<Uri> GetArtworkCandidates(SteamGameMetadata metadata)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (metadata.HeaderImageUri is { Scheme: "https" } header
            && (header.Host.EndsWith(".steamstatic.com", StringComparison.OrdinalIgnoreCase)
                || string.Equals(header.Host, "steamcdn-a.akamaihd.net", StringComparison.OrdinalIgnoreCase))
            && seen.Add(header.AbsoluteUri))
        {
            yield return header;
        }

        var appId = Uri.EscapeDataString(metadata.AppId);
        foreach (var address in new[]
                 {
                     $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg",
                     $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/header.jpg"
                 })
        {
            if (Uri.TryCreate(address, UriKind.Absolute, out var fallback) && seen.Add(fallback.AbsoluteUri))
                yield return fallback;
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AchievementSync/1.0.0 (+desktop game metadata lookup)");
        return client;
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

}

public sealed record CachedSteamMetadata(string AppId, string? Name, string? ArtworkPath, DateTimeOffset LastCheckedUtc, int? AchievementsTotal = null);

internal sealed record LegacySteamMetadata(
    string? AppId,
    string? Name,
    string? ArtworkPath,
    DateTimeOffset LastCheckedUtc,
    int? AchievementsTotal = null);
