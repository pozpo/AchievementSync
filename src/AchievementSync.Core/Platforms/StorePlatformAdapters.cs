using System.Buffers.Binary;
using System.Globalization;

namespace AchievementSync.Core.Platforms;

/// <summary>GOG Galaxy reader. Reads the per-game <c>gameplay.db</c> SQLite database that stores
/// official achievement state and rarity, plus an optional JSON export for older clients.</summary>
public sealed class GogDataAdapter(PlatformSourceOptions? defaults = null) : PlatformDataAdapter("gog", "GOG (official Galaxy data + rarity)")
{
    private readonly PlatformSourceOptions _defaults = defaults ?? PlatformSourceOptions.CreateDefault();

    public override bool IsConfigured => IsConfiguredFor(_defaults);

    public override bool IsConfiguredFor(PlatformSourceOptions options) => ApplicationsRoots(options).Any(Directory.Exists);

    public override string? ConfigurationHint =>
        "Sign in to GOG Galaxy so it writes achievement state, or choose its Galaxy folder on the Platforms page.";

    private static IReadOnlyList<string> ApplicationsRoots(PlatformSourceOptions options)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var configured = options.GogRoot;
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var full = Path.GetFullPath(configured);
            if (string.Equals(Path.GetFileName(full), "Applications", StringComparison.OrdinalIgnoreCase))
                candidates.Add(full);
            candidates.Add(Path.Combine(full, "Applications"));
            candidates.Add(full);
        }
        candidates.Add(Path.Combine(local, "GOG.com", "Galaxy", "Applications"));
        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public override Task<PlatformCollectResult> CollectAsync(PlatformSourceOptions options, CancellationToken cancellationToken = default)
    {
        var messages = new List<string>();
        var games = new SortedDictionary<string, PlatformGameSnapshot>(StringComparer.OrdinalIgnoreCase);
        var applicationsRoots = ApplicationsRoots(options).Where(Directory.Exists).ToArray();
        if (applicationsRoots.Length == 0)
            return Task.FromResult(new PlatformCollectResult(Platform, [], ["The GOG Galaxy Applications folder was not found."]));

        foreach (var database in applicationsRoots.SelectMany(root => DiscoverGameplayDatabases(root, cancellationToken)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var achievements = ReadGameplayDatabase(database);
                if (achievements.Count == 0) continue;
                var clientId = ClientIdFromPath(database) ?? Path.GetFileName(Path.GetDirectoryName(database)) ?? "gog";
                if (!games.TryGetValue(clientId, out var existing) || achievements.Count > existing.Achievements.Count)
                    games[clientId] = new PlatformGameSnapshot(Platform, clientId, null, database, achievements);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                messages.Add($"GOG: skipped {Path.GetFileName(Path.GetDirectoryName(database))} ({exception.GetType().Name}).");
            }
        }

        return Task.FromResult(new PlatformCollectResult(Platform, games.Values.ToList(), messages));
    }

    private static IEnumerable<string> DiscoverGameplayDatabases(string root, CancellationToken cancellationToken)
    {
        foreach (var clientDirectory in Directory.EnumerateDirectories(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var gameplay = Path.Combine(clientDirectory, "Gameplay");
            if (!Directory.Exists(gameplay)) continue;
            foreach (var userDirectory in Directory.EnumerateDirectories(gameplay))
            {
                var database = Path.Combine(userDirectory, "gameplay.db");
                if (File.Exists(database)) yield return database;
            }
        }
    }

    private static string? ClientIdFromPath(string database)
    {
        var parts = database.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var gameplayIndex = Array.FindIndex(parts, part => string.Equals(part, "Gameplay", StringComparison.OrdinalIgnoreCase));
        return gameplayIndex > 0 ? parts[gameplayIndex - 1] : null;
    }

    /// <summary>Reads the documented <c>achievement</c> table from a GOG gameplay database.</summary>
    public static IReadOnlyList<PlatformAchievement> ReadGameplayDatabase(string path)
    {
        var reader = new SqliteReadOnly(path);
        var rows = reader.ReadTable("achievement");
        var results = new List<PlatformAchievement>(rows.Count);
        foreach (var row in rows)
        {
            var key = AsString(row.ElementAtOrDefault(1));
            if (string.IsNullOrWhiteSpace(key)) continue;
            var name = AsString(row.ElementAtOrDefault(2));
            var description = AsString(row.ElementAtOrDefault(3));
            var unlockTime = AsLong(row.ElementAtOrDefault(5)) ?? 0;
            var rarity = AsDouble(row.ElementAtOrDefault(9));
            results.Add(new PlatformAchievement(
                key.Trim(),
                name,
                description,
                unlockTime > 0,
                unlockTime > 0 ? NormalizeEpochSeconds(unlockTime) : 0,
                IconPath: AsString(row.ElementAtOrDefault(6)),
                IconGrayPath: AsString(row.ElementAtOrDefault(7)),
                RarityPercent: rarity is > 0 and <= 100 ? rarity : null,
                Hidden: AsLong(row.ElementAtOrDefault(4)) == 0));
        }
        return results;
    }

    internal static long NormalizeEpochSeconds(long value) => value > 10_000_000_000 ? value / 1000 : value;

    internal static string? AsString(object? value) => value switch
    {
        string text => text,
        byte[] bytes => System.Text.Encoding.UTF8.GetString(bytes),
        _ => null
    };

    internal static long? AsLong(object? value) => value switch
    {
        long number => number,
        double number => (long)number,
        string text when long.TryParse(text, out var parsed) => parsed,
        _ => null
    };

    internal static double? AsDouble(object? value) => value switch
    {
        double number => number,
        long number => number,
        string text when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null
    };
}

