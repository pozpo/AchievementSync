using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AchievementSync.Core.Models;
using AchievementSync.Core.Parsing;
using AchievementSync.Core.Scanning;

namespace AchievementSync.Core.Metadata;

public sealed record AchievementCatalogCacheUpdate(int GamesUpdated, int ImagesStored, int Failed);

/// <summary>
/// Stores achievement definitions and artwork separately from the viewer progress file.
/// Catalog sources are read-only; local copies are written to Metadata/&lt;AppID&gt;/catalog.json
/// and Metadata/&lt;AppID&gt;/images/.
/// </summary>
public sealed class LocalAchievementCatalogCache
{
    private const int MaximumIconBytes = 4 * 1024 * 1024;
    private const int MaximumCatalogEntries = 5000;
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly string _metadataRoot;
    private readonly string[] _generatorRoots;
    private readonly JsonAchievementParser _jsonParser = new();
    private readonly IniAchievementParser _iniParser = new();

    public LocalAchievementCatalogCache(string? metadataRoot = null, string? adaptersRoot = null)
    {
        _metadataRoot = Path.GetFullPath(metadataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AchievementSync", "Metadata"));
        _generatorRoots = FindGeneratorRoots(adaptersRoot).ToArray();
    }

    public async Task<AchievementCatalogCacheUpdate> UpdateAsync(
        IReadOnlyList<DiscoveredAchievementFile> sourceFiles,
        CancellationToken cancellationToken = default)
    {
        var updated = 0;
        var images = 0;
        var failed = 0;
        foreach (var group in sourceFiles.Where(file => AppIdValidator.IsValid(file.AppId))
                     .GroupBy(file => file.AppId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var catalog = FindBestCatalog(group.Key, group);
            if (catalog is null) continue;
            try
            {
                var before = CountImages(group.Key);
                var cached = await CacheCatalogAsync(group.Key, catalog, cancellationToken).ConfigureAwait(false);
                if (cached is null) { failed++; continue; }
                updated++;
                images += Math.Max(0, CountImages(group.Key) - before);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or HttpRequestException)
            {
                failed++;
            }
        }

        return new AchievementCatalogCacheUpdate(updated, images, failed);
    }

    public ParsedProfile? LoadCatalog(string appId)
    {
        if (!AppIdValidator.IsValid(appId)) return null;
        var path = CatalogPath(appId);
        if (!File.Exists(path)) return null;
        var parsed = _jsonParser.Parse(path, appId);
        return parsed.Profile is { HasCompleteCatalog: true } profile ? profile : null;
    }

    /// <summary>Copies catalog art into this AppID's Metadata folder and returns the local catalog.</summary>
    public async Task<ParsedProfile?> CacheCatalogAsync(
        string appId,
        ParsedProfile catalog,
        CancellationToken cancellationToken = default)
    {
        if (!AppIdValidator.IsValid(appId) || !string.Equals(appId, catalog.AppId, StringComparison.Ordinal)
            || !catalog.HasCompleteCatalog) return null;
        var normalized = NormalizeCatalog(catalog);
        if (normalized.Achievements.Count == 0 || !HasCatalogDetails(normalized)) return null;

        var cached = LoadCatalog(appId);
        if (cached is not null)
        {
            var merged = ProfileMerger.Merge([NormalizeCatalog(cached), normalized]).Single();
            normalized = normalized with { Achievements = merged.Achievements };
        }

        var appDirectory = Path.Combine(_metadataRoot, appId);
        var imageDirectory = Path.Combine(appDirectory, "images");
        Directory.CreateDirectory(imageDirectory);

        using var parallelism = new SemaphoreSlim(4, 4);
        var iconTasks = normalized.Achievements.Take(MaximumCatalogEntries).Select(async achievement =>
        {
            await parallelism.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { return await StoreIconAsync(appId, achievement, imageDirectory, cancellationToken).ConfigureAwait(false); }
            finally { parallelism.Release(); }
        }).ToArray();
        var iconPaths = await Task.WhenAll(iconTasks).ConfigureAwait(false);

        var rows = normalized.Achievements.Take(MaximumCatalogEntries).Select((achievement, index) => new CatalogRow(
            achievement.ApiName,
            achievement.DisplayName,
            achievement.Description,
            iconPaths[index],
            achievement.IsHidden)).ToArray();
        if (rows.Length == 0) return null;

        Directory.CreateDirectory(appDirectory);
        await WriteAtomicAsync(CatalogPath(appId), JsonSerializer.SerializeToUtf8Bytes(rows, JsonOptions), cancellationToken)
            .ConfigureAwait(false);
        return LoadCatalog(appId);
    }

    private ParsedProfile? FindBestCatalog(string appId, IEnumerable<DiscoveredAchievementFile> sources)
    {
        ParsedProfile? best = null;
        var bestScore = 0;
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            AddCandidate(candidates, source.Path);
            // A generated achievements_schema.json beside the helper script has no AppID
            // marker and can belong to whichever game was processed last. Import only
            // the AppID-scoped source and generator outputs instead.
            var current = Path.GetDirectoryName(source.Path);
            for (var depth = 0; current is not null && depth < 10; depth++, current = Path.GetDirectoryName(current))
                AddOutputCandidates(candidates, current, appId);
        }

        foreach (var root in _generatorRoots) AddOutputCandidates(candidates, root, appId);

        foreach (var path in candidates)
        {
            if (!File.Exists(path)) continue;
            var parsed = ParseCandidate(path, appId);
            if (parsed is null || !HasCatalogDetails(parsed)) continue;
            var score = CatalogScore(parsed);
            if (score <= bestScore) continue;
            best = parsed;
            bestScore = score;
        }

        return best;
    }

    private ParsedProfile? ParseCandidate(string path, string appId)
    {
        var parsed = string.Equals(Path.GetExtension(path), ".ini", StringComparison.OrdinalIgnoreCase)
            ? _iniParser.Parse(path, appId)
            : _jsonParser.Parse(path, appId);
        if (parsed.Profile is not { HasCompleteCatalog: true } profile) return null;
        return NormalizeCatalog(profile with { FilePath = path });
    }

    private static ParsedProfile NormalizeCatalog(ParsedProfile profile) => profile with
    {
        Achievements = profile.Achievements.Select(achievement => achievement with
        {
            IsUnlocked = null,
            UnlockTimeUnix = null
        }).ToArray(),
        HasCompleteCatalog = profile.HasCompleteCatalog
    };

    private static bool HasCatalogDetails(ParsedProfile profile) => profile.Achievements.Count > 0
        && profile.Achievements.Any(achievement =>
            !string.IsNullOrWhiteSpace(achievement.DisplayName)
            || !string.IsNullOrWhiteSpace(achievement.Description)
            || !string.IsNullOrWhiteSpace(achievement.IconUri));

    private static int CatalogScore(ParsedProfile profile) => profile.Achievements.Sum(achievement =>
        (string.IsNullOrWhiteSpace(achievement.DisplayName) ? 0 : 4)
        + (string.IsNullOrWhiteSpace(achievement.Description) ? 0 : 4)
        + (string.IsNullOrWhiteSpace(achievement.IconUri) ? 0 : 2));

    private async Task<string?> StoreIconAsync(
        string appId,
        AchievementRecord achievement,
        string imageDirectory,
        CancellationToken cancellationToken)
    {
        var icon = achievement.IconUri;
        if (string.IsNullOrWhiteSpace(icon)) return null;
        Uri? uri = Uri.TryCreate(icon, UriKind.Absolute, out var parsed) ? parsed : null;
        if (uri?.Scheme == Uri.UriSchemeHttp && IsKnownSteamIconHost(uri.Host))
            uri = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri;
        string? localSource = uri?.IsFile == true ? uri.LocalPath : Path.IsPathRooted(icon) ? icon : null;
        if (localSource is not null && !File.Exists(localSource)) return null;
        if (localSource is null && (uri is null || uri.Scheme != Uri.UriSchemeHttps)) return null;

        var extension = GetImageExtension(localSource ?? uri!.AbsolutePath);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{appId}\n{icon}")))[..24].ToLowerInvariant();
        var relative = $"images/{key}{extension}";
        var destination = Path.Combine(Path.GetDirectoryName(imageDirectory)!, relative.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(destination)) return relative;

        var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
            if (localSource is not null)
            {
                await using var input = new FileStream(localSource, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (input.Length is <= 0 or > MaximumIconBytes) return null;
                await input.CopyToAsync(output, 16 * 1024, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                byte[]? imageBytes = null;
                foreach (var downloadUri in GetDownloadUris(uri!))
                {
                    imageBytes = await TryDownloadImageAsync(downloadUri, cancellationToken).ConfigureAwait(false);
                    if (imageBytes is not null) break;
                }
                if (imageBytes is null) return null;
                await output.WriteAsync(imageBytes, cancellationToken).ConfigureAwait(false);
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
            File.Move(temporary, destination, overwrite: true);
            return relative;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or HttpRequestException
                                           or OperationCanceledException or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string GetImageExtension(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif" ? extension : ".img";
    }

    private static bool IsKnownSteamIconHost(string host) =>
        string.Equals(host, "steamcdn-a.akamaihd.net", StringComparison.OrdinalIgnoreCase)
        || string.Equals(host, "media.steampowered.com", StringComparison.OrdinalIgnoreCase)
        || string.Equals(host, "shared.fastly.steamstatic.com", StringComparison.OrdinalIgnoreCase)
        || string.Equals(host, "cdn.fastly.steamstatic.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".steamstatic.com", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<Uri> GetDownloadUris(Uri original)
    {
        var path = original.AbsolutePath;
        const string legacyPath = "/steamcommunity/public/images/apps/";
        const string currentPath = "/community_assets/images/apps/";
        var isLegacy = path.Contains(legacyPath, StringComparison.OrdinalIgnoreCase);
        var isCurrent = path.Contains(currentPath, StringComparison.OrdinalIgnoreCase);
        if (isLegacy)
        {
            var currentPathValue = path.Replace(legacyPath, currentPath, StringComparison.OrdinalIgnoreCase);
            yield return new UriBuilder(original)
            {
                Scheme = Uri.UriSchemeHttps,
                Host = "shared.fastly.steamstatic.com",
                Port = -1,
                Path = currentPathValue
            }.Uri;
        }
        else if (isCurrent)
        {
            yield return new UriBuilder(original)
            {
                Scheme = Uri.UriSchemeHttps,
                Host = "shared.fastly.steamstatic.com",
                Port = -1
            }.Uri;
        }

        yield return original;
        if (!isLegacy && !isCurrent) yield break;

        var legacyFallbackPath = isCurrent
            ? path.Replace(currentPath, legacyPath, StringComparison.OrdinalIgnoreCase)
            : path;
        foreach (var host in new[] { "steamcdn-a.akamaihd.net", "cdn.akamai.steamstatic.com", "cdn.cloudflare.steamstatic.com" })
        {
            if (string.Equals(original.Host, host, StringComparison.OrdinalIgnoreCase)) continue;
            yield return new UriBuilder(original)
            {
                Scheme = Uri.UriSchemeHttps,
                Host = host,
                Port = -1,
                Path = legacyFallbackPath
            }.Uri;
        }
    }

    private static async Task<byte[]?> TryDownloadImageAsync(Uri uri, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is <= 0 or > MaximumIconBytes) return null;
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is not null && !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return null;

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await input.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaximumIconBytes) return null;
                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            return buffer.Length == 0 ? null : buffer.ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or HttpRequestException or OperationCanceledException
                                           or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static IEnumerable<string> FindGeneratorRoots(string? adaptersRoot)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\Games\generate_emu_config",
            @"C:\Games\GSE Uplay R2 semi-auto AW Joker combined V11\generate_emu_config",
            @"C:\AchievementSync\adapters\generate_emu_config",
            @"C:\AchievementSync\adapters\Achievement-Enabler-main\generate_emu_config"
        };

        if (!string.IsNullOrWhiteSpace(adaptersRoot))
        {
            var configured = Path.GetFullPath(adaptersRoot);
            roots.Add(Path.Combine(configured, "generate_emu_config"));
            roots.Add(Path.Combine(configured, "Achievement-Enabler-main", "generate_emu_config"));
            if (Path.GetFileName(configured).Equals("generate_emu_config", StringComparison.OrdinalIgnoreCase)) roots.Add(configured);
            AddAdapterGeneratorChildren(roots, configured);
        }
        AddAdapterGeneratorChildren(roots, Path.Combine(AppContext.BaseDirectory, "adapters"));
        AddAdapterGeneratorChildren(roots, @"C:\AchievementSync\adapters");

        var gamesRoot = @"C:\Games";
        if (Directory.Exists(gamesRoot))
        {
            try
            {
                foreach (var directory in Directory.EnumerateDirectories(gamesRoot, "*", SearchOption.TopDirectoryOnly))
                {
                    var candidate = Path.Combine(directory, "generate_emu_config");
                    if (Directory.Exists(candidate)) roots.Add(candidate);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }

        return roots.Where(Directory.Exists);
    }

    private static void AddAdapterGeneratorChildren(HashSet<string> roots, string adaptersRoot)
    {
        if (!Directory.Exists(adaptersRoot)) return;
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(adaptersRoot, "*", SearchOption.TopDirectoryOnly))
            {
                var candidate = Path.Combine(directory, "generate_emu_config");
                if (Directory.Exists(candidate)) roots.Add(candidate);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
    }

    private static void AddOutputCandidates(HashSet<string> candidates, string root, string appId)
    {
        foreach (var outputName in new[] { "_OUTPUT", "output" })
        {
            AddCandidate(candidates, Path.Combine(root, outputName, appId, "steam_settings", "achievements.json"));
            AddCandidate(candidates, Path.Combine(root, "generate_emu_config", outputName, appId, "steam_settings", "achievements.json"));
        }
    }

    private static void AddCandidate(HashSet<string> candidates, string path)
    {
        try { candidates.Add(Path.GetFullPath(path)); }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException) { }
    }

    private string CatalogPath(string appId) => Path.Combine(_metadataRoot, appId, "catalog.json");

    private int CountImages(string appId)
    {
        var directory = Path.Combine(_metadataRoot, appId, "images");
        try { return Directory.Exists(directory) ? Directory.GetFiles(directory).Length : 0; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return 0; }
    }

    private static async Task WriteAtomicAsync(string path, byte[] contents, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await output.WriteAsync(contents, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private sealed record CatalogRow(string Name, string? DisplayName, string? Description, string? Icon, bool Hidden);
}
