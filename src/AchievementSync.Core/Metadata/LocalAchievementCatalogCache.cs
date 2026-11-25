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
            ConnectTimeout = TimeSpan.FromSeconds(8)
        };
        // Steam's image CDN can take more than three seconds to finish a cold TLS request.
        // Keep this bounded, but don't silently abandon icons on a slower connection.
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AchievementSync/1.0.0 (+desktop game metadata lookup)");
        return client;
    }
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
        AppMetadataLayout.RemoveLegacyFiles(_metadataRoot, appId);
        var file = AppCatalogFileStore.Read(path, appId, persistRepairs: true);
        if (file is not { Achievements.Count: > 0 }) return null;
        var achievements = file.Achievements.Select(item =>
        {
            var resolvedIcon = AchievementIconResolver.Resolve(item.Icon, path, appId);
            var resolvedLockedIcon = AchievementIconResolver.Resolve(item.IconGray, path, appId);
            var icon = AchievementIdentity.IsMetadataImageUri(resolvedIcon) ? resolvedIcon : null;
            var lockedIcon = AchievementIdentity.IsMetadataImageUri(resolvedLockedIcon) ? resolvedLockedIcon : null;
            return new AchievementRecord(
                item.Name,
                string.IsNullOrWhiteSpace(item.DisplayName)
                    ? AchievementIdentity.HumanizeApiName(item.Name)
                    : item.DisplayName,
                item.Description,
                IsUnlocked: null,
                UnlockTimeUnix: null,
                IconUri: icon ?? lockedIcon,
                IsHidden: item.Hidden,
                LockedIconUri: lockedIcon);
        }).ToArray();
        return new ParsedProfile(
            appId,
            file.Source ?? "Cached achievement catalog",
            path,
            file.UpdatedAtUtc,
            achievements,
            file.IsComplete,
            file.Name,
            DeclaredCatalogTotal: file.DeclaredTotal,
            CommunityFetchedAtUtc: file.CommunityFetchedAtUtc);
    }

    /// <summary>Copies catalog art into this AppID's Metadata folder and returns the local catalog.</summary>
    public async Task<ParsedProfile?> CacheCatalogAsync(
        string appId,
        ParsedProfile catalog,
        CancellationToken cancellationToken = default,
        string source = "local",
        int? declaredTotal = null)
    {
        if (!AppIdValidator.IsValid(appId) || !string.Equals(appId, catalog.AppId, StringComparison.Ordinal)
            || catalog.Achievements.Count == 0) return null;
        var normalized = NormalizeCatalog(catalog);
        if (normalized.Achievements.Count == 0 || !HasCatalogDetails(normalized)) return null;

        AppMetadataLayout.RemoveLegacyFiles(_metadataRoot, appId);
        var cachedFile = AppCatalogFileStore.Read(CatalogPath(appId), appId);
        var cached = LoadCatalog(appId);
        var effectiveDeclaredTotal = declaredTotal ?? catalog.DeclaredCatalogTotal ?? cached?.DeclaredCatalogTotal;
        var isComplete = catalog.HasCompleteCatalog || cached?.HasCompleteCatalog == true;
        if (cached is not null)
        {
            var cachedCatalog = NormalizeCatalog(cached);
            var incomingGame = new GameProfile(appId, normalized.Title, [normalized.FilePath],
                normalized.Achievements, normalized.HasCompleteCatalog);
            // Fresh public definitions must replace stale or incomplete cached text. For local
            // imports, the existing online cache remains the preferred source of descriptions.
            var merged = source.Equals("steam-community", StringComparison.OrdinalIgnoreCase)
                || source.Equals("viewer", StringComparison.OrdinalIgnoreCase)
                    ? ProfileMerger.EnrichWithCatalog(
                        new GameProfile(appId, cachedCatalog.Title, [cachedCatalog.FilePath], cachedCatalog.Achievements,
                            cachedCatalog.HasCompleteCatalog, DeclaredAchievementTotal: cachedCatalog.DeclaredCatalogTotal),
                        normalized)
                    : ProfileMerger.EnrichWithCatalog(incomingGame, cachedCatalog);
            normalized = normalized with
            {
                Achievements = merged.Achievements,
                HasCompleteCatalog = isComplete || merged.HasCompleteCatalog
            };
        }
        if (string.Equals(source, "steam-community", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(catalog.Title))
        {
            // Older community-page parsing treated the game's hero art as an achievement.
            // Remove that synthetic title row from existing caches while refreshing them.
            var gameTitleKey = AchievementIdentity.NormalizeDisplayName(catalog.Title);
            normalized = normalized with
            {
                Achievements = normalized.Achievements.Where(achievement =>
                    !(achievement.ApiName.StartsWith("name:", StringComparison.OrdinalIgnoreCase)
                      && string.Equals(AchievementIdentity.NormalizeDisplayName(achievement.DisplayName),
                          gameTitleKey, StringComparison.Ordinal))).ToArray()
            };
        }
        isComplete = isComplete || normalized.HasCompleteCatalog
            || (effectiveDeclaredTotal is > 0 && normalized.Achievements.Count >= effectiveDeclaredTotal.Value);
        normalized = normalized with { HasCompleteCatalog = isComplete, DeclaredCatalogTotal = effectiveDeclaredTotal };

        var appDirectory = Path.Combine(_metadataRoot, appId);
        var imageDirectory = Path.Combine(appDirectory, "images");
        Directory.CreateDirectory(imageDirectory);

        using var parallelism = new SemaphoreSlim(4, 4);
        var iconTasks = normalized.Achievements.Take(MaximumCatalogEntries).Select(async achievement =>
        {
            await parallelism.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var icon = await StoreIconAsync(appId, achievement.IconUri, imageDirectory, cancellationToken).ConfigureAwait(false);
                var lockedIcon = await StoreIconAsync(appId, achievement.LockedIconUri, imageDirectory, cancellationToken).ConfigureAwait(false);
                return new CachedIconPaths(icon, lockedIcon);
            }
            finally { parallelism.Release(); }
        }).ToArray();
        var iconPaths = await Task.WhenAll(iconTasks).ConfigureAwait(false);

        var rows = normalized.Achievements.Take(MaximumCatalogEntries).Select((achievement, index) => new AppCatalogAchievement(
            achievement.ApiName,
            AchievementIdentity.IsIdentifierLikeDisplayName(achievement.ApiName, achievement.DisplayName)
                ? achievement.Description ?? AchievementIdentity.HumanizeApiName(achievement.ApiName)
                : achievement.DisplayName,
            achievement.Description,
            iconPaths[index].Icon,
            achievement.IsHidden,
            iconPaths[index].LockedIcon)).ToArray();
        if (rows.Length == 0) return null;

        Directory.CreateDirectory(appDirectory);
        await AppCatalogFileStore.WriteAsync(CatalogPath(appId), new AppCatalogFile(
            appId,
            normalized.Title ?? cachedFile?.Name,
            cachedFile?.AchievementsTotal,
            cachedFile?.SteamMetadataCheckedAtUtc,
            string.Equals(source, "steam-community", StringComparison.OrdinalIgnoreCase)
                ? DateTimeOffset.UtcNow
                : cachedFile?.CommunityFetchedAtUtc,
            DateTimeOffset.UtcNow,
            isComplete,
            source,
            effectiveDeclaredTotal,
            rows), cancellationToken).ConfigureAwait(false);
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
        // A usable partial catalog still improves the library and should be cached.
        // Completeness is persisted separately so partial local data is never reported as final.
        if (parsed.Profile is not { Achievements.Count: > 0 } profile || !HasCatalogDetails(profile)) return null;
        return NormalizeCatalog(profile with { FilePath = path });
    }

    private static ParsedProfile NormalizeCatalog(ParsedProfile profile) => profile with
    {
        Achievements = profile.Achievements.Select(achievement => achievement with
        {
            ApiName = string.IsNullOrWhiteSpace(achievement.ApiName)
                ? AchievementIdentity.FromDisplayName(achievement.DisplayName)
                : achievement.ApiName,
            IsUnlocked = null,
            UnlockTimeUnix = null
        }).Where(achievement => !string.IsNullOrWhiteSpace(achievement.ApiName)).ToArray(),
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
        string? icon,
        string imageDirectory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(icon)) return null;
        Uri? uri = Uri.TryCreate(icon, UriKind.Absolute, out var parsed) ? parsed : null;
        if (uri?.Scheme == Uri.UriSchemeHttp && IsKnownSteamIconHost(uri.Host))
            uri = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri;
        string? localSource = uri?.IsFile == true ? uri.LocalPath : uri is null && Path.IsPathRooted(icon) ? icon : null;
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
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
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
            }

            // Close the exclusive temporary-file handle before renaming; Windows
            // rejects moving a file that is still open with FileShare.None.
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
        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { original.AbsoluteUri };
        var path = original.AbsolutePath;
        const string legacyPath = "/steamcommunity/public/images/apps/";
        const string currentPath = "/community_assets/images/apps/";
        if (path.Contains(legacyPath, StringComparison.OrdinalIgnoreCase))
        {
            urls.Add(new UriBuilder(original)
            {
                Scheme = Uri.UriSchemeHttps,
                Host = "shared.fastly.steamstatic.com",
                Port = -1,
                Path = path.Replace(legacyPath, currentPath, StringComparison.OrdinalIgnoreCase)
            }.Uri.AbsoluteUri);
        }
        else if (path.Contains(currentPath, StringComparison.OrdinalIgnoreCase))
        {
            var fileName = Path.GetFileName(path);
            var legacy = $"https://steamcdn-a.akamaihd.net{legacyPath}{fileName}";
            urls.Add(legacy);
        }

        if (path.Contains(legacyPath, StringComparison.OrdinalIgnoreCase))
        {
            var relative = path[(path.IndexOf(legacyPath, StringComparison.OrdinalIgnoreCase) + legacyPath.Length)..];
            urls.Add($"https://steamcdn-a.akamaihd.net{legacyPath}{relative}");
            urls.Add($"https://cdn.akamai.steamstatic.com{legacyPath}{relative}");
        }

        foreach (var url in urls)
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri)) yield return uri;
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

    private sealed record CachedIconPaths(string? Icon, string? LockedIcon);
}
