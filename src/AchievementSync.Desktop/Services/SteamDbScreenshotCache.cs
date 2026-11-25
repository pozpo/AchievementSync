using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AchievementSync.Core.Metadata;

namespace AchievementSync.Desktop.Services;

/// <summary>Fetches SteamDB screenshot URLs and keeps a small per-game image cache on disk.</summary>
public sealed class SteamDbScreenshotCache
{
    private const int MaximumScreenshotsPerGame = 8;
    private const long MaximumImageBytes = 12L * 1024 * 1024;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(30);
    private static readonly TimeSpan FailedRefreshInterval = TimeSpan.FromHours(6);
    private static readonly Regex ImageUrlRegex = new(
        @"(?:https?:)?//[^\s""'<>\\]+?\.(?:jpg|jpeg|png|webp)(?:\?[^\s""'<>\\]*)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, DateTimeOffset> FailedRefreshes = new(StringComparer.Ordinal);
    private readonly string _cacheRoot;

    public SteamDbScreenshotCache(string? metadataRoot = null)
    {
        _cacheRoot = Path.GetFullPath(metadataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AchievementSync", "Metadata"));
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(8),
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
        })
        {
            Timeout = TimeSpan.FromSeconds(12)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AchievementSync/1.0.0 (+desktop game artwork)");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        return client;
    }

    public async Task<ImageSource?> GetRandomScreenshotAsync(string appId, CancellationToken cancellationToken = default)
    {
        if (!IsNumericAppId(appId)) return null;
        var directory = Path.Combine(_cacheRoot, appId, "screenshots");
        AppMetadataLayout.RemoveLegacyFiles(_cacheRoot, appId);
        Directory.CreateDirectory(directory);
        MigrateLegacyImages(directory);
        var cached = ReadImages(directory);
        if (cached.Count > 0)
        {
            if (DateTimeOffset.UtcNow - GetNewestWriteTime(cached) >= CacheLifetime)
            {
                _ = RefreshInBackgroundAsync(appId, directory, cancellationToken);
            }
            return LoadRandom(cached);
        }

        await RefreshAsync(appId, directory, cancellationToken).ConfigureAwait(false);
        return LoadRandom(ReadImages(directory));
    }

    /// <summary>Downloads and keeps SteamDB screenshots ready for offline playtime notifications.</summary>
    public async Task<int> PrecacheAsync(string appId, CancellationToken cancellationToken = default)
    {
        if (!IsNumericAppId(appId)) return 0;
        var directory = Path.Combine(_cacheRoot, appId, "screenshots");
        AppMetadataLayout.RemoveLegacyFiles(_cacheRoot, appId);
        Directory.CreateDirectory(directory);
        MigrateLegacyImages(directory);
        await RefreshAsync(appId, directory, cancellationToken).ConfigureAwait(false);
        var count = ReadImages(directory).Count;
        AppLog.Info(count > 0
            ? $"SteamDB screenshot cache ready for AppID {appId}; images={count}; path={directory}."
            : $"SteamDB screenshot cache is empty for AppID {appId}; path={directory}.");
        return count;
    }

    private static async Task RefreshInBackgroundAsync(
        string appId, string directory, CancellationToken cancellationToken)
    {
        try { await RefreshAsync(appId, directory, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception)
        {
            AppLog.Warning($"SteamDB screenshot refresh failed for AppID {appId}: {exception.GetType().Name}.");
        }
    }

    private static async Task RefreshAsync(
        string appId, string directory, CancellationToken cancellationToken)
    {
        var gate = Gates.GetOrAdd(appId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = ReadImages(directory);
            var now = DateTimeOffset.UtcNow;
            if (current.Count > 0 && now - GetNewestWriteTime(current) < CacheLifetime) return;
            if (current.Count == 0 && FailedRefreshes.TryGetValue(appId, out var failedAt)
                && now - failedAt < FailedRefreshInterval) return;

            Directory.CreateDirectory(directory);
            IReadOnlyList<Uri> sources = await GetScreenshotUrlsAsync(appId, cancellationToken).ConfigureAwait(false);
            if (sources.Count == 0)
            {
                AppLog.Warning($"SteamDB did not expose screenshot image URLs for AppID {appId}.");
                FailedRefreshes[appId] = DateTimeOffset.UtcNow;
                return;
            }

            var cachedImages = 0;
            foreach (var source in sources.DistinctBy(uri => uri.AbsoluteUri).Take(MaximumScreenshotsPerGame))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stem = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.AbsoluteUri)));
                var existing = Directory.EnumerateFiles(directory, stem + ".*", SearchOption.TopDirectoryOnly).FirstOrDefault(IsImageFile);
                if (existing is not null)
                {
                    cachedImages++;
                    continue;
                }

                var bytes = await DownloadImageAsync(source, cancellationToken).ConfigureAwait(false);
                if (bytes is null) continue;
                var filename = stem + GetImageExtension(bytes);
                var destination = Path.Combine(directory, filename);
                var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
                try
                {
                    await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
                    File.Move(temporary, destination, overwrite: true);
                    cachedImages++;
                }
                finally
                {
                    try { if (File.Exists(temporary)) File.Delete(temporary); }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
                }
            }

