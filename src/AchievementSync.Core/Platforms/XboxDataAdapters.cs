using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AchievementSync.Core.Platforms;

/// <summary>Xbox / Xbox PC / Games for Windows Live reader. Understands XBLessness
/// <c>achievements.dat</c> records (16 bytes: id, FILETIME, flags) and local viewer JSON.</summary>
public class XboxDataAdapter(string platform, string displayName, bool includeGfwl = false)
    : PlatformDataAdapter(platform, displayName)
{
    private static readonly Regex TitleIdPattern = new(@"^[0-9A-Fa-f]{8}$", RegexOptions.Compiled);

    public override bool IsConfigured => IsConfiguredFor(PlatformSourceOptions.CreateDefault());

    public override bool IsConfiguredFor(PlatformSourceOptions options) =>
        new[] { options.XblenRoot, options.XboxPcRoot, includeGfwl ? options.GfwlRoot : null }
            .Any(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path));

    public override string? ConfigurationHint =>
        "Keep XBLessness profile data in %LocalAppData%\\XLiveLessNess (or choose its folder on the Platforms page).";

    public override Task<PlatformCollectResult> CollectAsync(PlatformSourceOptions options, CancellationToken cancellationToken = default)
    {
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(options.XblenRoot)) roots.Add(options.XblenRoot);
        if (includeGfwl && !string.IsNullOrWhiteSpace(options.GfwlRoot)) roots.Add(options.GfwlRoot);
        if (!string.IsNullOrWhiteSpace(options.XboxPcRoot)) roots.Add(options.XboxPcRoot);

        var games = new SortedDictionary<string, PlatformGameSnapshot>(StringComparer.OrdinalIgnoreCase);
        var messages = new List<string>();
        var foundAnyRoot = false;
        foreach (var root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foundAnyRoot = true;
            foreach (var file in SafeEnumerate(root, "achievements.dat"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var achievements = ReadAchievementsDat(file);
                    if (achievements.Count == 0) continue;
                    Add(Path.GetFileName(Path.GetDirectoryName(file)) ?? "unknown", file, achievements);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    messages.Add($"Xbox: skipped {Path.GetFileName(file)} ({exception.GetType().Name}).");
                }
            }

            foreach (var file in SafeEnumerate(root, "achievements.json"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var achievements = ReadJsonStates(file);
                    if (achievements.Count == 0) continue;
                    Add(TitleFromPath(file) ?? "unknown", file, achievements);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
                {
                    messages.Add($"Xbox: skipped {Path.GetFileName(file)} ({exception.GetType().Name}).");
                }
            }
        }

        if (!foundAnyRoot) messages.Add("No Xbox/XBLessness data folder was found.");
        return Task.FromResult(new PlatformCollectResult(Platform, games.Values.ToList(), messages));

        void Add(string gameId, string source, IReadOnlyList<PlatformAchievement> achievements)
        {
            if (!games.TryGetValue(gameId, out var existing) || achievements.Count > existing.Achievements.Count)
                games[gameId] = new PlatformGameSnapshot(Platform, gameId, null, source, achievements);
        }
    }

    private static string? TitleFromPath(string file)
    {
        for (var directory = Path.GetDirectoryName(file); directory is not null; directory = Path.GetDirectoryName(directory))
        {
            var name = Path.GetFileName(directory);
            if (TitleIdPattern.IsMatch(name)) return name.ToUpperInvariant();
            if (string.Equals(name, "XLiveLessNess", StringComparison.OrdinalIgnoreCase)) break;
        }
        return Path.GetFileName(Path.GetDirectoryName(file));
    }

    private static IEnumerable<string> SafeEnumerate(string root, string fileName)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        var visited = 0;
        while (pending.Count > 0 && visited++ < 20_000)
        {
            var directory = pending.Pop();
            string[] files;
            string[] children;
            try
            {
                files = Directory.GetFiles(directory);
                children = Directory.GetDirectories(directory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                continue;
            }

            foreach (var file in files)
                if (string.Equals(Path.GetFileName(file), fileName, StringComparison.OrdinalIgnoreCase)) yield return file;
            foreach (var child in children)
            {
                try
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) pending.Push(child);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            }
        }
    }

    internal static IReadOnlyList<PlatformAchievement> ReadAchievementsDat(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length is 0 or > 1024 * 1024 || bytes.Length % 16 != 0) return [];
        var results = new List<PlatformAchievement>(bytes.Length / 16);
        var seen = new Dictionary<uint, int>();
        for (var offset = 0; offset < bytes.Length; offset += 16)
        {
            var id = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
            var low = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4, 4));
            var high = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 8, 4));
            var earnedTime = FileTimeToUnixSeconds((ulong)high << 32 | low);
            if (seen.TryGetValue(id, out var index))
            {
                if (earnedTime > results[index].EarnedTimeUnix)
                    results[index] = results[index] with { EarnedTimeUnix = earnedTime };
                continue;
            }
            seen[id] = results.Count;
            results.Add(new PlatformAchievement(id.ToString(CultureInfo.InvariantCulture),
                Earned: true, EarnedTimeUnix: earnedTime, RarityPercent: null));
        }
        return results;
    }

    internal static IReadOnlyList<PlatformAchievement> ReadJsonStates(string path)
    {
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path));
        var results = new List<PlatformAchievement>();
        if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return results;
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
            var earned = false;
            long earnedTime = 0;
            string? name = null;
            foreach (var field in property.Value.EnumerateObject())
            {
                if (field.NameEquals("earned") || field.NameEquals("unlocked"))
                    earned = field.Value.ValueKind == System.Text.Json.JsonValueKind.True;
                else if (field.NameEquals("earned_time") && field.Value.TryGetInt64(out var time)) earnedTime = time;
                else if (field.NameEquals("name")) name = field.Value.GetString();
            }
            results.Add(new PlatformAchievement(property.Name, name, Earned: earned, EarnedTimeUnix: earnedTime));
        }
        return results;
    }

    internal static long FileTimeToUnixSeconds(ulong ticks)
    {
        if (ticks == 0) return 0;
        try
        {
            var value = DateTime.FromFileTimeUtc(unchecked((long)ticks));
            return new DateTimeOffset(value).ToUnixTimeSeconds();
        }
        catch (ArgumentOutOfRangeException)
        {
            return 0;
        }
    }
}