/// <summary>Epic Games reader. Combines local emulator progress with the official public achievement
/// catalog and rarity endpoint. Public catalog rows are marked as unknown until local progress exists.</summary>
public sealed class EpicDataAdapter(PlatformSourceOptions? defaults = null, HttpClient? httpClient = null)
    : PlatformDataAdapter("epic", "Epic Games Store (local state + official API)")
{
    private readonly PlatformSourceOptions _defaults = defaults ?? PlatformSourceOptions.CreateDefault();
    private readonly HttpClient? _httpClient = httpClient;

    public override bool IsConfigured => IsConfiguredFor(_defaults);

    public override bool IsConfiguredFor(PlatformSourceOptions options)
    {
        return !string.IsNullOrWhiteSpace(options.EpicAccessToken)
               || LocalRoots(options).Any(Directory.Exists)
               || ManifestRoots(options).Any(Directory.Exists);
    }

    public override string? ConfigurationHint =>
        "Uses Epic Launcher manifests and the public achievements catalog. Optionally connect an Epic Developer Portal client in Platforms to query authenticated player progress.";

    public override async Task<PlatformCollectResult> CollectAsync(PlatformSourceOptions options, CancellationToken cancellationToken = default)
    {
        var messages = new List<string>();
        var games = new SortedDictionary<string, PlatformGameSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in LocalRoots(options).Where(Directory.Exists))
        {
            foreach (var file in Enumerate(root, "achievements.json", 6))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var achievements = XboxDataAdapter.ReadJsonStates(file);
                    if (achievements.Count == 0) continue;
                    var gameId = NearestDirectory(file) ?? "epic";
                    if (!games.TryGetValue(gameId, out var existing) || achievements.Count > existing.Achievements.Count)
                        games[gameId] = new PlatformGameSnapshot(Platform, gameId, null, file, achievements);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
                {
                    messages.Add($"Epic: skipped {Path.GetFileName(file)} ({exception.GetType().Name}).");
                }
            }
        }

        if (_httpClient is not null)
        {
            var manifestGames = ManifestRoots(options)
                .Where(Directory.Exists)
                .SelectMany(root => Enumerate(root, "*.item", 5))
                .Select(TryReadManifest)
                .Where(item => item is not null)
                .Cast<EpicInstalledGame>()
                .ToList();
            manifestGames.AddRange(ReadLauncherInstalledDat());

            var installations = manifestGames
                .GroupBy(item => item.CatalogItemId ?? item.AppName, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Take(500)
                .ToArray();
            using var slots = new SemaphoreSlim(3, 3);
            var tasks = installations.Select(async installation =>
            {
                await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var snapshot = await FetchPublicSchemaAsync(installation, options, games, cancellationToken).ConfigureAwait(false);
                    return (Installation: installation, Snapshot: snapshot);
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidDataException)
                {
                    lock (messages) messages.Add($"Epic: achievement lookup failed for {installation.DisplayName ?? installation.AppName} ({exception.GetType().Name}).");
                    return (Installation: installation, Snapshot: (PlatformGameSnapshot?)null);
                }
                finally { slots.Release(); }
            }).ToArray();

            foreach (var result in await Task.WhenAll(tasks).ConfigureAwait(false))
                if (result.Snapshot is { } snapshot) games[snapshot.GameId] = snapshot;
        }

        if (games.Count == 0) messages.Add("No local Epic achievement file was found.");
        return new PlatformCollectResult(Platform, games.Values.ToList(), messages);
    }

    private static IEnumerable<string> LocalRoots(PlatformSourceOptions options)
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return new[] { Path.Combine(roaming, "NemirtingasEpicEmu"), options.EpicRoot }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path!))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> ManifestRoots(PlatformSourceOptions options)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(options.EpicRoot)) roots.Add(options.EpicRoot);
        roots.Add(Path.Combine(programData, "Epic", "EpicGamesLauncher"));
        roots.Add(Path.Combine(local, "EpicGamesLauncher"));
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var full = Path.GetFullPath(root);
            yield return string.Equals(Path.GetFileName(full), "Manifests", StringComparison.OrdinalIgnoreCase)
                ? full
                : Path.Combine(full, "Data", "Manifests");
        }
    }

    private static IEnumerable<EpicInstalledGame> ReadLauncherInstalledDat()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var datPath = Path.Combine(programData, "Epic", "UnrealEngineLauncher", "LauncherInstalled.dat");
        if (!File.Exists(datPath)) yield break;
        System.Text.Json.JsonDocument? document = null;
        try
        {
            document = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(datPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            yield break;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object
                || !document.RootElement.TryGetProperty("InstallationList", out var list)
                || list.ValueKind != System.Text.Json.JsonValueKind.Array)
                yield break;

            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                var appName = ReadString(item, "AppName");
                var ns = ReadString(item, "NamespaceId");
                var catalogItemId = ReadString(item, "ArtifactId") ?? appName;
                if (string.IsNullOrWhiteSpace(appName) && string.IsNullOrWhiteSpace(ns)) continue;
                yield return new EpicInstalledGame(datPath, catalogItemId, appName, appName, ns);
            }
        }
    }

    private async Task<PlatformGameSnapshot?> FetchPublicSchemaAsync(EpicInstalledGame installation,
        PlatformSourceOptions options,
        IReadOnlyDictionary<string, PlatformGameSnapshot> localGames, CancellationToken cancellationToken)
    {
        foreach (var productId in new[] { installation.Namespace, installation.CatalogItemId, installation.AppName }
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var url = $"https://api.epicgames.dev/epic/achievements/v1/public/achievements/product/{Uri.EscapeDataString(productId!)}/locale/en?includeAchievements=true";
            using var response = await SendGetWithRateLimitAsync(url, bearerToken: null, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) continue;
            using var document = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            if (!document.RootElement.TryGetProperty("achievements", out var rows)
                || rows.ValueKind != System.Text.Json.JsonValueKind.Array || rows.GetArrayLength() == 0) continue;

            var gameId = installation.CatalogItemId ?? installation.AppName ?? productId!;
            var local = FindLocalGame(localGames, installation, gameId);
            var localById = (local?.Achievements ?? [])
                .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);

            var officialStates = !string.IsNullOrWhiteSpace(options.EpicAccessToken)
                                 && !string.IsNullOrWhiteSpace(options.EpicAccountId)
                ? await FetchOfficialPlayerProgressAsync(options.EpicAccessToken!, options.EpicAccountId!, productId!, cancellationToken).ConfigureAwait(false)
                : null;

            var achievements = new Dictionary<string, PlatformAchievement>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows.EnumerateArray())
            {
                var entry = row.TryGetProperty("achievement", out var nested) && nested.ValueKind == System.Text.Json.JsonValueKind.Object
                    ? nested : row;
                var id = ReadString(entry, "name", "id", "apiName", "api_name");
                if (string.IsNullOrWhiteSpace(id)) continue;
                var remote = new PlatformAchievement(
                    id,
                    ReadString(entry, "lockedDisplayName", "unlockedDisplayName", "displayName", "name"),
                    ReadString(entry, "lockedDescription", "unlockedDescription", "lockedDesc", "description"),
                    EarnedKnown: false,
                    IconPath: ReadString(entry, "unlockedIconLink", "unlockedIcon", "icon"),
                    IconGrayPath: ReadString(entry, "lockedIconLink", "lockedIcon", "iconGray"),
                    RarityPercent: ReadRarity(entry, row),
                    Hidden: ReadBool(entry, "hidden"));

                if (officialStates is not null && officialStates.TryGetValue(id, out var apiState))
                {
                    remote = remote with
                    {
                        Earned = apiState.Earned,
                        EarnedKnown = true,
                        EarnedTimeUnix = apiState.EarnedTimeUnix
                    };
                }
                if (localById.TryGetValue(id, out var state))
                {
                    var earned = (remote.EarnedKnown && remote.Earned) || (state.EarnedKnown && state.Earned);
                    remote = remote with
                    {
                        Earned = earned,
                        EarnedKnown = remote.EarnedKnown || state.EarnedKnown,
                        EarnedTimeUnix = earned ? Math.Max(remote.EarnedTimeUnix, SteamAppIdResolver.NormalizeUnixSeconds(state.EarnedTimeUnix)) : 0
                    };
                }
                achievements[id] = remote;
            }

            foreach (var localAchievement in local?.Achievements ?? [])
                if (!achievements.ContainsKey(localAchievement.Id)) achievements[localAchievement.Id] = localAchievement;

            if (achievements.Count == 0) continue;
            var source = local?.SourcePath ?? installation.ManifestPath;
            return new PlatformGameSnapshot(Platform, gameId, installation.DisplayName, source,
                achievements.Values.ToArray(), officialStates is { Count: > 0 } ? "epic-official-api" : "epic-public-api");
        }

        return null;
    }

    private async Task<Dictionary<string, PlatformAchievement>?> FetchOfficialPlayerProgressAsync(
        string accessToken,
        string accountId,
        string productId,
        CancellationToken cancellationToken)
    {
        try
        {
            var url = $"https://api.epicgames.dev/epic/achievements/v1/players/{Uri.EscapeDataString(accountId)}/achievements/product/{Uri.EscapeDataString(productId)}?locale=en";
            using var response = await SendGetWithRateLimitAsync(url, accessToken, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            if (!doc.RootElement.TryGetProperty("achievements", out var arr)
                || arr.ValueKind != System.Text.Json.JsonValueKind.Array)
                return null;

            var states = new Dictionary<string, PlatformAchievement>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in arr.EnumerateArray())
            {
                if (entry.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                var id = ReadString(entry, "name", "achievementId", "id");
                if (string.IsNullOrWhiteSpace(id)) continue;
                var unlocked = ReadBool(entry, "unlocked") || ReadBool(entry, "earned");
                var unlockTimeStr = ReadString(entry, "unlockTime", "dateUnlocked", "earnedAt");
                var unlockTime = unlocked ? SteamAppIdResolver.NormalizeUnixSeconds(unlockTimeStr) : 0L;
                states[id] = new PlatformAchievement(id, Earned: unlocked, EarnedTimeUnix: unlockTime, EarnedKnown: true);
            }
            return states;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private async Task<HttpResponseMessage> SendGetWithRateLimitAsync(
        string url,
        string? bearerToken,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
            if (!string.IsNullOrWhiteSpace(bearerToken))
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);

            var response = await _httpClient!.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests && attempt == 0)
            {
                var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2);
                if (delay > TimeSpan.FromSeconds(15)) delay = TimeSpan.FromSeconds(15);
                response.Dispose();
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                continue;
            }
            return response;
        }

        throw new HttpRequestException("Epic API rate limit exceeded.");
    }

    private static PlatformGameSnapshot? FindLocalGame(IReadOnlyDictionary<string, PlatformGameSnapshot> localGames,
        EpicInstalledGame installation, string gameId)
    {
        foreach (var key in new[] { gameId, installation.AppName, installation.CatalogItemId, installation.Namespace })
            if (!string.IsNullOrWhiteSpace(key) && localGames.TryGetValue(key, out var match)) return match;
        return null;
    }

    private static EpicInstalledGame? TryReadManifest(string path)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path));
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
            var root = document.RootElement;
            var catalogItemId = ReadString(root, "CatalogItemId", "catalogItemId");
            var appName = ReadString(root, "AppName", "appName");
            if (string.IsNullOrWhiteSpace(catalogItemId) && string.IsNullOrWhiteSpace(appName)) return null;
            return new EpicInstalledGame(path, catalogItemId, appName,
                ReadString(root, "DisplayName", "displayName", "LaunchExecutable", "launchExecutable"),
                ReadString(root, "CatalogNamespace", "Namespace", "namespace"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(System.Text.Json.JsonElement element, params string[] names)
    {
        foreach (var name in names)
            if (element.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString())) return value.GetString()!.Trim();
        return null;
    }

    private static double? ReadRarity(System.Text.Json.JsonElement achievement, System.Text.Json.JsonElement wrapper)
    {
        foreach (var element in new[] { achievement, wrapper })
            if (element.TryGetProperty("rarity", out var rarity) && rarity.ValueKind == System.Text.Json.JsonValueKind.Object
                && rarity.TryGetProperty("percent", out var percent) && percent.TryGetDouble(out var value)
                && value is >= 0 and <= 100) return value;
        return null;
    }

    private static bool ReadBool(System.Text.Json.JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.True;

    private sealed record EpicInstalledGame(string ManifestPath, string? CatalogItemId, string? AppName,
        string? DisplayName, string? Namespace);

    private static string? NearestDirectory(string file)
    {
        var directory = Path.GetDirectoryName(file);
        for (var depth = 0; directory is not null && depth < 4; depth++, directory = Path.GetDirectoryName(directory))
        {
            var name = Path.GetFileName(directory);
            if (!string.IsNullOrWhiteSpace(name) && !name.Equals("NemirtingasEpicEmu", StringComparison.OrdinalIgnoreCase)) return name;
        }
        return null;
    }

    private static IEnumerable<string> Enumerate(string root, string name, int maxDepth)
    {
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));
        var visited = 0;
        while (pending.Count > 0 && visited++ < 10_000)
        {
            var (directory, depth) = pending.Pop();
            string[] files;
            string[] children;
            try
            {
                files = Directory.GetFiles(directory, name);
                children = Directory.GetDirectories(directory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                continue;
            }

            foreach (var file in files) yield return file;
            if (depth >= maxDepth) continue;
            foreach (var child in children)
            {
                try
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) pending.Push((child, depth + 1));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            }
        }
    }
}