            if (cachedImages == 0 && current.Count == 0) FailedRefreshes[appId] = DateTimeOffset.UtcNow;
            else FailedRefreshes.TryRemove(appId, out _);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException
                                           or OperationCanceledException or InvalidOperationException)
        {
            AppLog.Warning($"SteamDB screenshots could not be cached for AppID {appId}: {exception.GetType().Name}.");
            FailedRefreshes[appId] = DateTimeOffset.UtcNow;
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<IReadOnlyList<Uri>> GetScreenshotUrlsAsync(
        string appId, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Http.GetAsync(
                $"https://steamdb.info/app/{appId}/screenshots/",
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode
                && response.Content.Headers.ContentLength is not > 4 * 1024 * 1024)
            {
                var page = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var steamDbUrls = ExtractScreenshotUrls(page);
                if (steamDbUrls.Count > 0)
                {
                    AppLog.Info($"SteamDB screenshot URLs loaded for AppID {appId}; candidates={steamDbUrls.Count}.");
                    return steamDbUrls;
                }
            }

            AppLog.Warning($"SteamDB screenshot page returned HTTP {(int)response.StatusCode} or no image URLs for AppID {appId}; trying Steam's public store listing.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            AppLog.Warning($"SteamDB screenshot lookup failed for AppID {appId} ({exception.GetType().Name}); trying Steam's public store listing.");
        }

        return await GetSteamStoreScreenshotUrlsAsync(appId, cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<Uri> ExtractScreenshotUrls(string page)
    {
        var urls = new List<Uri>();
        foreach (Match match in ImageUrlRegex.Matches(page))
        {
            var candidate = match.Value.Replace("\\/", "/", StringComparison.Ordinal)
                .Replace("&amp;", "&", StringComparison.OrdinalIgnoreCase);
            if (candidate.StartsWith("//", StringComparison.Ordinal)) candidate = "https:" + candidate;
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || !IsAllowedImageHost(uri)) continue;
            var path = uri.AbsolutePath;
            if (!path.Contains("ss_", StringComparison.OrdinalIgnoreCase)
                && !path.Contains("screenshot", StringComparison.OrdinalIgnoreCase)
                && !path.EndsWith("movie_full.jpg", StringComparison.OrdinalIgnoreCase)
                && !path.EndsWith("movie_600x337.jpg", StringComparison.OrdinalIgnoreCase)
                && !path.Contains("/ugc/", StringComparison.OrdinalIgnoreCase)) continue;
            urls.Add(uri);
        }
        return urls.DistinctBy(uri => uri.AbsoluteUri).ToArray();
    }

    private static async Task<IReadOnlyList<Uri>> GetSteamStoreScreenshotUrlsAsync(
        string appId, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Http.GetAsync(
                $"https://store.steampowered.com/api/appdetails?appids={Uri.EscapeDataString(appId)}&l=en",
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > 4 * 1024 * 1024)
                return [];
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await System.Text.Json.JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty(appId, out var app)
                || !app.TryGetProperty("success", out var success)
                || success.ValueKind != System.Text.Json.JsonValueKind.True
                || !app.TryGetProperty("data", out var data)
                || !data.TryGetProperty("screenshots", out var screenshots)
                || screenshots.ValueKind != System.Text.Json.JsonValueKind.Array)
                return [];

            var urls = new List<Uri>();
            foreach (var screenshot in screenshots.EnumerateArray())
            {
                if (!screenshot.TryGetProperty("path_full", out var image)
                    || image.ValueKind != System.Text.Json.JsonValueKind.String
                    || !Uri.TryCreate(image.GetString(), UriKind.Absolute, out var uri)
                    || !IsAllowedImageHost(uri)) continue;
                urls.Add(uri);
            }

            var result = urls.DistinctBy(uri => uri.AbsoluteUri).ToArray();
            AppLog.Info($"Steam Store screenshot fallback loaded for AppID {appId}; candidates={result.Length}.");
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException
                                           or System.Text.Json.JsonException)
        {
            AppLog.Warning($"Steam Store screenshot fallback failed for AppID {appId}: {exception.GetType().Name}.");
            return [];
        }
    }

    private static async Task<byte[]?> DownloadImageAsync(Uri uri, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > MaximumImageBytes) return null;
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[32 * 1024];
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (output.Length + read > MaximumImageBytes) return null;
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            var bytes = output.ToArray();
            return IsImage(bytes) ? bytes : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException
                                           or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> ReadImages(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                    .Where(path => Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".webp" or ".gif" or ".bmp")
                    .Where(IsImageFile)
                    .ToArray()
                : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void MigrateLegacyImages(string directory)
    {
        foreach (var legacy in Directory.EnumerateFiles(directory, "*.img", SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (!IsImageFile(legacy)) { File.Delete(legacy); continue; }
                var destination = Path.ChangeExtension(legacy, GetImageExtension(File.ReadAllBytes(legacy)));
                if (File.Exists(destination)) File.Delete(legacy);
                else File.Move(legacy, destination);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private static DateTimeOffset GetNewestWriteTime(IReadOnlyList<string> images) => images
        .Select(path => new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero))
        .DefaultIfEmpty(DateTimeOffset.MinValue)
        .Max();

    private static BitmapImage? LoadRandom(IReadOnlyList<string> images)
    {
        if (images.Count == 0) return null;
        return LoadImage(images[Random.Shared.Next(images.Count)]);
    }

    private static BitmapImage? LoadImage(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 1400;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException
                                           or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static bool IsImageFile(string path)
    {
        try { return File.Exists(path) && IsImage(File.ReadAllBytes(path)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
    }

    private static string GetImageExtension(byte[] bytes)
    {
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return ".png";
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return ".jpg";
        if (bytes.Length >= 12 && Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF" && Encoding.ASCII.GetString(bytes, 8, 4) == "WEBP") return ".webp";
        if (bytes.Length >= 6 && (Encoding.ASCII.GetString(bytes, 0, 6) is "GIF87a" or "GIF89a")) return ".gif";
        if (bytes.Length >= 2 && bytes[0] == 0x42 && bytes[1] == 0x4D) return ".bmp";
        return ".jpg";
    }

    private static bool IsImage(byte[] bytes)
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
        catch (Exception exception) when (exception is IOException or NotSupportedException or ArgumentException
                                           or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsAllowedImageHost(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host.EndsWith(".steamstatic.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".steamusercontent.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".akamaihd.net", StringComparison.OrdinalIgnoreCase));

    private static bool IsNumericAppId(string? appId) =>
        !string.IsNullOrWhiteSpace(appId) && appId.Length <= 10 && appId.All(char.IsAsciiDigit);

}