/// <summary>Games for Windows Live profile reader (same XBLessness record shape, separate platform).</summary>
public sealed class GfwlDataAdapter() : XboxDataAdapter("gfwl", "Games for Windows Live (GFWL)", includeGfwl: true);

/// <summary>
/// Xbox PC (Microsoft / Xbox Network) reader.
/// Scans local <c>XboxGames</c> / packaged GDK (<c>MicrosoftGame.config</c>) installations and, when signed in
/// via a user-configured Microsoft Entra application, retrieves PC achievement history from Xbox Network
/// (<c>titlehub.xboxlive.com</c> and <c>achievements.xboxlive.com</c>) with throttling and local cache fallback.
/// Console-only titles without a Windows/PC device or local install match are excluded.
/// </summary>
public sealed class XboxPcDataAdapter(
    PlatformSourceOptions? defaults = null,
    HttpClient? httpClient = null,
    string? cacheRootDirectory = null)
    : PlatformDataAdapter("xboxpc", "Xbox PC (Microsoft / Xbox Network)")
{
    private const string TitleHubBase = "https://titlehub.xboxlive.com";
    private const string AchievementsBase = "https://achievements.xboxlive.com";
    private readonly PlatformSourceOptions _defaults = defaults ?? PlatformSourceOptions.CreateDefault();
    private readonly HttpClient? _httpClient = httpClient;
    private readonly string _cacheRoot = Path.GetFullPath(cacheRootDirectory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AchievementSync", "metadata"));

    public override bool IsConfigured => IsConfiguredFor(_defaults);

    public override bool IsConfiguredFor(PlatformSourceOptions options) =>
        (!string.IsNullOrWhiteSpace(options.XboxXstsToken)
         && !string.IsNullOrWhiteSpace(options.XboxUhs)
         && !string.IsNullOrWhiteSpace(options.XboxXuid))
        || LocalRoots(options).Any(Directory.Exists);

    public override string? ConfigurationHint =>
        "Configure your Microsoft Entra Application Client ID and connect Xbox under Platforms, or point Xbox PC to your XboxGames folder.";

    public override async Task<PlatformCollectResult> CollectAsync(PlatformSourceOptions options, CancellationToken cancellationToken = default)
    {
        var messages = new List<string>();
        var games = new SortedDictionary<string, PlatformGameSnapshot>(StringComparer.OrdinalIgnoreCase);

        // 1. Discover locally installed Xbox PC / GDK titles via MicrosoftGame.config & local cached JSON.
        var localTitles = new Dictionary<string, LocalXboxPcGame>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in LocalRoots(options).Where(Directory.Exists))
        {
            foreach (var configPath in SafeEnumerateNamed(root, "MicrosoftGame.config", 5))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var parsed = TryReadMicrosoftGameConfig(configPath);
                if (parsed is not null) localTitles.TryAdd(parsed.TitleId, parsed);
            }
        }

        // Load any locally cached Xbox PC snapshots as fallback / baseline.
        LoadCachedSnapshots(games);

        // 2. If authenticated with Xbox Network XSTS token, query TitleHub + Achievements API.
        if (_httpClient is not null
            && !string.IsNullOrWhiteSpace(options.XboxXstsToken)
            && !string.IsNullOrWhiteSpace(options.XboxUhs)
            && !string.IsNullOrWhiteSpace(options.XboxXuid))
        {
            try
            {
                var titles = await FetchPcTitleHistoryAsync(options, localTitles, cancellationToken).ConfigureAwait(false);
                using var throttle = new SemaphoreSlim(2, 2);
                var tasks = titles.Select(async title =>
                {
                    await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        var achievements = await FetchTitleAchievementsAsync(options, title.TitleId, cancellationToken).ConfigureAwait(false);
                        if (achievements.Count == 0) return (Title: title, Snapshot: (PlatformGameSnapshot?)null);
                        var source = localTitles.TryGetValue(title.TitleId, out var local)
                            ? local.ConfigPath
                            : $"xbox-network://title/{title.TitleId}";
                        return (Title: title, Snapshot: (PlatformGameSnapshot?)new PlatformGameSnapshot(
                            Platform,
                            title.TitleId,
                            title.Name ?? local?.DisplayName,
                            source,
                            achievements,
                            "xbox-pc-network"));
                    }
                    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidDataException)
                    {
                        lock (messages) messages.Add($"Xbox PC: skipped title {title.TitleId} ({ex.GetType().Name}).");
                        return (Title: title, Snapshot: (PlatformGameSnapshot?)null);
                    }
                    finally
                    {
                        throttle.Release();
                    }
                }).ToArray();

                foreach (var result in await Task.WhenAll(tasks).ConfigureAwait(false))
                {
                    if (result.Snapshot is not null)
                        games[result.Snapshot.GameId] = result.Snapshot;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidDataException)
            {
                messages.Add($"Xbox PC: Xbox Network sync failed ({ex.GetType().Name}); using local cache if available.");
            }
        }
        else if (localTitles.Count > 0 && games.Count == 0)
        {
            messages.Add($"Xbox PC: found {localTitles.Count} installed GDK title(s). Sign in to Xbox Network to import achievements.");
        }

        return new PlatformCollectResult(Platform, games.Values.ToList(), messages);
    }

    private async Task<IReadOnlyList<XboxTitleInfo>> FetchPcTitleHistoryAsync(
        PlatformSourceOptions options,
        IReadOnlyDictionary<string, LocalXboxPcGame> localTitles,
        CancellationToken cancellationToken)
    {
        var url = $"{TitleHubBase}/users/xuid({Uri.EscapeDataString(options.XboxXuid!)})/titles/titlehistory/decoration/achievement,image,scid";
        using var response = await SendXboxGetAsync(url, options, "2", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var doc = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        if (!doc.RootElement.TryGetProperty("titles", out var titlesArray)
            || titlesArray.ValueKind != System.Text.Json.JsonValueKind.Array)
            return [];

        var results = new List<XboxTitleInfo>();
        foreach (var entry in titlesArray.EnumerateArray())
        {
            if (entry.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
            var titleId = ReadJsonString(entry, "titleId");
            if (string.IsNullOrWhiteSpace(titleId)) continue;
            var name = ReadJsonString(entry, "name");
            var isPcDevice = HasPcDevice(entry);
            var isLocallyInstalled = localTitles.ContainsKey(titleId)
                                     || (decimal.TryParse(titleId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var decId)
                                         && localTitles.ContainsKey(((uint)decId).ToString("X8", CultureInfo.InvariantCulture)));
            if (!isPcDevice && !isLocallyInstalled) continue; // Exclude console-only history.

            if (entry.TryGetProperty("achievement", out var achMeta)
                && achMeta.ValueKind == System.Text.Json.JsonValueKind.Object
                && achMeta.TryGetProperty("totalAchievements", out var totalEl)
                && totalEl.TryGetInt32(out var total) && total == 0)
            {
                continue;
            }

            results.Add(new XboxTitleInfo(titleId, name));
        }

        return results;
    }

    private async Task<IReadOnlyList<PlatformAchievement>> FetchTitleAchievementsAsync(
        PlatformSourceOptions options,
        string titleId,
        CancellationToken cancellationToken)
    {
        var url = $"{AchievementsBase}/users/xuid({Uri.EscapeDataString(options.XboxXuid!)})/achievements?titleId={Uri.EscapeDataString(titleId)}&maxItems=1000";
        using var response = await SendXboxGetAsync(url, options, "2", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ParseAchievementsResponseJson(json);
    }

    public static IReadOnlyList<PlatformAchievement> ParseAchievementsResponseJson(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("achievements", out var arr)
            || arr.ValueKind != System.Text.Json.JsonValueKind.Array)
            return [];
        return ParseAchievementsArray(arr);
    }

    private static IReadOnlyList<PlatformAchievement> ParseAchievementsArray(System.Text.Json.JsonElement arr)
    {
        var results = new List<PlatformAchievement>();
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
            var id = ReadJsonString(item, "id");
            if (string.IsNullOrWhiteSpace(id)) continue;
            var name = ReadJsonString(item, "name");
            var progressState = ReadJsonString(item, "progressState");
            var earned = string.Equals(progressState, "Achieved", StringComparison.OrdinalIgnoreCase);
            var description = earned
                ? ReadJsonString(item, "description") ?? ReadJsonString(item, "lockedDescription")
                : ReadJsonString(item, "lockedDescription") ?? ReadJsonString(item, "description");
            long earnedTime = 0;
            if (earned && item.TryGetProperty("progression", out var prog) && prog.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                var timeUnlocked = ReadJsonString(prog, "timeUnlocked");
                earnedTime = SteamAppIdResolver.NormalizeUnixSeconds(timeUnlocked);
            }

            string? iconPath = null;
            if (item.TryGetProperty("mediaAssets", out var media) && media.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var asset in media.EnumerateArray())
                {
                    if (string.Equals(ReadJsonString(asset, "type"), "Icon", StringComparison.OrdinalIgnoreCase))
                    {
                        iconPath = ReadJsonString(asset, "url");
                        if (!string.IsNullOrWhiteSpace(iconPath)) break;
                    }
                }
            }

            double? rarity = null;
            if (item.TryGetProperty("rarity", out var rarityObj)
                && rarityObj.ValueKind == System.Text.Json.JsonValueKind.Object
                && rarityObj.TryGetProperty("currentPercentage", out var pct)
                && pct.TryGetDouble(out var pctVal)
                && pctVal is >= 0 and <= 100)
            {
                rarity = pctVal;
            }

            var isSecret = item.TryGetProperty("isSecret", out var secretEl) && secretEl.ValueKind == System.Text.Json.JsonValueKind.True;
            results.Add(new PlatformAchievement(
                id,
                name,
                description,
                earned,
                earnedTime,
                IconPath: iconPath,
                RarityPercent: rarity,
                Hidden: isSecret));
        }

        return results;
    }

    private async Task<HttpResponseMessage> SendXboxGetAsync(
        string url,
        PlatformSourceOptions options,
        string contractVersion,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Authorization", $"XBL3.0 x={options.XboxUhs};{options.XboxXstsToken}");
            request.Headers.TryAddWithoutValidation("x-xbl-contract-version", contractVersion);
            request.Headers.TryAddWithoutValidation("Accept-Language", "en-US");
            request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

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
        throw new HttpRequestException("Xbox Network rate limit exceeded.");
    }

    private void LoadCachedSnapshots(SortedDictionary<string, PlatformGameSnapshot> games)
    {
        var platformDir = Path.Combine(_cacheRoot, "_platforms", Platform);
        if (!Directory.Exists(platformDir)) return;
        foreach (var dir in Directory.EnumerateDirectories(platformDir))
        {
            var achPath = Path.Combine(dir, "achievements.json");
            if (!File.Exists(achPath)) continue;
            try
            {
                var titleId = Path.GetFileName(dir);
                var achievements = XboxDataAdapter.ReadJsonStates(achPath);
                if (achievements.Count > 0)
                    games[titleId] = new PlatformGameSnapshot(Platform, titleId, null, achPath, achievements, "local-cache");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        }
    }

    private static IEnumerable<string> LocalRoots(PlatformSourceOptions options)
    {
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(options.XboxPcRoot)) roots.Add(options.XboxPcRoot);
        var systemDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        roots.Add(Path.Combine(systemDrive, "XboxGames"));
        return roots.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    public static LocalXboxPcGame? TryReadMicrosoftGameConfig(string configPath)
    {
        try
        {
            var doc = System.Xml.Linq.XDocument.Load(configPath);
            var titleId = doc.Descendants("TitleId").FirstOrDefault()?.Value.Trim();
            if (string.IsNullOrWhiteSpace(titleId)) return null;
            var shellVisuals = doc.Descendants("ShellVisuals").FirstOrDefault();
            var displayName = shellVisuals?.Attribute("DefaultDisplayName")?.Value.Trim()
                              ?? shellVisuals?.Attribute("DisplayName")?.Value.Trim()
                              ?? shellVisuals?.Element("DisplayName")?.Value.Trim();
            var exeName = doc.Descendants("Executable").FirstOrDefault()?.Attribute("Name")?.Value.Trim();
            return new LocalXboxPcGame(titleId.ToUpperInvariant(), displayName, configPath, exeName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return null;
        }
    }

    private static bool HasPcDevice(System.Text.Json.JsonElement titleEntry)
    {
        if (!titleEntry.TryGetProperty("devices", out var devices) || devices.ValueKind != System.Text.Json.JsonValueKind.Array)
            return false;
        foreach (var device in devices.EnumerateArray())
        {
            var text = device.GetString();
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (text.Equals("Win32", StringComparison.OrdinalIgnoreCase)
                || text.Equals("PC", StringComparison.OrdinalIgnoreCase)
                || text.Equals("Windows", StringComparison.OrdinalIgnoreCase)
                || text.Equals("WindowsOneCore", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static string? ReadJsonString(System.Text.Json.JsonElement element, string name) =>
        element.TryGetProperty(name, out var prop) && prop.ValueKind == System.Text.Json.JsonValueKind.String
            ? prop.GetString()?.Trim()
            : null;

    private static IEnumerable<string> SafeEnumerateNamed(string root, string fileName, int maxDepth)
    {
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));
        var visited = 0;
        while (pending.Count > 0 && visited++ < 10_000)
        {
            var (dir, depth) = pending.Pop();
            string[] files;
            string[] children;
            try
            {
                files = Directory.GetFiles(dir, fileName);
                children = Directory.GetDirectories(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                continue;
            }

            foreach (var f in files) yield return f;
            if (depth >= maxDepth) continue;
            foreach (var c in children)
            {
                try { if ((File.GetAttributes(c) & FileAttributes.ReparsePoint) == 0) pending.Push((c, depth + 1)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            }
        }
    }

    public sealed record LocalXboxPcGame(string TitleId, string? DisplayName, string ConfigPath, string? ExecutableName = null);
    private sealed record XboxTitleInfo(string TitleId, string? Name);
}