/// <summary>Ubisoft Connect / Uplay reader. Reads official spool protobuf files (<c>spool\&lt;userId&gt;\&lt;productId&gt;.spool</c>),
/// local <c>cache\achievements</c> definitions, opt-in RUNE <c>achievements.cfg</c>, and opt-in LumaPlay registry entries.</summary>
public sealed class UplayDataAdapter(PlatformSourceOptions? defaults = null) : PlatformDataAdapter("uplay", "Ubisoft Connect / Uplay")
{
    private readonly PlatformSourceOptions _defaults = defaults ?? PlatformSourceOptions.CreateDefault();

    public override bool IsConfigured => IsConfiguredFor(_defaults);

    public override bool IsConfiguredFor(PlatformSourceOptions options) =>
        SpoolRoots(options).Any(Directory.Exists)
        || CacheRoots(options).Any(Directory.Exists)
        || (!string.IsNullOrWhiteSpace(options.UplayRuneRoot) && Directory.Exists(options.UplayRuneRoot))
        || (options.LumaPlayWatcherEnabled && OperatingSystem.IsWindows());

    public override string? ConfigurationHint =>
        "Sign in to Ubisoft Connect (%LOCALAPPDATA%\\Ubisoft Game Launcher\\spool), or optionally point RUNE Ubisoft Connect to its folder.";

