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
        return LocalRoots(options).Any(Directory.Exists) || ManifestRoots(options).Any(Directory.Exists);
    }

    public override string? ConfigurationHint =>
        "Uses Epic Launcher manifests and the public achievements catalog. Unlock state is read from local emulator data when available.";

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
            var installations = ManifestRoots(options)
                .Where(Directory.Exists)
                .SelectMany(root => Enumerate(root, "*.item", 5))
                .Select(TryReadManifest)
                .Where(item => item is not null)
                .Cast<EpicInstalledGame>()
                .GroupBy(item => item.CatalogItemId ?? item.AppName, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Take(500)
                .ToArray();
            using var slots = new SemaphoreSlim(4, 4);
            var tasks = installations.Select(async installation =>
            {
                await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var snapshot = await FetchPublicSchemaAsync(installation, games, cancellationToken).ConfigureAwait(false);
                    return (Installation: installation, Snapshot: snapshot);
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidDataException)
                {
                    lock (messages) messages.Add($"Epic: public achievement lookup failed for {installation.DisplayName ?? installation.AppName} ({exception.GetType().Name}).");
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
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(options.EpicRoot)) roots.Add(options.EpicRoot);
        roots.Add(Path.Combine(local, "EpicGamesLauncher"));
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var full = Path.GetFullPath(root);
            yield return string.Equals(Path.GetFileName(full), "Manifests", StringComparison.OrdinalIgnoreCase)
                ? full
                : Path.Combine(full, "Data", "Manifests");
        }
    }

    private async Task<PlatformGameSnapshot?> FetchPublicSchemaAsync(EpicInstalledGame installation,
        IReadOnlyDictionary<string, PlatformGameSnapshot> localGames, CancellationToken cancellationToken)
    {
        foreach (var productId in new[] { installation.CatalogItemId, installation.AppName, installation.Namespace }
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var url = $"https://api.epicgames.dev/epic/achievements/v1/public/achievements/product/{Uri.EscapeDataString(productId!)}/locale/en?includeAchievements=true";
            using var response = await _httpClient!.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) continue;
            using var document = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            if (!document.RootElement.TryGetProperty("achievements", out var rows)
                || rows.ValueKind != System.Text.Json.JsonValueKind.Array || rows.GetArrayLength() == 0) continue;

            var gameId = installation.CatalogItemId ?? installation.AppName ?? productId!;
            var local = FindLocalGame(localGames, installation, gameId);
            var localById = (local?.Achievements ?? [])
                .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
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
                if (localById.TryGetValue(id, out var state))
                    remote = remote with { Earned = state.Earned, EarnedKnown = state.EarnedKnown, EarnedTimeUnix = state.EarnedTimeUnix };
                achievements[id] = remote;
            }

            foreach (var localAchievement in local?.Achievements ?? [])
                if (!achievements.ContainsKey(localAchievement.Id)) achievements[localAchievement.Id] = localAchievement;

            if (achievements.Count == 0) continue;
            var source = local?.SourcePath ?? installation.ManifestPath;
            return new PlatformGameSnapshot(Platform, gameId, installation.DisplayName, source,
                achievements.Values.ToArray(), "epic-public-api");
        }

        return null;
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

/// <summary>Ubisoft Connect / Uplay reader. Parses the protobuf achievement spool files written by
/// Ubisoft Connect and the Uplay emulators (achievement id + earned timestamp).</summary>
public sealed class UplayDataAdapter(PlatformSourceOptions? defaults = null) : PlatformDataAdapter("uplay", "Ubisoft Connect / Uplay")
{
    private readonly PlatformSourceOptions _defaults = defaults ?? PlatformSourceOptions.CreateDefault();

    public override bool IsConfigured => IsConfiguredFor(_defaults);

    public override bool IsConfiguredFor(PlatformSourceOptions options) =>
        (!string.IsNullOrWhiteSpace(options.UplayRoot) && Directory.Exists(options.UplayRoot))
        || Directory.Exists(UplaySaveGamesRoot());

    public override string? ConfigurationHint =>
        "Sign in to Ubisoft Connect so it writes spool data, or choose its achievements cache folder.";

    private static string UplaySaveGamesRoot()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(local, "Ubisoft Game Launcher", "savegames");
    }

    public override Task<PlatformCollectResult> CollectAsync(PlatformSourceOptions options, CancellationToken cancellationToken = default)
    {
        var roots = new[] { options.UplayRoot, UplaySaveGamesRoot() }
            .Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var messages = new List<string>();
        var games = new Dictionary<string, PlatformGameSnapshot>(StringComparer.OrdinalIgnoreCase);
        if (roots.Length == 0)
            return Task.FromResult(new PlatformCollectResult(Platform, [], ["No Ubisoft Connect achievement folder was found."]));

        foreach (var root in roots)
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
                    var existing = games.GetValueOrDefault(gameId);
                    var merged = Merge(existing?.Achievements ?? [], records);
                    games[gameId] = new PlatformGameSnapshot(Platform, gameId, null, file, merged);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    messages.Add($"Uplay: skipped {Path.GetFileName(file)} ({exception.GetType().Name}).");
                }
            }
        }

        return Task.FromResult(new PlatformCollectResult(Platform, games.Values.ToList(), messages));
    }

    private static List<PlatformAchievement> Merge(IReadOnlyList<PlatformAchievement> existing, IReadOnlyList<PlatformAchievement> incoming)
    {
        var merged = existing.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var item in incoming) merged[item.Id] = item;
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
            foreach (var child in children) pending.Push((child, depth + 1));
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
                        var seconds = GOGNormalize(earnedTime.Value);
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

        static long GOGNormalize(long value) => value > 10_000_000_000 ? value / 1000 : value;
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

    internal static long NormalizeEpochSeconds(long value) => value > 10_000_000_000 ? value / 1000 : value;
}

/// <summary>Static helper kept for adapter parity and unit checks.</summary>
internal static class UplayBinary
{
    public static int ReadTag(ReadOnlySpan<byte> data) => data.Length == 0 ? 0 : BinaryPrimitives.ReadUInt16LittleEndian(data);
}
