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
    private const int MemoryCacheLimit = 128;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, byte> FailedUris = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, BitmapImage?> MemoryCache = new(StringComparer.Ordinal);
    private static readonly string CacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AchievementSync", "achievement-icons");

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

    private static async Task<BitmapImage?> GetCoreAsync(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile)
            return LoadLocal(parsed.LocalPath);
        if (!Uri.TryCreate(uri, UriKind.Absolute, out parsed) || parsed.Scheme != Uri.UriSchemeHttps)
            return Path.IsPathRooted(uri) ? LoadLocal(uri) : null;

        var path = Path.Combine(CacheDirectory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri))) + ".img");
        var gate = Gates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (File.Exists(path)) return LoadLocal(path);
            if (FailedUris.ContainsKey(uri)) return null;
            try
            {
                using var response = await Http.GetAsync(parsed, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > MaximumBytes)
                {
                    FailedUris.TryAdd(uri, 0);
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
                        FailedUris.TryAdd(uri, 0);
                        return null;
                    }
                    await output.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                }

                var bytes = output.ToArray();
                var image = Decode(bytes);
                if (image is null)
                {
                    FailedUris.TryAdd(uri, 0);
                    return null;
                }

                Directory.CreateDirectory(CacheDirectory);
                var temporary = path + ".tmp";
                await File.WriteAllBytesAsync(temporary, bytes).ConfigureAwait(false);
                File.Move(temporary, path, overwrite: true);
                return image;
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException or NotSupportedException)
            {
                FailedUris.TryAdd(uri, 0);
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