    private static IEnumerable<string> SpoolRoots(PlatformSourceOptions options)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new[]
            {
                options.UplayRoot,
                Path.Combine(local, "Ubisoft Game Launcher", "spool"),
                Path.Combine(local, "Ubisoft Game Launcher", "savegames")
            }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path!))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> CacheRoots(PlatformSourceOptions options)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(options.UplayCacheRoot)) candidates.Add(options.UplayCacheRoot);
        if (!string.IsNullOrWhiteSpace(options.UplayRoot))
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(options.UplayRoot));
            if (!string.IsNullOrWhiteSpace(parent))
                candidates.Add(Path.Combine(parent, "cache", "achievements"));
        }
        candidates.Add(Path.Combine(local, "Ubisoft Game Launcher", "cache", "achievements"));
        candidates.Add(Path.Combine(programFilesX86, "Ubisoft", "Ubisoft Game Launcher", "cache", "achievements"));
        return candidates
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    public override Task<PlatformCollectResult> CollectAsync(PlatformSourceOptions options, CancellationToken cancellationToken = default)
    {
        var messages = new List<string>();
        var games = new Dictionary<string, PlatformGameSnapshot>(StringComparer.OrdinalIgnoreCase);

        // 1. Load optional Ubisoft local cache definitions (numeric ID -> name/description)
        var cacheSchemas = LoadCacheSchemas(CacheRoots(options).Where(Directory.Exists));

        // 2. Read official Ubisoft Connect spool files
        var spoolRoots = SpoolRoots(options).Where(Directory.Exists).ToArray();
        foreach (var root in spoolRoots)
        {
            foreach (var file in Enumerate(root, "*.spool", 8))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var records = ReadSpool(file);
                    if (records.Count == 0) continue;
                    var gameId = Path.GetFileNameWithoutExtension(file);
                    if (string.IsNullOrWhiteSpace(gameId)) continue;
                    records = EnrichWithCache(records, cacheSchemas.GetValueOrDefault(gameId));
                    var existing = games.GetValueOrDefault(gameId);
                    var merged = Merge(existing?.Achievements ?? [], records);
                    games[gameId] = new PlatformGameSnapshot(Platform, gameId, existing?.Title, file, merged, "ubisoft-spool");
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    messages.Add($"Uplay: skipped {Path.GetFileName(file)} ({exception.GetType().Name}).");
                }
            }
        }

        // 3. Opt-in RUNE Ubisoft Connect (<root>\achievements\<UserID>\<Uplay AppID>\achievements.cfg, ignoring savegames)
        if (!string.IsNullOrWhiteSpace(options.UplayRuneRoot) && Directory.Exists(options.UplayRuneRoot))
        {
            foreach (var cfg in DiscoverRuneAchievementConfigs(options.UplayRuneRoot, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var gameId = Path.GetFileName(Path.GetDirectoryName(cfg));
                    if (string.IsNullOrWhiteSpace(gameId)) continue;
                    var records = ReadRuneAchievementsCfg(cfg);
                    if (records.Count == 0) continue;
                    records = EnrichWithCache(records, cacheSchemas.GetValueOrDefault(gameId));
                    var existing = games.GetValueOrDefault(gameId);
                    var merged = Merge(existing?.Achievements ?? [], records);
                    games[gameId] = new PlatformGameSnapshot(Platform, gameId, existing?.Title, cfg, merged, "uplay-rune");
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
                {
                    messages.Add($"Uplay RUNE: skipped {Path.GetFileName(cfg)} ({exception.GetType().Name}).");
                }
            }
        }

        // 4. Opt-in LumaPlay Windows registry entries (HKCU\SOFTWARE\LumaPlay\<Player>\<AppId>\Achievements)
        if (options.LumaPlayWatcherEnabled && OperatingSystem.IsWindows())
        {
            try
            {
                foreach (var snapshot in LumaPlayRegistryReader.ReadAll())
                {
                    var records = EnrichWithCache(snapshot.Achievements, cacheSchemas.GetValueOrDefault(snapshot.GameId));
                    var existing = games.GetValueOrDefault(snapshot.GameId);
                    var merged = Merge(existing?.Achievements ?? [], records);
                    games[snapshot.GameId] = new PlatformGameSnapshot(Platform, snapshot.GameId, existing?.Title ?? snapshot.Title, snapshot.SourcePath, merged, "lumaplay");
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or UnauthorizedAccessException or IOException)
            {
                messages.Add($"Uplay LumaPlay: could not read registry ({ex.GetType().Name}).");
            }
        }

        if (games.Count == 0 && spoolRoots.Length == 0 && string.IsNullOrWhiteSpace(options.UplayRuneRoot))
            return Task.FromResult(new PlatformCollectResult(Platform, [], ["No Ubisoft Connect achievement folder was found."]));

        return Task.FromResult(new PlatformCollectResult(Platform, games.Values.ToList(), messages));
    }

    private static IEnumerable<string> DiscoverRuneAchievementConfigs(string runeRoot, CancellationToken cancellationToken)
    {
        var fullRoot = Path.GetFullPath(runeRoot);
        var searchBase = Directory.Exists(Path.Combine(fullRoot, "achievements"))
            ? Path.Combine(fullRoot, "achievements")
            : fullRoot;

        foreach (var file in Enumerate(searchBase, "achievements.cfg", 4))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Explicitly ignore RUNE's sibling savegames tree.
            var relative = Path.GetRelativePath(fullRoot, file);
            var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Any(segment => string.Equals(segment, "savegames", StringComparison.OrdinalIgnoreCase)))
                continue;
            yield return file;
        }
    }

    /// <summary>Parses RUNE Ubisoft Connect <c>achievements.cfg</c> (INI sections, key=value pairs, or JSON).</summary>
    public static IReadOnlyList<PlatformAchievement> ReadRuneAchievementsCfg(string path) =>
        ParseRuneAchievementsCfgText(File.ReadAllText(path));

    public static IReadOnlyList<PlatformAchievement> ParseRuneAchievementsCfgText(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return [];
        var trimmed = content.Trim();

        // JSON variant
        if (trimmed.StartsWith('{') || (trimmed.StartsWith('[') && trimmed.EndsWith(']') && trimmed.Contains('{')))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(trimmed,
                    new System.Text.Json.JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = System.Text.Json.JsonCommentHandling.Skip });
                var list = new List<PlatformAchievement>();
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.Object)
                        {
                            var earned = ReadRuneJsonBool(prop.Value, "earned", "unlocked", "achieved", "state") ?? false;
                            var time = ReadRuneJsonTime(prop.Value, "earned_time", "unlock_time", "unlocktime", "timestamp", "time");
                            var name = ReadRuneJsonString(prop.Value, "name", "displayName");
                            var desc = ReadRuneJsonString(prop.Value, "description", "desc");
                            list.Add(new PlatformAchievement(prop.Name.Trim(), name, desc, earned, earned ? time : 0));
                        }
                        else if (SteamAppIdResolver.NormalizeEarnedFlag(prop.Value) is { } boolVal)
                        {
                            list.Add(new PlatformAchievement(prop.Name.Trim(), Earned: boolVal));
                        }
                    }
                }
                else if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var item in doc.RootElement.EnumerateArray())
                    {
                        if (item.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                        var id = ReadRuneJsonString(item, "id", "achievementId", "name");
                        if (string.IsNullOrWhiteSpace(id)) continue;
                        var earned = ReadRuneJsonBool(item, "earned", "unlocked", "achieved", "state") ?? true;
                        var time = ReadRuneJsonTime(item, "earned_time", "unlock_time", "unlocktime", "timestamp", "time");
                        list.Add(new PlatformAchievement(id, ReadRuneJsonString(item, "displayName", "title"),
                            ReadRuneJsonString(item, "description", "desc"), earned, earned ? time : 0));
                    }
                }
                return list;
            }
            catch (System.Text.Json.JsonException)
            {
                // Fall back to INI parsing if not valid JSON
            }
        }

        // INI / key-value variant
        var results = new Dictionary<string, PlatformAchievement>(StringComparer.OrdinalIgnoreCase);
        string? currentSection = null;
        var hasSections = false;
        var sectionData = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var flatEntries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in trimmed.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';')) continue;
            if (line.StartsWith('[') && line.EndsWith(']') && line.Length > 2)
            {
                currentSection = line[1..^1].Trim();
                hasSections = true;
                if (!sectionData.ContainsKey(currentSection))
                    sectionData[currentSection] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim().Trim('"');
            var val = line[(eq + 1)..].Trim().Trim('"');
            if (hasSections && currentSection is not null)
                sectionData[currentSection][key] = val;
            else
                flatEntries[key] = val;
        }

        if (hasSections)
        {
            foreach (var (sectionId, fields) in sectionData)
            {
                if (sectionId.Equals("Settings", StringComparison.OrdinalIgnoreCase)
                    || sectionId.Equals("General", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var (k, v) in fields)
                    {
                        if (SteamAppIdResolver.NormalizeEarnedFlag(v) is { } flag)
                            results[k] = new PlatformAchievement(k, Earned: flag);
                    }
                    continue;
                }

                bool? earnedFlag = null;
                foreach (var key in new[] { "unlocked", "earned", "achieved", "state", "status" })
                {
                    if (fields.TryGetValue(key, out var rawVal))
                    {
                        earnedFlag = SteamAppIdResolver.NormalizeEarnedFlag(rawVal);
                        if (earnedFlag.HasValue) break;
                    }
                }

                long time = 0;
                foreach (var key in new[] { "unlocktime", "unlock_time", "earned_time", "earnedtime", "timestamp", "time" })
                {
                    if (fields.TryGetValue(key, out var rawTime))
                    {
                        time = SteamAppIdResolver.NormalizeUnixSeconds(rawTime);
                        if (time > 0) break;
                    }
                }

                var earned = earnedFlag ?? time > 0;
                fields.TryGetValue("name", out var achName);
                fields.TryGetValue("description", out var achDesc);
                results[sectionId] = new PlatformAchievement(sectionId, achName, achDesc, earned, earned ? time : 0);
            }
        }
        else
        {
            foreach (var (key, val) in flatEntries)
            {
                if (SteamAppIdResolver.NormalizeEarnedFlag(val) is { } flag)
                {
                    results[key] = new PlatformAchievement(key, Earned: flag);
                }
                else
                {
                    var ts = SteamAppIdResolver.NormalizeUnixSeconds(val);
                    if (ts > 0) results[key] = new PlatformAchievement(key, Earned: true, EarnedTimeUnix: ts);
                }
            }
        }

        return results.Values.OrderBy(item => item.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool? ReadRuneJsonBool(System.Text.Json.JsonElement el, params string[] names)
    {
        foreach (var n in names)
            if (el.TryGetProperty(n, out var prop))
                return SteamAppIdResolver.NormalizeEarnedFlag(prop);
        return null;
    }

    private static long ReadRuneJsonTime(System.Text.Json.JsonElement el, params string[] names)
    {
        foreach (var n in names)
            if (el.TryGetProperty(n, out var prop))
            {
                var ts = SteamAppIdResolver.NormalizeUnixSeconds(prop);
                if (ts > 0) return ts;
            }
        return 0;
    }

    private static string? ReadRuneJsonString(System.Text.Json.JsonElement el, params string[] names)
    {
        foreach (var n in names)
            if (el.TryGetProperty(n, out var prop) && prop.ValueKind == System.Text.Json.JsonValueKind.String)
                return prop.GetString()?.Trim();
        return null;
    }

    private static Dictionary<string, Dictionary<string, (string? Name, string? Description)>> LoadCacheSchemas(IEnumerable<string> cacheRoots)
    {
        var result = new Dictionary<string, Dictionary<string, (string? Name, string? Description)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in cacheRoots)
        {
            foreach (var file in Enumerate(root, "*", 2))
            {
                var stem = Path.GetFileNameWithoutExtension(file);
                if (string.IsNullOrWhiteSpace(stem)) continue;
                var productId = stem.Split('_')[0];
                if (!productId.All(char.IsAsciiDigit)) continue;
                try
                {
                    var text = File.ReadAllText(file);
                    if (!text.TrimStart().StartsWith('{') && !text.TrimStart().StartsWith('[')) continue;
                    using var doc = System.Text.Json.JsonDocument.Parse(text);
                    var map = result.GetValueOrDefault(productId) ?? new Dictionary<string, (string?, string?)>(StringComparer.OrdinalIgnoreCase);
                    if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var item in doc.RootElement.EnumerateArray())
                        {
                            var id = ReadRuneJsonString(item, "id", "achievementId");
                            if (string.IsNullOrWhiteSpace(id)) continue;
                            map[id] = (ReadRuneJsonString(item, "name", "title"), ReadRuneJsonString(item, "description", "detail"));
                        }
                    }
                    else if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        foreach (var prop in doc.RootElement.EnumerateObject())
                        {
                            if (prop.Value.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                            map[prop.Name] = (ReadRuneJsonString(prop.Value, "name", "title", "displayName"),
                                ReadRuneJsonString(prop.Value, "description", "detail"));
                        }
                    }
                    if (map.Count > 0) result[productId] = map;
                }
                catch { }
            }
        }
        return result;
    }

    private static IReadOnlyList<PlatformAchievement> EnrichWithCache(
        IReadOnlyList<PlatformAchievement> records,
        Dictionary<string, (string? Name, string? Description)>? schema)
    {
        if (schema is null || schema.Count == 0) return records;
        return records.Select(r =>
        {
            if (!schema.TryGetValue(r.Id, out var meta)) return r;
            return r with
            {
                Name = string.IsNullOrWhiteSpace(r.Name) ? meta.Name : r.Name,
                Description = string.IsNullOrWhiteSpace(r.Description) ? meta.Description : r.Description
            };
        }).ToArray();
    }

    private static List<PlatformAchievement> Merge(IReadOnlyList<PlatformAchievement> existing, IReadOnlyList<PlatformAchievement> incoming)
    {
        var merged = existing.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var item in incoming)
        {
            if (merged.TryGetValue(item.Id, out var current))
            {
                var earned = current.Earned || item.Earned;
                merged[item.Id] = current with
                {
                    Name = current.Name ?? item.Name,
                    Description = current.Description ?? item.Description,
                    Earned = earned,
                    EarnedTimeUnix = earned ? Math.Max(current.EarnedTimeUnix, item.EarnedTimeUnix) : 0,
                    EarnedKnown = current.EarnedKnown || item.EarnedKnown
                };
            }
            else
            {
                merged[item.Id] = item;
            }
        }
        return merged.Values.OrderBy(item => item.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<string> Enumerate(string root, string pattern, int maxDepth)
    {
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));
        var visited = 0;
        while (pending.Count > 0 && visited++ < 10_000)
        {
            var (directory, depth) = pending.Pop();
            string[] files;
            string[] children;
            try
            {
                files = Directory.GetFiles(directory, pattern);
                children = Directory.GetDirectories(directory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                continue;
            }

            foreach (var file in files) yield return file;
            if (depth >= maxDepth) continue;
            foreach (var child in children)
            {
                try { if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) pending.Push((child, depth + 1)); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            }
        }
    }

    /// <summary>Reads achievement records from a Ubisoft spool protobuf stream.</summary>
    public static IReadOnlyList<PlatformAchievement> ReadSpool(string path) => ReadSpool(File.ReadAllBytes(path));

    public static IReadOnlyList<PlatformAchievement> ReadSpool(ReadOnlySpan<byte> buffer)
    {
        var results = new Dictionary<string, PlatformAchievement>(StringComparer.OrdinalIgnoreCase);
        var offset = 0;
        while (offset < buffer.Length)
        {
            var tag = ReadVarint(buffer, ref offset);
            if (tag is null) break;
            var fieldNumber = (int)(tag.Value >> 3);
            var wireType = (int)(tag.Value & 0x07);
            if (wireType == 2)
            {
                var length = ReadVarint(buffer, ref offset);
                if (length is null || length < 0 || offset + length > buffer.Length) break;
                if (fieldNumber == 1)
                {
                    var start = offset;
                    var end = offset + (int)length.Value;
                    var achievementId = FindVarintField(buffer, 1, start, end);
                    var earnedTime = FindVarintField(buffer, 2, start, end);
                    if (achievementId is > 0 && earnedTime is > 0)
                    {
                        var seconds = SteamAppIdResolver.NormalizeUnixSeconds(earnedTime.Value);
                        var id = achievementId.Value.ToString(CultureInfo.InvariantCulture);
                        results[id] = new PlatformAchievement(id, Earned: true, EarnedTimeUnix: seconds);
                    }
                    offset = end;
                    continue;
                }

                offset += (int)length.Value;
                continue;
            }

            offset = SkipField(buffer, offset, wireType);
        }

        return results.Values.OrderBy(item => item.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static long? FindVarintField(ReadOnlySpan<byte> buffer, int fieldNumber, int start, int end)
    {
        var offset = start;
        while (offset < end)
        {
            var tag = ReadVarint(buffer, ref offset);
            if (tag is null) return null;
            var field = (int)(tag.Value >> 3);
            var wireType = (int)(tag.Value & 0x07);
            if (wireType == 0)
            {
                var value = ReadVarint(buffer, ref offset);
                if (value is null) return null;
                if (field == fieldNumber) return value.Value;
            }
            else if (wireType == 2)
            {
                var length = ReadVarint(buffer, ref offset);
                if (length is null) return null;
                offset += (int)length.Value;
            }
            else if (wireType == 5) offset += 4;
            else if (wireType == 1) offset += 8;
            else return null;
        }
        return null;
    }

    private static long? ReadVarint(ReadOnlySpan<byte> buffer, ref int offset)
    {
        long value = 0;
        for (var index = 0; index < 10; index++)
        {
            if (offset >= buffer.Length) return null;
            var current = buffer[offset++];
            value |= (long)(current & 0x7F) << (index * 7);
            if ((current & 0x80) == 0) return value;
        }
        return null;
    }

    private static int SkipField(ReadOnlySpan<byte> buffer, int offset, int wireType)
    {
        return wireType switch
        {
            0 => ReadVarint(buffer, ref offset) is null ? buffer.Length : offset,
            1 => offset + 8,
            5 => offset + 4,
            _ => buffer.Length
        };
    }

    internal static long NormalizeEpochSeconds(long value) => SteamAppIdResolver.NormalizeUnixSeconds(value);
}

/// <summary>Reads LumaPlay achievement states from <c>HKCU\SOFTWARE\LumaPlay\&lt;Player&gt;\&lt;AppId&gt;\Achievements</c> on Windows.</summary>
public static class LumaPlayRegistryReader
{
    public static IReadOnlyList<PlatformGameSnapshot> ReadAll()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var snapshots = new List<PlatformGameSnapshot>();
        foreach (var player in EnumerateSubKeys(@"SOFTWARE\LumaPlay"))
        {
            foreach (var appId in EnumerateSubKeys($@"SOFTWARE\LumaPlay\{player}"))
            {
                if (!appId.All(char.IsAsciiDigit)) continue;
                var achKey = $@"SOFTWARE\LumaPlay\{player}\{appId}\Achievements";
                var values = ReadKeyValues(achKey);
                var achievements = ParseValues(values);
                if (achievements.Count > 0)
                {
                    snapshots.Add(new PlatformGameSnapshot("uplay", appId, null, $@"HKCU\{achKey}", achievements, "lumaplay"));
                }
            }
        }
        return snapshots;
    }

    public static IReadOnlyList<PlatformAchievement> ParseValues(IReadOnlyDictionary<string, object?> values)
    {
        var results = new Dictionary<string, PlatformAchievement>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, raw) in values)
        {
            if (name.EndsWith("_Report", StringComparison.OrdinalIgnoreCase)) continue;
            var id = name.StartsWith("ACH_", StringComparison.OrdinalIgnoreCase) ? name[4..] : name;
            if (string.IsNullOrWhiteSpace(id)) continue;

            var flag = SteamAppIdResolver.NormalizeEarnedFlag(raw);
            var timestamp = raw is long l ? SteamAppIdResolver.NormalizeUnixSeconds(l)
                : raw is int i && i > 1_000_000_000 ? (long)i
                : raw is string s ? SteamAppIdResolver.NormalizeUnixSeconds(s)
                : 0L;
            var earned = flag ?? (timestamp > 0);
            results[id] = new PlatformAchievement(id, Earned: earned, EarnedTimeUnix: earned ? timestamp : 0);
        }
        return results.Values.OrderBy(a => a.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyList<string> EnumerateSubKeys(string subKeyPath)
    {
        var names = new List<string>();
        if (RegOpenKeyEx(HKeyCurrentUser, subKeyPath, 0, KeyRead, out var handle) != 0) return names;
        try
        {
            var buffer = new char[256];
            for (uint index = 0; ; index++)
            {
                uint size = (uint)buffer.Length;
                var status = RegEnumKeyEx(handle, index, buffer, ref size, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (status != 0) break;
                names.Add(new string(buffer, 0, (int)size));
            }
        }
        finally { RegCloseKey(handle); }
        return names;
    }

    private static Dictionary<string, object?> ReadKeyValues(string subKeyPath)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (RegOpenKeyEx(HKeyCurrentUser, subKeyPath, 0, KeyRead, out var handle) != 0) return result;
        try
        {
            var nameBuffer = new char[256];
            var dataBuffer = new byte[1024];
            for (uint index = 0; ; index++)
            {
                uint nameLen = (uint)nameBuffer.Length;
                uint dataLen = (uint)dataBuffer.Length;
                uint type = 0;
                var status = RegEnumValue(handle, index, nameBuffer, ref nameLen, IntPtr.Zero, ref type, dataBuffer, ref dataLen);
                if (status != 0) break;
                var valueName = new string(nameBuffer, 0, (int)nameLen);
                object? parsed = type switch
                {
                    4 when dataLen >= 4 => BitConverter.ToInt32(dataBuffer, 0),
                    11 when dataLen >= 8 => BitConverter.ToInt64(dataBuffer, 0),
                    1 when dataLen >= 2 => System.Text.Encoding.Unicode.GetString(dataBuffer, 0, (int)dataLen).TrimEnd('\0'),
                    _ => null
                };
                result[valueName] = parsed;
            }
        }
        finally { RegCloseKey(handle); }
        return result;
    }

    private static readonly IntPtr HKeyCurrentUser = new(unchecked((int)0x80000001));
    private const uint KeyRead = 0x20019;

    [System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int RegOpenKeyEx(IntPtr hKey, string lpSubKey, uint ulOptions, uint samDesired, out IntPtr phkResult);

    [System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int RegEnumKeyEx(IntPtr hKey, uint dwIndex, char[] lpName, ref uint lpcchName,
        IntPtr lpReserved, IntPtr lpClass, IntPtr lpcchClass, IntPtr lpftLastWriteTime);

    [System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int RegEnumValue(IntPtr hKey, uint dwIndex, char[] lpValueName, ref uint lpcchValueName,
        IntPtr lpReserved, ref uint lpType, byte[] lpData, ref uint lpcbData);

    [System.Runtime.InteropServices.DllImport("advapi32.dll")]
    private static extern int RegCloseKey(IntPtr hKey);
}

/// <summary>
/// EA Desktop reader. Parses achievement sets and unlock events from
/// <c>EADesktopVerbose.bak</c> and <c>EADesktopVerbose.log</c> (preserving events across log rotation).
/// </summary>
public sealed class EaDesktopDataAdapter(PlatformSourceOptions? defaults = null)
    : PlatformDataAdapter("ea", "EA Desktop (official logs)")
{
    private static readonly System.Text.RegularExpressions.Regex LogTimestampPattern = new(
        @"(\d{4}-\d{2}-\d{2}[T\s]\d{2}:\d{2}:\d{2}(?:\.\d+)?Z?)",
        System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex KeyValueEventPattern = new(
        @"\b(?:achievement(?:Id|_id|Name)?|id)\s*[=:]\s*[""']?(?<id>[A-Za-z0-9_.:-]+)[""']?",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex GameIdPattern = new(
        @"\b(?:achievementSet(?:Id|Override)?|offerId|contentId|gameId|titleId|productId)\s*[=:]\s*[""']?(?<game>[A-Za-z0-9_.:-]+)[""']?",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex ExplicitTimestampPattern = new(
        @"\b(?:timestamp|unlockTime|dateUnlocked|earnedAt|time)\s*[=:]\s*[""']?(?<ts>[^""'\s,]+)[""']?",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex TitlePattern = new(
        @"\b(?:gameTitle|displayName|title|productName)\s*[=:]\s*[""'](?<title>[^""']+)[""']",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private readonly PlatformSourceOptions _defaults = defaults ?? PlatformSourceOptions.CreateDefault();

    public override bool IsConfigured => IsConfiguredFor(_defaults);

    public override bool IsConfiguredFor(PlatformSourceOptions options) =>
        LogRoots(options).Any(root => File.Exists(Path.Combine(root, "EADesktopVerbose.log"))
                                      || File.Exists(Path.Combine(root, "EADesktopVerbose.bak")));

    public override string? ConfigurationHint =>
        "Add %LOCALAPPDATA%\\Electronic Arts\\EA Desktop\\Logs on Platforms so EADesktopVerbose.log and .bak can be read.";

    private static IEnumerable<string> LogRoots(PlatformSourceOptions options)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new[] { options.EaRoot, Path.Combine(local, "Electronic Arts", "EA Desktop", "Logs") }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path!))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    public override Task<PlatformCollectResult> CollectAsync(PlatformSourceOptions options, CancellationToken cancellationToken = default)
    {
        var messages = new List<string>();
        var games = new SortedDictionary<string, PlatformGameSnapshot>(StringComparer.OrdinalIgnoreCase);
        var roots = LogRoots(options).Where(Directory.Exists).ToArray();
        if (roots.Length == 0)
            return Task.FromResult(new PlatformCollectResult(Platform, [], ["The EA Desktop Logs folder was not found."]));

        foreach (var root in roots)
        {
            // Read .bak BEFORE .log so unlocks present before rotation are unioned in chronological order.
            var candidates = new[]
            {
                Path.Combine(root, "EADesktopVerbose.bak"),
                Path.Combine(root, "EADesktopVerbose.log")
            }.Where(File.Exists).ToArray();

            foreach (var file in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    foreach (var snapshot in ParseVerboseLog(file))
                    {
                        if (games.TryGetValue(snapshot.GameId, out var existing))
                        {
                            games[snapshot.GameId] = existing with
                            {
                                Title = existing.Title ?? snapshot.Title,
                                SourcePath = file,
                                Achievements = UnionAchievements(existing.Achievements, snapshot.Achievements)
                            };
                        }
                        else
                        {
                            games[snapshot.GameId] = snapshot;
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    messages.Add($"EA Desktop: skipped {Path.GetFileName(file)} ({ex.GetType().Name}).");
                }
            }
        }

        if (games.Count == 0)
            messages.Add("No EA Desktop achievement events were found in EADesktopVerbose.log or .bak.");
        return Task.FromResult(new PlatformCollectResult(Platform, games.Values.ToList(), messages));
    }

    public static IReadOnlyList<PlatformGameSnapshot> ParseVerboseLog(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return ParseVerboseLogLines(ReadLines(reader), path);

        static IEnumerable<string> ReadLines(StreamReader r)
        {
            while (r.ReadLine() is { } line) yield return line;
        }
    }

    public static IReadOnlyList<PlatformGameSnapshot> ParseVerboseLogLines(IEnumerable<string> lines, string sourcePath = "EADesktopVerbose.log")
    {
        var perGame = new Dictionary<string, (string? Title, Dictionary<string, PlatformAchievement> Achievements)>(StringComparer.OrdinalIgnoreCase);

        void Upsert(string gameId, string? title, PlatformAchievement ach)
        {
            if (string.IsNullOrWhiteSpace(gameId) || string.IsNullOrWhiteSpace(ach.Id)) return;
            if (!perGame.TryGetValue(gameId, out var bucket))
            {
                bucket = (title, new Dictionary<string, PlatformAchievement>(StringComparer.OrdinalIgnoreCase));
                perGame[gameId] = bucket;
            }
            else if (string.IsNullOrWhiteSpace(bucket.Title) && !string.IsNullOrWhiteSpace(title))
            {
                bucket.Title = title;
                perGame[gameId] = bucket;
            }

            if (bucket.Achievements.TryGetValue(ach.Id, out var existing))
            {
                var earned = existing.Earned || ach.Earned;
                bucket.Achievements[ach.Id] = existing with
                {
                    Name = existing.Name ?? ach.Name,
                    Description = existing.Description ?? ach.Description,
                    Earned = earned,
                    EarnedTimeUnix = earned ? Math.Max(existing.EarnedTimeUnix, ach.EarnedTimeUnix) : 0,
                    Hidden = existing.Hidden || ach.Hidden
                };
            }
            else
            {
                bucket.Achievements[ach.Id] = ach;
            }
        }

        foreach (var rawLine in lines)
        {
            if (string.IsNullOrWhiteSpace(rawLine)) continue;
            if (!rawLine.Contains("achievement", StringComparison.OrdinalIgnoreCase)) continue;

            var lineTimestamp = 0L;
            var tsMatch = LogTimestampPattern.Match(rawLine);
            if (tsMatch.Success)
                lineTimestamp = SteamAppIdResolver.NormalizeUnixSeconds(tsMatch.Groups[1].Value);

            // 1. Try JSON payload embedded in the log line (e.g. EA GraphQL/SDK response or unlock event).
            var jsonStart = rawLine.IndexOf('{');
            if (jsonStart >= 0)
            {
                var jsonSlice = rawLine[jsonStart..];
                if (TryParseEmbeddedJson(jsonSlice, lineTimestamp, Upsert))
                    continue;
            }

            // 2. Fallback: structured key=value log lines (e.g. GrantAchievement / AchievementGranted / awarded).
            var gameMatch = GameIdPattern.Match(rawLine);
            var idMatch = KeyValueEventPattern.Match(rawLine);
            if (!gameMatch.Success || !idMatch.Success) continue;

            var gameId = gameMatch.Groups["game"].Value.Trim();
            var achId = idMatch.Groups["id"].Value.Trim();
            var titleMatch = TitlePattern.Match(rawLine);
            var title = titleMatch.Success ? titleMatch.Groups["title"].Value.Trim() : null;

            var explicitTsMatch = ExplicitTimestampPattern.Match(rawLine);
            var explicitTs = 0L;
            if (explicitTsMatch.Success)
                explicitTs = SteamAppIdResolver.NormalizeUnixSeconds(explicitTsMatch.Groups["ts"].Value);

            var achTimestamp = explicitTs > 0 ? explicitTs : lineTimestamp;

            var isUnlockLine = rawLine.Contains("grant", StringComparison.OrdinalIgnoreCase)
                               || rawLine.Contains("unlock", StringComparison.OrdinalIgnoreCase)
                               || rawLine.Contains("award", StringComparison.OrdinalIgnoreCase)
                               || rawLine.Contains("earned=true", StringComparison.OrdinalIgnoreCase)
                               || rawLine.Contains("earned=1", StringComparison.OrdinalIgnoreCase)
                               || rawLine.Contains("completed=true", StringComparison.OrdinalIgnoreCase);
            var isLockedLine = rawLine.Contains("earned=false", StringComparison.OrdinalIgnoreCase)
                               || rawLine.Contains("earned=0", StringComparison.OrdinalIgnoreCase)
                               || rawLine.Contains("unlocked=false", StringComparison.OrdinalIgnoreCase);
            var earned = (isUnlockLine || achTimestamp > 0) && !isLockedLine;
            Upsert(gameId, title, new PlatformAchievement(achId, Earned: earned, EarnedTimeUnix: earned ? achTimestamp : 0));
        }

        return perGame
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new PlatformGameSnapshot(
                "ea",
                kv.Key,
                kv.Value.Title,
                sourcePath,
                kv.Value.Achievements.Values.OrderBy(a => a.Id, StringComparer.OrdinalIgnoreCase).ToArray(),
                "ea-verbose-log"))
            .ToArray();
    }

    private static bool TryParseEmbeddedJson(
        string jsonSlice,
        long fallbackTimestamp,
        Action<string, string?, PlatformAchievement> upsert)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(jsonSlice);
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return false;

            var gameId = ReadAnyString(root, "achievementSet", "achievementSetId", "achievementSetOverride", "offerId", "contentId", "gameId", "titleId", "productId");
            var gameTitle = ReadAnyString(root, "gameTitle", "titleName", "productName");

            if (root.TryGetProperty("achievements", out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                if (string.IsNullOrWhiteSpace(gameId)) return false;
                foreach (var entry in arr.EnumerateArray())
                {
                    if (entry.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                    var ach = ParseJsonAchievement(entry, fallbackTimestamp);
                    if (ach is not null) upsert(gameId, gameTitle, ach);
                }
                return true;
            }

            if (root.TryGetProperty("achievementSets", out var sets) && sets.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var setObj in sets.EnumerateArray())
                {
                    if (setObj.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                    var setId = ReadAnyString(setObj, "id", "achievementSetId", "name") ?? gameId;
                    if (string.IsNullOrWhiteSpace(setId)) continue;
                    if (setObj.TryGetProperty("achievements", out var innerArr) && innerArr.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var entry in innerArr.EnumerateArray())
                        {
                            var ach = ParseJsonAchievement(entry, fallbackTimestamp);
                            if (ach is not null) upsert(setId, gameTitle, ach);
                        }
                    }
                }
                return true;
            }

            var singleId = ReadAnyString(root, "achievementId", "id", "apiName", "achievementName");
            if (!string.IsNullOrWhiteSpace(gameId) && !string.IsNullOrWhiteSpace(singleId))
            {
                var ach = ParseJsonAchievement(root, fallbackTimestamp);
                if (ach is not null)
                {
                    upsert(gameId, gameTitle, ach);
                    return true;
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
        return false;
    }

    private static PlatformAchievement? ParseJsonAchievement(System.Text.Json.JsonElement entry, long fallbackTimestamp)
    {
        var id = ReadAnyString(entry, "id", "achievementId", "apiName", "name", "achievementName");
        if (string.IsNullOrWhiteSpace(id)) return null;
        var name = ReadAnyString(entry, "name", "displayName", "title");
        var desc = ReadAnyString(entry, "description", "howto", "lockedDescription");

        bool? earnedFlag = null;
        foreach (var field in new[] { "earned", "unlocked", "granted", "complete", "completed", "achieved" })
        {
            if (entry.TryGetProperty(field, out var prop))
            {
                earnedFlag = SteamAppIdResolver.NormalizeEarnedFlag(prop);
                if (earnedFlag.HasValue) break;
            }
        }
        if (!earnedFlag.HasValue && entry.TryGetProperty("count", out var cnt) && cnt.TryGetInt32(out var cVal)
            && entry.TryGetProperty("total", out var tot) && tot.TryGetInt32(out var tVal) && tVal > 0)
        {
            earnedFlag = cVal >= tVal;
        }

        long ts = 0;
        foreach (var field in new[] { "dateUnlocked", "unlockDate", "earnedAt", "earned_time", "timestamp", "time", "unlockTime" })
        {
            if (entry.TryGetProperty(field, out var prop))
            {
                ts = SteamAppIdResolver.NormalizeUnixSeconds(prop);
                if (ts > 0) break;
            }
        }

        var earned = earnedFlag ?? ts > 0;
        if (earned && ts <= 0) ts = fallbackTimestamp;
        var hidden = entry.TryGetProperty("hidden", out var hid) && hid.ValueKind == System.Text.Json.JsonValueKind.True;
        return new PlatformAchievement(id, name, desc, earned, earned ? ts : 0, Hidden: hidden);
    }

    private static string? ReadAnyString(System.Text.Json.JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var prop))
            {
                if (prop.ValueKind == System.Text.Json.JsonValueKind.String && !string.IsNullOrWhiteSpace(prop.GetString()))
                    return prop.GetString()!.Trim();
                if (prop.ValueKind == System.Text.Json.JsonValueKind.Number)
                    return prop.GetRawText();
            }
        }
        return null;
    }

    private static IReadOnlyList<PlatformAchievement> UnionAchievements(
        IReadOnlyList<PlatformAchievement> existing,
        IReadOnlyList<PlatformAchievement> incoming)
    {
        var merged = existing.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var item in incoming)
        {
            if (merged.TryGetValue(item.Id, out var current))
            {
                var earned = current.Earned || item.Earned;
                merged[item.Id] = current with
                {
                    Name = current.Name ?? item.Name,
                    Description = current.Description ?? item.Description,
                    Earned = earned,
                    EarnedTimeUnix = earned ? Math.Max(current.EarnedTimeUnix, item.EarnedTimeUnix) : 0,
                    Hidden = current.Hidden || item.Hidden
                };
            }
            else
            {
                merged[item.Id] = item;
            }
        }
        return merged.Values.OrderBy(a => a.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}

/// <summary>Static helper kept for adapter parity and unit checks.</summary>
internal static class UplayBinary
{
    public static int ReadTag(ReadOnlySpan<byte> data) => data.Length == 0 ? 0 : BinaryPrimitives.ReadUInt16LittleEndian(data);
}
