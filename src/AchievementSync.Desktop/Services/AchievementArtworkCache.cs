using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;

namespace AchievementSync.Desktop.Services;

/// <summary>
/// Loads and caches achievement icons. Absolute http(s)/file URIs are decoded once per URI per
/// session; local disk cache and negative-result memory keep repeated lookups cheap.
/// </summary>
internal static class AchievementArtworkCache
{
    private const int MaximumBytes = 4 * 1024 * 1024;
    private const int MemoryCacheLimit = 2048;

    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck
            },
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(5)
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AchievementSync/1.0.0 (+desktop game metadata lookup)");
        return client;
    }
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, DateTimeOffset> FailedUris = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, BitmapImage?> MemoryCache = new(StringComparer.Ordinal);
    private static readonly string CacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AchievementSync", "achievement-icons");

    public static BitmapImage? TryGetCached(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return null;
        var key = uri.Trim();
        if (MemoryCache.TryGetValue(key, out var cached)) return cached;

        if (Uri.TryCreate(key, UriKind.Absolute, out var parsed) && parsed.IsFile)
        {
            var local = LoadLocal(parsed.LocalPath);
            if (local is not null) Remember(key, local);
            return local;
        }

        if (Path.IsPathRooted(key) && File.Exists(key))
        {
            var local = LoadLocal(key);
            if (local is not null) Remember(key, local);
            return local;
        }

        var normalized = NormalizeUri(key);
        var path = GetCachePath(normalized);
        if (File.Exists(path))
        {
            var local = LoadLocal(path);
            if (local is not null) Remember(key, local);
            return local;
        }

        if (!string.Equals(key, normalized, StringComparison.Ordinal))
        {
            var altPath = GetCachePath(key);
            if (File.Exists(altPath))
            {
                var local = LoadLocal(altPath);
                if (local is not null) Remember(key, local);
                return local;
            }
        }

        return null;
    }

    public static async Task<BitmapImage?> GetAsync(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return null;
        var key = uri.Trim();
        if (MemoryCache.TryGetValue(key, out var cached)) return cached;

        var result = await GetCoreAsync(key).ConfigureAwait(false);
        Remember(key, result);
        return result;
    }

    private static void Remember(string key, BitmapImage? image)
    {
        if (MemoryCache.Count >= MemoryCacheLimit) MemoryCache.Clear();
        MemoryCache[key] = image;
    }

    private static string NormalizeUri(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.Scheme == Uri.UriSchemeHttp)
        {
            return new UriBuilder(parsed) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri.ToString();
        }
        return uri;
    }

    private static string GetCachePath(string uri) =>
        Path.Combine(CacheDirectory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri))) + ".img");

    private static async Task<BitmapImage?> GetCoreAsync(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile)
            return LoadLocal(parsed.LocalPath);

        if (Path.IsPathRooted(uri) && File.Exists(uri))
            return LoadLocal(uri);

        var normalized = NormalizeUri(uri);
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out parsed) || (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp))
            return null;

        var path = GetCachePath(normalized);
        var gate = Gates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (File.Exists(path)) return LoadLocal(path);
            if (FailedUris.TryGetValue(normalized, out var failedAt) && DateTimeOffset.UtcNow - failedAt < TimeSpan.FromMinutes(2))
                return null;

            try
            {
                using var response = await Http.GetAsync(parsed, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > MaximumBytes)
                {
                    FailedUris[normalized] = DateTimeOffset.UtcNow;
                    return null;
                }

                await using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                await using var output = new MemoryStream();
                var buffer = new byte[16 * 1024];
                int read;
                while ((read = await input.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                {
                    if (output.Length + read > MaximumBytes)
                    {
                        FailedUris[normalized] = DateTimeOffset.UtcNow;
                        return null;
                    }
                    await output.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                }

                var bytes = output.ToArray();
                var image = Decode(bytes);
                if (image is null)
                {
                    FailedUris[normalized] = DateTimeOffset.UtcNow;
                    return null;
                }

                Directory.CreateDirectory(CacheDirectory);
                var temporary = path + ".tmp";
                await File.WriteAllBytesAsync(temporary, bytes).ConfigureAwait(false);
                File.Move(temporary, path, overwrite: true);
                FailedUris.TryRemove(normalized, out _);
                return image;
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException or NotSupportedException)
            {
                FailedUris[normalized] = DateTimeOffset.UtcNow;
                return null;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static BitmapImage? LoadLocal(string path)
    {
        try { return File.Exists(path) ? Decode(File.ReadAllBytes(path)) : null; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException) { return null; }
    }

    private static BitmapImage? Decode(byte[] data)
    {
        try
        {
            using var stream = new MemoryStream(data, writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 128;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException or System.Windows.Markup.XamlParseException or ArgumentException) { return null; }
    }
}
