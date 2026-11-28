using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AchievementSync.Core.Platforms;

/// <summary>
/// PC Ports and Recompilations adapter covering:
/// <list type="bullet">
///   <item>LADXHD Updated (Link's Awakening DX HD): Data\Achievements + %LOCALAPPDATA%\Zelda_LA\achievements (or portable).</item>
///   <item>PS3Recomp JSON: local trophy JSON schemas and unlock progress preserving Bronze/Silver/Gold/Platinum grades.</item>
///   <item>PS4 Recomp ports (Bloodborne, Uncharted, God of War III, MediEvil): trophy files + launcher unlock logs.</item>
/// </list>
/// </summary>
public sealed class RecompDataAdapter(PlatformSourceOptions? defaults = null)
    : PlatformDataAdapter("recomp", "PC Ports & Recomps (LADXHD / PS3 / PS4)")
{
    private static readonly Regex Ps4LogTrophyPattern = new(
        @"(?:unlock(?:ed)?\s+trophy|trophy[_\s-]*unlock(?:ed)?|\[trophy\].*?unlock)\s*[:=\s#\[\(]*(?<id>[A-Za-z0-9_-]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LogTimestampPattern = new(
        @"(\d{4}-\d{2}-\d{2}[T\s]\d{2}:\d{2}:\d{2}(?:\.\d+)?Z?)",
        RegexOptions.Compiled);

    private readonly PlatformSourceOptions _defaults = defaults ?? PlatformSourceOptions.CreateDefault();

    public override bool IsConfigured => IsConfiguredFor(_defaults);

    public override bool IsConfiguredFor(PlatformSourceOptions options) =>
        (!string.IsNullOrWhiteSpace(options.RecompRoot) && Directory.Exists(options.RecompRoot))
        || (!string.IsNullOrWhiteSpace(options.LadxhdRoot) && Directory.Exists(options.LadxhdRoot))
        || Directory.Exists(DefaultLadxhdSaveRoot());

    public override string? ConfigurationHint =>
        "Add the port folder. Reads LADXHD/PS3Recomp data and PS4 TROPHY.TRP/npbind.dat or decrypted XML/JSON; follows static game_dir/game_path settings, local icons, and logs without running launcher scripts.";

    private static string DefaultLadxhdSaveRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zelda_LA", "achievements");

    public override Task<PlatformCollectResult> CollectAsync(PlatformSourceOptions options, CancellationToken cancellationToken = default)
    {
        var games = new SortedDictionary<string, PlatformGameSnapshot>(StringComparer.OrdinalIgnoreCase);
        var messages = new List<string>();

        var ladxhdSaveRoots = new[] { options.LadxhdRoot, DefaultLadxhdSaveRoot() }
            .Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
            .Select(p => Path.GetFullPath(p!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var saveRoot in ladxhdSaveRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (File.Exists(Path.Combine(saveRoot, "Link's Awakening DX HD.exe"))) continue;
                var progress = ReadLadxhdDirectory(schemaDir: null, progressDir: saveRoot);
                if (progress.Count > 0)
                {
                    games["LADXHD"] = new PlatformGameSnapshot(Platform, "LADXHD", "The Legend of Zelda: Link's Awakening DX HD", saveRoot, progress, "ladxhd");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                messages.Add($"LADXHD: skipped {Path.GetFileName(saveRoot)} ({ex.GetType().Name}).");
            }
        }

        var installRoots = new[] { options.RecompRoot, options.LadxhdRoot }
            .Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
            .Select(p => Path.GetFullPath(p!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var root in installRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TryCollectLadxhdInstall(root, ladxhdSaveRoots, games, messages);
            CollectPs3Recomp(root, games, messages, cancellationToken);
            CollectPs4Recomp(root, games, messages, cancellationToken);
        }

        if (games.Count == 0)
            messages.Add("No LADXHD, PS3Recomp, or PS4 Recomp achievement data was found.");
        return Task.FromResult(new PlatformCollectResult(Platform, games.Values.ToList(), messages));
    }

    private static void TryCollectLadxhdInstall(
        string root,
        IReadOnlyList<string> globalSaveRoots,
        SortedDictionary<string, PlatformGameSnapshot> games,
        List<string> messages)
    {
        var exeCandidates = Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "Link's Awakening DX HD.exe", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true }).ToArray()
            : [];
        foreach (var exe in exeCandidates)
        {
            var gameDir = Path.GetDirectoryName(exe)!;
            var dataAchDir = Path.Combine(gameDir, "Data", "Achievements");
            if (!Directory.Exists(dataAchDir)) continue;

            try
            {
                var portableDir = Path.Combine(gameDir, "achievements");
                var progressDir = Directory.Exists(portableDir)
                    ? portableDir
                    : globalSaveRoots.FirstOrDefault(Directory.Exists);
                var achievements = ReadLadxhdDirectory(dataAchDir, progressDir);
                if (achievements.Count == 0) continue;

                var existing = games.GetValueOrDefault("LADXHD");
                var merged = existing is null ? achievements : UnionAchievements(existing.Achievements, achievements);
                games["LADXHD"] = new PlatformGameSnapshot(
                    "recomp",
                    "LADXHD",
                    "The Legend of Zelda: Link's Awakening DX HD",
                    progressDir ?? dataAchDir,
                    merged,
                    "ladxhd");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                messages.Add($"LADXHD: failed reading install ({ex.GetType().Name}).");
            }
        }
    }

    public static IReadOnlyList<PlatformAchievement> ReadLadxhdDirectory(string? schemaDir, string? progressDir)
    {
        var byId = new Dictionary<string, PlatformAchievement>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(schemaDir) && Directory.Exists(schemaDir))
        {
            foreach (var file in Directory.EnumerateFiles(schemaDir, "*.*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext == ".json")
                {
                    foreach (var ach in ParseGenericAchievementJson(file))
                        Upsert(byId, ach);
                }
                else if (ext == ".xml")
                {
                    foreach (var ach in ParseGenericAchievementXml(file))
                        Upsert(byId, ach);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(progressDir) && Directory.Exists(progressDir))
        {
            foreach (var file in Directory.EnumerateFiles(progressDir, "*.*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext is ".json" or ".dat" or ".txt" or ".sav" or ".ini" or "")
                {
                    try
                    {
                        var text = File.ReadAllText(file).Trim();
                        if (text.StartsWith('{') || text.StartsWith('['))
                        {
                            foreach (var ach in ParseGenericAchievementJsonText(text, defaultEarnedIfListed: true))
                                Upsert(byId, ach);
                        }
                        else if (ext is ".txt" or ".ini" or "")
                        {
                            foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                            {
                                var trimmed = line.Trim();
                                if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
                                var eq = trimmed.IndexOf('=');
                                if (eq > 0)
                                {
                                    var id = trimmed[..eq].Trim();
                                    var val = trimmed[(eq + 1)..].Trim();
                                    var earned = SteamAppIdResolver.NormalizeEarnedFlag(val) ?? false;
                                    var ts = earned ? SteamAppIdResolver.NormalizeUnixSeconds(val) : 0L;
                                    Upsert(byId, new PlatformAchievement(id, Earned: earned, EarnedTimeUnix: ts));
                                }
                                else
                                {
                                    Upsert(byId, new PlatformAchievement(trimmed, Earned: true));
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
        }

        return byId.Values.OrderBy(a => a.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void CollectPs3Recomp(
        string root,
        SortedDictionary<string, PlatformGameSnapshot> games,
        List<string> messages,
        CancellationToken cancellationToken)
    {
        var patterns = new[] { "*troph*.json", "*ps3recomp*.json", "ps3recomp_achievements.json", "*recomp*.json" };
        var candidateFiles = patterns.SelectMany(p => SafeEnumerateFiles(root, p, 4)).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var file in candidateFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var (gameId, title, achievements) = ParsePs3RecompJson(file);
                if (achievements.Count == 0) continue;
                var effectiveId = gameId ?? Path.GetFileName(Path.GetDirectoryName(file)) ?? "ps3recomp";
                if (!games.TryGetValue(effectiveId, out var existing) || achievements.Count >= existing.Achievements.Count)
                {
                    games[effectiveId] = new PlatformGameSnapshot("recomp", effectiveId, title ?? existing?.Title, file, achievements, "ps3recomp");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                messages.Add($"PS3Recomp: skipped {Path.GetFileName(file)} ({ex.GetType().Name}).");
            }
        }
    }

    public static (string? GameId, string? Title, IReadOnlyList<PlatformAchievement> Achievements) ParsePs3RecompJson(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path),
            new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var root = doc.RootElement;
        string? gameId = null;
        string? title = null;
        var results = new List<PlatformAchievement>();

        if (root.ValueKind == JsonValueKind.Object)
        {
            gameId = ReadString(root, "npCommunicationId", "npcommid", "titleId", "gameId", "id");
            title = ReadString(root, "title", "titleName", "name", "gameName");
            if (root.TryGetProperty("trophies", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in arr.EnumerateArray())
                {
                    var ach = ParsePs3TrophyEntry(entry);
                    if (ach is not null) results.Add(ach);
                }
            }
            else
            {
                foreach (var prop in root.EnumerateObject())
                {
                    if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                    var ach = ParsePs3TrophyEntry(prop.Value, prop.Name);
                    if (ach is not null) results.Add(ach);
                }
            }
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in root.EnumerateArray())
            {
                var ach = ParsePs3TrophyEntry(entry);
                if (ach is not null) results.Add(ach);
            }
        }

        return (gameId, title, results);
    }

    private static PlatformAchievement? ParsePs3TrophyEntry(JsonElement entry, string? fallbackId = null)
    {
        if (entry.ValueKind != JsonValueKind.Object) return null;
        var id = ReadString(entry, "id", "trophyId", "trophy_id")
                 ?? (entry.TryGetProperty("id", out var idNum) && idNum.ValueKind == JsonValueKind.Number ? idNum.GetRawText() : null)
                 ?? fallbackId;
        if (string.IsNullOrWhiteSpace(id)) return null;

        var name = ReadString(entry, "name", "title", "trophyName");
        var desc = ReadString(entry, "detail", "description", "trophyDetail");
        var grade = NormalizeTrophyGrade(ReadString(entry, "grade", "ttype", "type", "trophyType"));
        var hidden = entry.TryGetProperty("hidden", out var hid) && (SteamAppIdResolver.NormalizeEarnedFlag(hid) ?? false);

        bool? earnedFlag = null;
        foreach (var k in new[] { "unlocked", "earned", "achieved", "state" })
        {
            if (entry.TryGetProperty(k, out var val))
            {
                earnedFlag = SteamAppIdResolver.NormalizeEarnedFlag(val);
                if (earnedFlag.HasValue) break;
            }
        }

        long ts = 0;
        foreach (var k in new[] { "timestamp", "unlockTime", "unlock_time", "earned_time", "unlockedAt" })
        {
            if (entry.TryGetProperty(k, out var val))
            {
                ts = SteamAppIdResolver.NormalizeUnixSeconds(val);
                if (ts > 0) break;
            }
        }

        var earned = earnedFlag ?? ts > 0;
        var icon = ReadString(entry, "icon", "iconPath", "icon_path");
        return new PlatformAchievement(id, name, desc, earned, earned ? ts : 0, IconPath: icon, TrophyType: grade, Hidden: hidden);
    }

    private static void CollectPs4Recomp(
        string root,
        SortedDictionary<string, PlatformGameSnapshot> games,
        List<string> messages,
        CancellationToken cancellationToken)
    {
        foreach (var marker in SafeEnumerateFiles(root, "run.py", 3))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var portDir = Path.GetDirectoryName(marker)!;
            var portName = Path.GetFileName(portDir);
            var fallbackGameId = InferPs4RecompId(portDir, portName);
            var sourceRoots = new[] { portDir }
                .Concat(DiscoverPs4GameDataRoots(portDir))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var byGame = new Dictionary<string, Dictionary<string, PlatformAchievement>>(StringComparer.OrdinalIgnoreCase);
            var titlesByGame = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var iconFiles = sourceRoots.SelectMany(sourceRoot => SafeEnumerateFiles(sourceRoot, "TROP*.PNG", 5)
                    .Concat(SafeEnumerateFiles(sourceRoot, "TROP*.png", 5)))
                .Select(path => (Name: Path.GetFileName(path), Path: path))
                .Where(item => !string.IsNullOrWhiteSpace(item.Name))
                .GroupBy(item => item.Name!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Path, StringComparer.OrdinalIgnoreCase);

            foreach (var xmlDir in sourceRoots.SelectMany(sourceRoot => SafeEnumerateDirs(sourceRoot, "Xml", 4))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var gameId = InferPs4RecompId(xmlDir, fallbackGameId);
                if (!byGame.TryGetValue(gameId, out var schema))
                    byGame[gameId] = schema = new Dictionary<string, PlatformAchievement>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in PlayStationDataAdapter.ReadPs4TrophyXmlDirectory(xmlDir))
                {
                    var iconName = int.TryParse(item.Id, out var trophyId) ? $"TROP{trophyId:000}.PNG" : string.Empty;
                    var grayIconName = int.TryParse(item.Id, out trophyId) ? $"TROP{trophyId:000}_G.PNG" : string.Empty;
                    var icon = iconFiles.GetValueOrDefault(iconName);
                    var grayIcon = iconFiles.GetValueOrDefault(grayIconName);
                    Upsert(schema, item with
                    {
                        IconPath = item.IconPath ?? icon,
                        IconGrayPath = item.IconGrayPath ?? grayIcon
                    });
                }
            }
            foreach (var jsonFile in sourceRoots.SelectMany(sourceRoot => SafeEnumerateFiles(sourceRoot, "*troph*.json", 3))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var gameId = InferPs4RecompId(jsonFile, fallbackGameId);
                if (!byGame.TryGetValue(gameId, out var schema))
                    byGame[gameId] = schema = new Dictionary<string, PlatformAchievement>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in ParseGenericAchievementJson(jsonFile))
                    Upsert(schema, item);
            }

            var logsByGame = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var logFile in SafeEnumerateFiles(portDir, "*.log", 3))
            {
                try
                {
                    var lines = File.ReadLines(logFile).ToArray();
                    var explicitGameId = InferPs4RecompId(string.Join("\n", lines), string.Empty);
                    if (string.IsNullOrWhiteSpace(explicitGameId) && byGame.Count > 1)
                    {
                        messages.Add($"PS4 Recomp: skipped ambiguous launcher log {Path.GetFileName(logFile)} because it does not identify one trophy list.");
                        continue;
                    }
                    var gameId = !string.IsNullOrWhiteSpace(explicitGameId)
                        ? explicitGameId
                        : byGame.Count == 1 ? byGame.Keys.First() : string.Empty;
                    if (byGame.Count == 0 || !byGame.TryGetValue(gameId, out var schema))
                    {
                        messages.Add($"PS4 Recomp: skipped launcher log {Path.GetFileName(logFile)} because no matching trophy schema was found.");
                        continue;
                    }
                    logsByGame.TryAdd(gameId, logFile);
                    var unknownUnlocks = 0;
                    foreach (var unlocked in ParsePs4LauncherLog(lines))
                    {
                        if (!schema.ContainsKey(unlocked.Id))
                        {
                            unknownUnlocks++;
                            continue;
                        }
                        Upsert(schema, unlocked);
                    }
                    if (unknownUnlocks > 0)
                        messages.Add($"PS4 Recomp: ignored {unknownUnlocks} launcher unlock(s) that were absent from the trophy schema.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    messages.Add($"PS4 Recomp: skipped {Path.GetFileName(logFile)} ({ex.GetType().Name}).");
                }
            }

            foreach (var sourceRoot in sourceRoots)
            foreach (var archiveFile in SafeEnumerateFiles(sourceRoot, "*.trp", 5)
                         .Where(path => Regex.IsMatch(Path.GetFileName(path), @"^trophy\d{2}\.trp$", RegexOptions.IgnoreCase)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var bindingFile = FindNearestPs4File(Path.GetDirectoryName(archiveFile)!, "npbind.dat", sourceRoot, 6);
                    var keyFile = FindNearestPs4File(portDir, "trophy.key", portDir, 1)
                        ?? FindNearestPs4File(Path.GetDirectoryName(archiveFile)!, "trophy.key", sourceRoot, 6);
                    var trophyArchive = Ps4TrophyArchiveReader.Read(archiveFile, bindingFile, keyFile);
                    var inferredId = InferPs4RecompId(archiveFile, string.Empty);
                    var gameId = !string.IsNullOrWhiteSpace(inferredId)
                        ? inferredId
                        : !string.IsNullOrWhiteSpace(trophyArchive.NpCommunicationId)
                            ? trophyArchive.NpCommunicationId
                            : fallbackGameId;
                    if (!byGame.TryGetValue(gameId, out var schema))
                        byGame[gameId] = schema = new Dictionary<string, PlatformAchievement>(StringComparer.OrdinalIgnoreCase);
                    foreach (var item in trophyArchive.Achievements) Upsert(schema, item);
                    if (!string.IsNullOrWhiteSpace(trophyArchive.Title)) titlesByGame[gameId] = trophyArchive.Title;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or CryptographicException)
                {
                    messages.Add($"PS4 Recomp: skipped {Path.GetFileName(archiveFile)} ({ex.GetType().Name}).");
                }
            }

            foreach (var (gameId, schema) in byGame)
            {
                if (schema.Count == 0) continue;
                var snapshot = new PlatformGameSnapshot(
                    "recomp", gameId, titlesByGame.GetValueOrDefault(gameId) ?? portName,
                    logsByGame.GetValueOrDefault(gameId) ?? marker,
                    schema.Values.OrderBy(a => a.Id, StringComparer.OrdinalIgnoreCase).ToArray(),
                    "ps4recomp");
                if (games.TryGetValue(gameId, out var existing))
                    snapshot = snapshot with { Achievements = UnionAchievements(existing.Achievements, snapshot.Achievements) };
                games[gameId] = snapshot;
            }
        }
    }

    public static IReadOnlyList<PlatformAchievement> ParsePs4LauncherLog(IEnumerable<string> lines)
    {
        var unlocked = new Dictionary<string, PlatformAchievement>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var m = Ps4LogTrophyPattern.Match(line);
            if (!m.Success) continue;
            var id = m.Groups["id"].Value.Trim();
            if (string.IsNullOrWhiteSpace(id)) continue;

            var ts = 0L;
            var tsMatch = LogTimestampPattern.Match(line);
            if (tsMatch.Success) ts = SteamAppIdResolver.NormalizeUnixSeconds(tsMatch.Groups[1].Value);

            if (unlocked.TryGetValue(id, out var existing))
                unlocked[id] = existing with { EarnedTimeUnix = Math.Max(existing.EarnedTimeUnix, ts) };
            else
                unlocked[id] = new PlatformAchievement(id, Earned: true, EarnedTimeUnix: ts);
        }
        return unlocked.Values.OrderBy(a => a.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string InferPs4RecompId(string portDir, string fallbackName)
    {
        var match = Regex.Match(portDir, @"(CUSA\d{5}|NPWR\d{5}_\d{2})", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : fallbackName;
    }

    private static string? FindNearestPs4File(string startDirectory, string fileName, string stopDirectory, int maxLevels)
    {
        var stop = Path.GetFullPath(stopDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var current = Path.GetFullPath(startDirectory);
        for (var level = 0; level <= maxLevels; level++)
        {
            var stopPrefix = stop + Path.DirectorySeparatorChar;
            if (!string.Equals(current, stop, StringComparison.OrdinalIgnoreCase)
                && !current.StartsWith(stopPrefix, StringComparison.OrdinalIgnoreCase)) break;
            var candidate = Path.Combine(current, fileName);
            try
            {
                if (File.Exists(candidate) && (File.GetAttributes(candidate) & FileAttributes.ReparsePoint) == 0)
                    return candidate;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            if (string.Equals(current, stop, StringComparison.OrdinalIgnoreCase)) break;
            current = Path.GetDirectoryName(current)!;
            if (string.IsNullOrWhiteSpace(current)) break;
        }
        return null;
    }

    private static IReadOnlyList<string> DiscoverPs4GameDataRoots(string portDir)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var settingsFile in SafeEnumerateFiles(portDir, "*.json", 3)
                     .Where(path => Path.GetFileName(path).Equals("settings.json", StringComparison.OrdinalIgnoreCase)
                         || Path.GetFileName(path).Equals("launcher.json", StringComparison.OrdinalIgnoreCase)
                         || Path.GetFileName(path).Equals("config.json", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                if (new FileInfo(settingsFile).Length > 1024 * 1024) continue;
                using var document = JsonDocument.Parse(File.ReadAllBytes(settingsFile),
                    new JsonDocumentOptions { MaxDepth = 32, AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                AddConfiguredPs4Paths(document.RootElement, Path.GetDirectoryName(settingsFile)!, roots, 0);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException) { }
        }

        foreach (var script in SafeEnumerateFiles(portDir, "run.py", 3))
        {
            try
            {
                if (new FileInfo(script).Length > 1024 * 1024) continue;
                var source = File.ReadAllText(script);
                foreach (Match match in Regex.Matches(source,
                             """(?im)^\s*(?:GAME|ORIGINAL_GAME|DATA)_(?:DIR|PATH|FOLDER)\s*=\s*['\"](?<path>[^'\"\r\n]+)['\"]"""))
                    AddConfiguredPs4Path(match.Groups["path"].Value, Path.GetDirectoryName(script)!, roots);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException) { }
        }
        return roots.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void AddConfiguredPs4Paths(JsonElement element, string baseDirectory, HashSet<string> roots, int depth)
    {
        if (depth > 16) return;
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String && IsPs4GamePathKey(property.Name))
                    AddConfiguredPs4Path(property.Value.GetString(), baseDirectory, roots);
                else if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    AddConfiguredPs4Paths(property.Value, baseDirectory, roots, depth + 1);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                AddConfiguredPs4Paths(item, baseDirectory, roots, depth + 1);
        }
    }

    private static bool IsPs4GamePathKey(string name) => name.Trim().ToLowerInvariant().Replace('-', '_') is
        "game_dir" or "game_directory" or "game_path" or "game_folder" or
        "original_game" or "original_game_dir" or "original_game_directory" or "original_game_path" or "original_game_folder" or
        "data_dir" or "data_directory" or "data_path" or "data_folder";

    private static void AddConfiguredPs4Path(string? configuredPath, string baseDirectory, HashSet<string> roots)
    {
        if (string.IsNullOrWhiteSpace(configuredPath)) return;
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(configuredPath.Trim().Trim('"', '\''));
            var fullPath = Path.GetFullPath(Path.IsPathRooted(expanded) ? expanded : Path.Combine(baseDirectory, expanded));
            if (!Directory.Exists(fullPath) || !HasPs4TrophyRoot(fullPath)) return;
            if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0) return;
            roots.Add(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException) { }
    }

    private static bool HasPs4TrophyRoot(string path) =>
        Directory.Exists(Path.Combine(path, "sce_sys", "trophy"))
        || File.Exists(Path.Combine(path, "sce_sys", "npbind.dat"))
        || Directory.Exists(Path.Combine(path, "TrophyFiles"));

    private static IReadOnlyList<PlatformAchievement> ParseGenericAchievementJson(string path) =>
        ParseGenericAchievementJsonText(File.ReadAllText(path), defaultEarnedIfListed: false);

    private static IReadOnlyList<PlatformAchievement> ParseGenericAchievementJsonText(string json, bool defaultEarnedIfListed)
    {
        var results = new List<PlatformAchievement>();
        using var doc = JsonDocument.Parse(json,
            new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && defaultEarnedIfListed)
                {
                    var s = item.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(s)) results.Add(new PlatformAchievement(s, Earned: true));
                }
                else if (item.ValueKind == JsonValueKind.Object)
                {
                    var id = ReadString(item, "id", "apiName", "name", "achievementId");
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    var earned = ReadEarnedBool(item) ?? defaultEarnedIfListed;
                    var ts = earned ? ReadTimestamp(item) : 0L;
                    results.Add(new PlatformAchievement(id, ReadString(item, "displayName", "title", "name"),
                        ReadString(item, "description", "detail"), earned, ts,
                        IconPath: ReadString(item, "icon", "iconPath")));
                }
            }
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("achievements", out var inner))
                return ParseGenericAchievementJsonText(inner.GetRawText(), defaultEarnedIfListed);

            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Object)
                {
                    var earned = ReadEarnedBool(prop.Value) ?? defaultEarnedIfListed;
                    var ts = earned ? ReadTimestamp(prop.Value) : 0L;
                    results.Add(new PlatformAchievement(prop.Name, ReadString(prop.Value, "displayName", "title", "name"),
                        ReadString(prop.Value, "description", "detail"), earned, ts,
                        IconPath: ReadString(prop.Value, "icon", "iconPath")));
                }
                else if (SteamAppIdResolver.NormalizeEarnedFlag(prop.Value) is { } flag)
                {
                    results.Add(new PlatformAchievement(prop.Name, Earned: flag));
                }
            }
        }
        return results;
    }

    private static IReadOnlyList<PlatformAchievement> ParseGenericAchievementXml(string path)
    {
        var results = new List<PlatformAchievement>();
        var doc = XDocument.Load(path);
        foreach (var el in doc.Descendants().Where(e => e.Name.LocalName is "achievement" or "trophy"))
        {
            var id = el.Attribute("id")?.Value ?? el.Attribute("name")?.Value ?? el.Element("id")?.Value;
            if (string.IsNullOrWhiteSpace(id)) continue;
            var name = el.Element("name")?.Value?.Trim() ?? el.Attribute("displayName")?.Value?.Trim();
            var desc = el.Element("description")?.Value?.Trim() ?? el.Element("detail")?.Value?.Trim();
            var earned = SteamAppIdResolver.NormalizeEarnedFlag(el.Attribute("unlocked")?.Value ?? el.Attribute("earned")?.Value) ?? false;
            var ts = earned ? SteamAppIdResolver.NormalizeUnixSeconds(el.Attribute("timestamp")?.Value) : 0L;
            results.Add(new PlatformAchievement(id.Trim(), name, desc, earned, ts));
        }
        return results;
    }

    private static bool? ReadEarnedBool(JsonElement el)
    {
        foreach (var k in new[] { "earned", "unlocked", "achieved", "completed" })
            if (el.TryGetProperty(k, out var val))
                return SteamAppIdResolver.NormalizeEarnedFlag(val);
        return null;
    }

    private static long ReadTimestamp(JsonElement el)
    {
        foreach (var k in new[] { "earned_time", "unlock_time", "unlocktime", "timestamp", "unlockedAt" })
            if (el.TryGetProperty(k, out var val))
            {
                var ts = SteamAppIdResolver.NormalizeUnixSeconds(val);
                if (ts > 0) return ts;
            }
        return 0;
    }

    private static string? ReadString(JsonElement el, params string[] names)
    {
        foreach (var n in names)
            if (el.TryGetProperty(n, out var prop) && prop.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(prop.GetString()))
                return prop.GetString()!.Trim();
        return null;
    }

    private static string? NormalizeTrophyGrade(string? raw) => (raw ?? string.Empty).Trim().ToUpperInvariant() switch
    {
        "P" or "PLATINUM" => "platinum",
        "G" or "GOLD" => "gold",
        "S" or "SILVER" => "silver",
        "B" or "BRONZE" => "bronze",
        _ => string.IsNullOrWhiteSpace(raw) ? null : raw.Trim().ToLowerInvariant()
    };

    private static void Upsert(Dictionary<string, PlatformAchievement> map, PlatformAchievement item)
    {
        if (map.TryGetValue(item.Id, out var existing))
        {
            var earned = existing.Earned || item.Earned;
            map[item.Id] = existing with
            {
                Name = existing.Name ?? item.Name,
                Description = existing.Description ?? item.Description,
                Earned = earned,
                EarnedTimeUnix = earned ? Math.Max(existing.EarnedTimeUnix, item.EarnedTimeUnix) : 0,
                EarnedKnown = existing.EarnedKnown || item.EarnedKnown,
                IconPath = existing.IconPath ?? item.IconPath,
                IconGrayPath = existing.IconGrayPath ?? item.IconGrayPath,
                TrophyType = existing.TrophyType ?? item.TrophyType,
                RarityPercent = existing.RarityPercent ?? item.RarityPercent,
                Hidden = existing.Hidden || item.Hidden
            };
        }
        else
        {
            map[item.Id] = item;
        }
    }

    private static IReadOnlyList<PlatformAchievement> UnionAchievements(
        IReadOnlyList<PlatformAchievement> existing,
        IReadOnlyList<PlatformAchievement> incoming)
    {
        var map = new Dictionary<string, PlatformAchievement>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in existing) Upsert(map, item);
        foreach (var item in incoming) Upsert(map, item);
        return map.Values.OrderBy(a => a.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IEnumerable<string> SafeEnumerateFiles(string root, string pattern, int maxDepth)
    {
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));
        var visited = 0;
        while (pending.Count > 0 && visited++ < 15_000)
        {
            var (dir, depth) = pending.Pop();
            string[] files;
            string[] children;
            try
            {
                files = Directory.GetFiles(dir, pattern);
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
                catch { }
            }
        }
    }

    private static IEnumerable<string> SafeEnumerateDirs(string root, string targetName, int maxDepth)
    {
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));
        var visited = 0;
        while (pending.Count > 0 && visited++ < 10_000)
        {
            var (dir, depth) = pending.Pop();
            if (string.Equals(Path.GetFileName(dir), targetName, StringComparison.OrdinalIgnoreCase))
                yield return dir;
            if (depth >= maxDepth) continue;
            string[] children;
            try { children = Directory.GetDirectories(dir); }
            catch { continue; }
            foreach (var c in children)
            {
                try { if ((File.GetAttributes(c) & FileAttributes.ReparsePoint) == 0) pending.Push((c, depth + 1)); }
                catch { }
            }
        }
    }
}
