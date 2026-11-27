using System.Buffers.Binary;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AchievementSync.Core.Platforms;

/// <summary>PlayStation reader covering RPCS3 trophy sets (TROPCONF.SFM + TROPUSR.DAT),
/// PS4/PS5 exported trophy XML (shadPS4) and KytyPS5 trophy state JSON.</summary>
public sealed class PlayStationDataAdapter() : PlatformDataAdapter("playstation", "PlayStation (PS3/PS4/PS5)")
{
    public override bool IsConfigured => IsConfiguredFor(PlatformSourceOptions.CreateDefault());

    public override bool IsConfiguredFor(PlatformSourceOptions options) =>
        Ps3Roots(options).Any(Directory.Exists)
        || Ps4Roots(options).Any(Directory.Exists)
        || Ps5Roots(options).Any(Directory.Exists);

    public override string? ConfigurationHint =>
        "Keep RPCS3, shadPS4 (%APPDATA%\\shadPS4), or Kyty (_Trophies) in its folder, or choose it on the Platforms page.";

    private static IEnumerable<string> Ps3Roots(PlatformSourceOptions options) =>
        new[] { options.Rpcs3Root }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => Path.GetFullPath(p!))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> Ps4Roots(PlatformSourceOptions options)
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new[]
            {
                options.Ps4Root,
                Path.Combine(roaming, "shadPS4"),
                Path.Combine(local, "shadPS4")
            }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => Path.GetFullPath(p!))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> Ps5Roots(PlatformSourceOptions options) =>
        new[] { options.Ps5Root }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => Path.GetFullPath(p!))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    public override Task<PlatformCollectResult> CollectAsync(PlatformSourceOptions options, CancellationToken cancellationToken = default)
    {
        var games = new SortedDictionary<string, PlatformGameSnapshot>(StringComparer.OrdinalIgnoreCase);
        var messages = new List<string>();
        var anyRoot = false;

        // 1. RPCS3
        foreach (var rpcs3Root in Ps3Roots(options).Where(Directory.Exists))
        {
            anyRoot = true;
            foreach (var conf in Enumerate(rpcs3Root, "TROPCONF.SFM", 10))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var directory = Path.GetDirectoryName(conf)!;
                    var gameId = Path.GetFileName(directory);
                    if (string.IsNullOrWhiteSpace(gameId)) continue;
                    var achievements = ReadRpcs3TrophySet(conf, Path.Combine(directory, "TROPUSR.DAT"));
                    if (achievements.Count == 0) continue;
                    games[gameId] = new PlatformGameSnapshot(Platform, gameId,
                        TrophyTitle(conf), conf, achievements, "rpcs3");
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException)
                {
                    messages.Add($"PlayStation: skipped {Path.GetFileName(conf)} ({exception.GetType().Name}).");
                }
            }
        }

        // 2. ShadPS4 (prefer modern trophy/<NPWR> + home/<user>/trophy/<NPWR>.xml over legacy game_data/<CUSA>/TrophyFiles)
        foreach (var ps4Root in Ps4Roots(options).Where(Directory.Exists))
        {
            anyRoot = true;
            CollectShadPs4(ps4Root, games, messages, cancellationToken);
        }

        // 3. KytyPS5 (_Trophies + registered launcher games/.zar)
        foreach (var ps5Root in Ps5Roots(options).Where(Directory.Exists))
        {
            anyRoot = true;
            CollectKytyPs5(ps5Root, games, messages, cancellationToken);
        }

        if (!anyRoot) messages.Add("No PlayStation trophy folder was found.");
        return Task.FromResult(new PlatformCollectResult(Platform, games.Values.ToList(), messages));
    }

    private void CollectShadPs4(
        string ps4Root,
        SortedDictionary<string, PlatformGameSnapshot> games,
        List<string> messages,
        CancellationToken cancellationToken)
    {
        var npwrToCusa = ReadNpwrToCusaMapping(ps4Root);
        var modernCoveredIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Modern ShadPS4 layout:
        //   Schema: <root>\trophy\<NPWR>\Xml\TROP*.XML
        //   User progress: <root>\home\<userId>\trophy\<NPWR>.xml (or <root>\home\<userId>\trophy\<NPWR>\Xml\TROP*.XML)
        var modernTrophyRoot = Path.Combine(ps4Root, "trophy");
        if (Directory.Exists(modernTrophyRoot))
        {
            foreach (var npwrDir in Directory.EnumerateDirectories(modernTrophyRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var npwr = Path.GetFileName(npwrDir);
                if (string.IsNullOrWhiteSpace(npwr)) continue;
                var xmlDir = Path.Combine(npwrDir, "Xml");
                if (!Directory.Exists(xmlDir)) continue;

                try
                {
                    var schema = ReadPs4TrophyXmlDirectory(xmlDir);
                    if (schema.Count == 0) continue;

                    var iconsDir = Path.Combine(npwrDir, "Icons");
                    if (Directory.Exists(iconsDir))
                    {
                        schema = AttachShadPs4Icons(schema, iconsDir);
                    }

                    var userFiles = DiscoverShadPs4UserXmls(ps4Root, npwr);
                    var resolvedCusa = npwrToCusa.GetValueOrDefault(npwr);
                    var canonicalGameId = !string.IsNullOrWhiteSpace(resolvedCusa) ? resolvedCusa : npwr;
                    modernCoveredIds.Add(npwr);
                    if (!string.IsNullOrWhiteSpace(resolvedCusa)) modernCoveredIds.Add(resolvedCusa);

                    if (userFiles.Count == 0)
                    {
                        games[canonicalGameId] = new PlatformGameSnapshot(Platform, canonicalGameId, null, xmlDir, schema, "shadps4-modern");
                        continue;
                    }

                    // Merge across per-user progress XMLs (and keep per-user scoped snapshots when multiple users exist).
                    var mergedById = schema.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
                    foreach (var (userId, userXml) in userFiles)
                    {
                        try
                        {
                            var userStates = ReadShadPs4UserProgressXml(userXml);
                            var perUserAchievements = MergeUserStates(schema, userStates);
                            foreach (var a in perUserAchievements)
                            {
                                if (mergedById.TryGetValue(a.Id, out var cur))
                                {
                                    var earned = cur.Earned || a.Earned;
                                    mergedById[a.Id] = cur with
                                    {
                                        Earned = earned,
                                        EarnedTimeUnix = earned ? Math.Max(cur.EarnedTimeUnix, a.EarnedTimeUnix) : 0
                                    };
                                }
                            }

                            if (userFiles.Count > 1)
                            {
                                var scopedKey = $"{canonicalGameId}_user_{userId}";
                                games[scopedKey] = new PlatformGameSnapshot(Platform, scopedKey, null, userXml, perUserAchievements, $"shadps4-user-{userId}");
                            }
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
                        {
                            messages.Add($"PlayStation: skipped user trophy XML {Path.GetFileName(userXml)} ({ex.GetType().Name}).");
                        }
                    }

                    games[canonicalGameId] = new PlatformGameSnapshot(
                        Platform,
                        canonicalGameId,
                        null,
                        userFiles[0].Path,
                        mergedById.Values.OrderBy(a => int.TryParse(a.Id, out var n) ? n : int.MaxValue).ThenBy(a => a.Id, StringComparer.OrdinalIgnoreCase).ToArray(),
                        "shadps4-modern");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
                {
                    messages.Add($"PlayStation: skipped {npwr} ({ex.GetType().Name}).");
                }
            }
        }

        // Legacy ShadPS4 layout:
        //   <root>\game_data\<CUSA>\TrophyFiles\trophy00\Xml\TROP*.XML
        // Also supports direct TROP*.XML under Xml/ when a user pointed Ps4Root directly at a TrophyFiles or game folder.
        foreach (var xml in Enumerate(ps4Root, "TROP*.XML", 10))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var xmlDir = Path.GetDirectoryName(xml)!;
                if (!string.Equals(Path.GetFileName(xmlDir), "Xml", StringComparison.OrdinalIgnoreCase)) continue;
                var trophyDir = Path.GetDirectoryName(xmlDir)!;
                if (string.Equals(Path.GetDirectoryName(trophyDir), modernTrophyRoot, StringComparison.OrdinalIgnoreCase))
                    continue; // Already handled by modern layout

                var slotParent = Path.GetDirectoryName(trophyDir)!;
                var gameId = string.Equals(Path.GetFileName(slotParent), "TrophyFiles", StringComparison.OrdinalIgnoreCase)
                    ? Path.GetFileName(Path.GetDirectoryName(slotParent))!
                    : Path.GetFileName(trophyDir);
                if (string.IsNullOrWhiteSpace(gameId)) continue;

                // Prefer modern ShadPS4 storage when both modern and legacy layouts exist for the same game.
                if (modernCoveredIds.Contains(gameId)) continue;
                if (games.ContainsKey(gameId)) continue;

                var achievements = ReadPs4TrophyXmlDirectory(xmlDir);
                if (achievements.Count == 0) continue;
                var iconsDir = Path.Combine(trophyDir, "Icons");
                if (Directory.Exists(iconsDir)) achievements = AttachShadPs4Icons(achievements, iconsDir);
                games[gameId] = new PlatformGameSnapshot(Platform, gameId, null, xmlDir, achievements, "shadps4-legacy");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                messages.Add($"PlayStation: skipped {Path.GetFileName(xml)} ({exception.GetType().Name}).");
            }
        }
    }

    private void CollectKytyPs5(
        string ps5Root,
        SortedDictionary<string, PlatformGameSnapshot> games,
        List<string> messages,
        CancellationToken cancellationToken)
    {
        var trophyRoots = new List<string> { ps5Root };
        var underscoreTrophies = Path.Combine(ps5Root, "_Trophies");
        if (Directory.Exists(underscoreTrophies)) trophyRoots.Add(underscoreTrophies);

        foreach (var root in trophyRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var stateFile in Enumerate(root, "trophies_*.json", 6))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var directory = Path.GetDirectoryName(stateFile)!;
                    var gameId = Path.GetFileName(directory);
                    if (string.IsNullOrWhiteSpace(gameId)) continue;
                    var achievements = ReadPs5TrophyState(stateFile);
                    if (achievements.Count == 0) continue;

                    var fileName = Path.GetFileNameWithoutExtension(stateFile);
                    var key = gameId;
                    if (games.TryGetValue(key, out var existing))
                    {
                        games[key] = existing with
                        {
                            SourcePath = stateFile,
                            Achievements = UnionAchievements(existing.Achievements, achievements),
                            Version = "kyty-ps5"
                        };
                    }
                    else
                    {
                        games[key] = new PlatformGameSnapshot(Platform, key, null, stateFile, achievements, "kyty-ps5");
                    }

                    // Keep per-user scoped copy when filename has userId (trophies_<userId>_<idx>.json)
                    var parts = fileName.Split('_');
                    if (parts.Length >= 2 && !string.IsNullOrWhiteSpace(parts[1]))
                    {
                        var userKey = $"{gameId}_user_{parts[1]}";
                        if (!games.ContainsKey(userKey) && Enumerate(directory, "trophies_*.json", 1).Count() > 1)
                            games[userKey] = new PlatformGameSnapshot(Platform, userKey, null, stateFile, achievements, $"kyty-user-{parts[1]}");
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
                {
                    messages.Add($"PlayStation: skipped {Path.GetFileName(stateFile)} ({exception.GetType().Name}).");
                }
            }
        }

        // Also scan KytyPS5 launcher configuration (config.json / games.json) for registered game locations & .zar titles
        foreach (var cfgFile in new[] { "config.json", "games.json", "launcher.json" }
                     .Select(name => Path.Combine(ps5Root, name))
                     .Where(File.Exists))
        {
            try
            {
                foreach (var registered in ReadKytyRegisteredGames(cfgFile))
                {
                    if (!games.ContainsKey(registered.TitleId))
                    {
                        games[registered.TitleId] = new PlatformGameSnapshot(
                            Platform,
                            registered.TitleId,
                            registered.Title,
                            registered.Path,
                            [],
                            "kyty-registered");
                    }
                    else if (!string.IsNullOrWhiteSpace(registered.Title))
                    {
                        var existing = games[registered.TitleId];
                        if (string.IsNullOrWhiteSpace(existing.Title))
                            games[registered.TitleId] = existing with { Title = registered.Title };
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                messages.Add($"PlayStation: skipped Kyty config {Path.GetFileName(cfgFile)} ({ex.GetType().Name}).");
            }
        }
    }

    public static Dictionary<string, string> ReadNpwrToCusaMapping(string shadPs4Root)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var npwrCusaRegex = new Regex(@"(NPWR\d{5}_\d{2}).*?(CUSA\d{5})|(CUSA\d{5}).*?(NPWR\d{5}_\d{2})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        foreach (var subDir in new[] { "log", "logs", "." })
        {
            var dir = Path.Combine(shadPs4Root, subDir);
            if (!Directory.Exists(dir)) continue;
            foreach (var logFile in Directory.EnumerateFiles(dir, "*.txt", SearchOption.TopDirectoryOnly)
                         .Concat(Directory.EnumerateFiles(dir, "*.log", SearchOption.TopDirectoryOnly)))
            {
                try
                {
                    foreach (var line in File.ReadLines(logFile))
                    {
                        var m = npwrCusaRegex.Match(line);
                        if (!m.Success) continue;
                        var npwr = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[4].Value).ToUpperInvariant();
                        var cusa = (m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value).ToUpperInvariant();
                        map.TryAdd(npwr, cusa);
                    }
                }
                catch { }
            }
        }

        // Also check game_data\<CUSA>\npwr* or param.sfo/trophy00 correlations if present
        var gameDataDir = Path.Combine(shadPs4Root, "game_data");
        if (Directory.Exists(gameDataDir))
        {
            foreach (var cusaDir in Directory.EnumerateDirectories(gameDataDir))
            {
                var cusa = Path.GetFileName(cusaDir);
                if (!Regex.IsMatch(cusa, @"^CUSA\d{5}$", RegexOptions.IgnoreCase)) continue;
                var mapMarker = Path.Combine(cusaDir, "npwr.txt");
                if (File.Exists(mapMarker))
                {
                    var npwr = File.ReadAllText(mapMarker).Trim();
                    if (Regex.IsMatch(npwr, @"^NPWR\d{5}_\d{2}$", RegexOptions.IgnoreCase))
                        map.TryAdd(npwr.ToUpperInvariant(), cusa.ToUpperInvariant());
                }
            }
        }

        return map;
    }

    private static List<(string UserId, string Path)> DiscoverShadPs4UserXmls(string ps4Root, string npwr)
    {
        var results = new List<(string UserId, string Path)>();
        var homeDir = Path.Combine(ps4Root, "home");
        if (!Directory.Exists(homeDir)) return results;

        foreach (var userDir in Directory.EnumerateDirectories(homeDir))
        {
            var userId = Path.GetFileName(userDir);
            if (string.IsNullOrWhiteSpace(userId)) continue;
            var trophyFile = Path.Combine(userDir, "trophy", npwr + ".xml");
            if (File.Exists(trophyFile))
            {
                results.Add((userId, trophyFile));
                continue;
            }
            var nestedXmlDir = Path.Combine(userDir, "trophy", npwr, "Xml");
            if (Directory.Exists(nestedXmlDir))
            {
                var nested = Directory.EnumerateFiles(nestedXmlDir, "TROP*.XML").FirstOrDefault();
                if (nested is not null) results.Add((userId, nested));
            }
        }
        return results;
    }

    public static Dictionary<int, (bool Earned, long Timestamp)> ReadShadPs4UserProgressXml(string xmlPath)
    {
        var states = new Dictionary<int, (bool Earned, long Timestamp)>();
        var doc = XDocument.Load(xmlPath);
        foreach (var trophy in doc.Descendants().Where(e => e.Name.LocalName == "trophy"))
        {
            if (!int.TryParse(trophy.Attribute("id")?.Value, out var id)) continue;
            var stateAttr = trophy.Attribute("unlockstate")?.Value
                ?? trophy.Attribute("unlocked")?.Value
                ?? trophy.Attribute("state")?.Value;
            var unlocked = SteamAppIdResolver.NormalizeEarnedFlag(stateAttr) ?? false;
            var tsAttr = trophy.Attribute("unlocked_timestamp")?.Value
                ?? trophy.Attribute("unlock_timestamp")?.Value
                ?? trophy.Attribute("unlocked_time")?.Value
                ?? trophy.Attribute("timestamp")?.Value
                ?? trophy.Attribute("unlocktime")?.Value
                ?? trophy.Attribute("unlock_time")?.Value
                ?? trophy.Attribute("time")?.Value;
            var ts = unlocked ? SteamAppIdResolver.NormalizeUnixSeconds(tsAttr) : 0L;
            states[id] = (unlocked, ts);
        }
        return states;
    }

    private static IReadOnlyList<PlatformAchievement> MergeUserStates(
        IReadOnlyList<PlatformAchievement> schema,
        Dictionary<int, (bool Earned, long Timestamp)> userStates)
    {
        return schema.Select(item =>
        {
            if (!int.TryParse(item.Id, out var id) || !userStates.TryGetValue(id, out var st))
                return item;
            var earned = item.Earned || st.Earned;
            var ts = earned ? Math.Max(item.EarnedTimeUnix, st.Timestamp) : 0L;
            return item with { Earned = earned, EarnedTimeUnix = ts };
        }).ToArray();
    }

    private static IReadOnlyList<PlatformAchievement> AttachShadPs4Icons(
        IReadOnlyList<PlatformAchievement> achievements,
        string iconsDirectory)
    {
        return achievements.Select(item =>
        {
            if (!int.TryParse(item.Id, out var id)) return item;
            var iconName = $"TROP{id:000}.PNG";
            var iconPath = Path.Combine(iconsDirectory, iconName);
            if (!File.Exists(iconPath))
            {
                iconPath = Path.Combine(iconsDirectory, $"TROP{id:000}.png");
            }
            return File.Exists(iconPath) ? item with { IconPath = iconPath } : item;
        }).ToArray();
    }

    private static IReadOnlyList<(string TitleId, string? Title, string Path)> ReadKytyRegisteredGames(string configPath)
    {
        var results = new List<(string TitleId, string? Title, string Path)>();
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(configPath));
        void Walk(System.Text.Json.JsonElement el)
        {
            if (el.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var item in el.EnumerateArray()) Walk(item);
                return;
            }
            if (el.ValueKind != System.Text.Json.JsonValueKind.Object) return;

            string? path = null;
            string? title = null;
            string? titleId = null;
            foreach (var prop in el.EnumerateObject())
            {
                if (prop.Name.Equals("path", StringComparison.OrdinalIgnoreCase)
                    || prop.Name.Equals("game_dir", StringComparison.OrdinalIgnoreCase)
                    || prop.Name.Equals("zar", StringComparison.OrdinalIgnoreCase))
                    path = prop.Value.GetString();
                else if (prop.Name.Equals("title", StringComparison.OrdinalIgnoreCase)
                         || prop.Name.Equals("name", StringComparison.OrdinalIgnoreCase))
                    title = prop.Value.GetString();
                else if (prop.Name.Equals("title_id", StringComparison.OrdinalIgnoreCase)
                         || prop.Name.Equals("titleId", StringComparison.OrdinalIgnoreCase)
                         || prop.Name.Equals("id", StringComparison.OrdinalIgnoreCase))
                    titleId = prop.Value.GetString();
                else if (prop.Value.ValueKind is System.Text.Json.JsonValueKind.Array or System.Text.Json.JsonValueKind.Object)
                    Walk(prop.Value);
            }

            if (!string.IsNullOrWhiteSpace(path) && (path.EndsWith(".zar", StringComparison.OrdinalIgnoreCase) || Directory.Exists(path)))
            {
                var inferredId = !string.IsNullOrWhiteSpace(titleId)
                    ? titleId.Trim()
                    : Path.GetFileNameWithoutExtension(path);
                if (!string.IsNullOrWhiteSpace(inferredId))
                    results.Add((inferredId, title ?? Path.GetFileNameWithoutExtension(path), path));
            }
        }
        Walk(doc.RootElement);
        return results;
    }

    private static IReadOnlyList<PlatformAchievement> UnionAchievements(
        IReadOnlyList<PlatformAchievement> existing,
        IReadOnlyList<PlatformAchievement> incoming)
    {
        var merged = existing.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var item in incoming)
        {
            if (merged.TryGetValue(item.Id, out var cur))
            {
                var earned = cur.Earned || item.Earned;
                merged[item.Id] = cur with
                {
                    Name = cur.Name ?? item.Name,
                    Description = cur.Description ?? item.Description,
                    Earned = earned,
                    EarnedTimeUnix = earned ? Math.Max(cur.EarnedTimeUnix, item.EarnedTimeUnix) : 0,
                    TrophyType = cur.TrophyType ?? item.TrophyType
                };
            }
            else
            {
                merged[item.Id] = item;
            }
        }
        return merged.Values.OrderBy(a => a.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IEnumerable<string> Enumerate(string root, string pattern, int maxDepth)
    {
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));
        var visited = 0;
        while (pending.Count > 0 && visited++ < 40_000)
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
                try
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) pending.Push((child, depth + 1));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            }
        }
    }

    private static string? TrophyTitle(string confPath)
    {
        try
        {
            var document = XDocument.Load(confPath);
            return document.Descendants().FirstOrDefault(element => element.Name.LocalName == "title-name")?.Value.Trim();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>Parses TROPCONF.SFM metadata and the TROPUSR.DAT unlock bit map.</summary>
    public static IReadOnlyList<PlatformAchievement> ReadRpcs3TrophySet(string confPath, string userDataPath)
    {
        var document = XDocument.Load(confPath);
        var entries = new List<(int Id, string Name, string Description, string Type, bool Hidden)>();
        foreach (var trophy in document.Descendants().Where(element => element.Name.LocalName == "trophy"))
        {
            var idText = trophy.Attribute("id")?.Value;
            if (!int.TryParse(idText, out var id)) continue;
            entries.Add((id,
                trophy.Elements().FirstOrDefault(element => element.Name.LocalName == "name")?.Value.Trim() ?? id.ToString(CultureInfo.InvariantCulture),
                trophy.Elements().FirstOrDefault(element => element.Name.LocalName == "detail")?.Value.Trim() ?? string.Empty,
                TrophyType(trophy.Attribute("ttype")?.Value),
                (trophy.Attribute("hidden")?.Value ?? "no") is "yes" or "true" or "1"));
        }

        var unlocked = new Dictionary<int, bool>();
        if (File.Exists(userDataPath))
        {
            try
            {
                unlocked = ParseTropusrDat(File.ReadAllBytes(userDataPath), entries.Count);
            }
            catch (InvalidDataException)
            {
                // Unknown layout: keep all states unknown (not locked).
            }
        }

        return entries.Select(entry => new PlatformAchievement(
            entry.Id.ToString(CultureInfo.InvariantCulture),
            entry.Name,
            entry.Description,
            unlocked.GetValueOrDefault(entry.Id),
            TrophyType: entry.Type,
            Hidden: entry.Hidden)).ToArray();
    }

    /// <summary>Heuristic TROPUSR.DAT unlock-map reader ported from the documented trophy layout.</summary>
    public static Dictionary<int, bool> ParseTropusrDat(byte[] bytes, int trophyCount = 0)
    {
        var result = new Dictionary<int, bool>();
        var length = bytes.Length;
        if (length < 0x40) throw new InvalidDataException("TROPUSR.DAT is too small.");

        uint U32(int offset) => offset + 4 <= length ? BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4)) : 0;

        var candidates = new List<(int EntrySize, int Count, int BaseOffset)>();
        var headerBytes = Math.Min(0x600, length);
        for (var offset = 0; offset + 24 <= headerBytes; offset += 4)
        {
            if (U32(offset) != 6) continue;
            var entrySize = (int)U32(offset + 4);
            var count = (int)U32(offset + 12);
            var baseOffset = (int)U32(offset + 20);
            if (entrySize is < 0x10 or > 0x400) continue;
            if (count is < 1 or > 5000) continue;
            if (baseOffset <= 0 || baseOffset >= length) continue;
            candidates.Add((entrySize, count, baseOffset));
        }

        if (candidates.Count == 0) throw new InvalidDataException("No valid trophy descriptor found in TROPUSR.DAT.");

        var idOffsets = new[] { 0x10, 0x00, 0x08, 0x14, 0x0C };
        var best = (Score: -1L, IdOffset: -1, EntrySize: 0, Count: 0, BaseOffset: 0);
        foreach (var candidate in candidates)
        {
            var stride = candidate.EntrySize + 0x10;
            var limit = trophyCount > 0 ? Math.Min(trophyCount, candidate.Count) : Math.Min(96, candidate.Count);
            foreach (var idOffset in idOffsets)
            {
                long score = 0;
                for (var trophyId = 0; trophyId < limit; trophyId++)
                {
                    var entry = candidate.BaseOffset + trophyId * stride;
                    var idPosition = entry + idOffset;
                    var flagPosition = idPosition + 4;
                    if (flagPosition + 4 > length) break;
                    if (U32(idPosition) == trophyId) score += 10;
                    var flag = U32(flagPosition);
                    if (flag is 0 or 1) score += 1;
                }

                if (score > best.Score) best = (score, idOffset, candidate.EntrySize, candidate.Count, candidate.BaseOffset);
            }
        }

        if (best.IdOffset < 0) throw new InvalidDataException("Could not validate a TROPUSR.DAT trophy layout.");
        var total = trophyCount > 0 ? Math.Min(trophyCount, best.Count) : best.Count;
        var finalStride = best.EntrySize + 0x10;
        for (var trophyId = 0; trophyId < total; trophyId++)
        {
            var entry = best.BaseOffset + trophyId * finalStride;
            var idPosition = entry + best.IdOffset;
            var flagPosition = idPosition + 4;
            if (flagPosition + 4 > length) break;
            var id = (int)U32(idPosition);
            result[id] = U32(flagPosition) == 1;
        }

        return result;
    }

    public static IReadOnlyList<PlatformAchievement> ReadPs4TrophyXmlDirectory(string xmlDirectory)
    {
        var files = Directory.GetFiles(xmlDirectory)
            .Where(path => Regex.IsMatch(Path.GetFileName(path), @"^trop(_\d{2})?\.xml$", RegexOptions.IgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (files.Length == 0) return [];

        var entries = new SortedDictionary<int, PlatformAchievement>();
        var isEnglishFirst = Path.GetFileName(files[0]).Equals("trop_01.xml", StringComparison.OrdinalIgnoreCase) || files.Length == 1;
        for (var index = 0; index < files.Length; index++)
        {
            var isPreferred = index == 0 && isEnglishFirst || index == 1 && !isEnglishFirst && files.Length > 1;
            var document = XDocument.Load(files[index]);
            foreach (var trophy in document.Descendants().Where(element => element.Name.LocalName == "trophy"))
            {
                if (!int.TryParse(trophy.Attribute("id")?.Value, out var id)) continue;
                var unlocked = string.Equals(trophy.Attribute("unlockstate")?.Value, "true", StringComparison.OrdinalIgnoreCase)
                               || string.Equals(trophy.Attribute("unlocked")?.Value, "yes", StringComparison.OrdinalIgnoreCase);
                long.TryParse(trophy.Attribute("timestamp")?.Value, out var timestamp);
                var name = trophy.Elements().FirstOrDefault(element => element.Name.LocalName == "name")?.Value.Trim();
                var detail = trophy.Elements().FirstOrDefault(element => element.Name.LocalName == "detail")?.Value.Trim();
                var type = TrophyType(trophy.Attribute("ttype")?.Value);
                if (entries.TryGetValue(id, out var existing) && !isPreferred)
                {
                    entries[id] = existing with { Earned = existing.Earned || unlocked };
                    continue;
                }

                entries[id] = new PlatformAchievement(id.ToString(CultureInfo.InvariantCulture), name, detail,
                    unlocked, unlocked ? timestamp : 0, TrophyType: type,
                    Hidden: string.Equals(trophy.Attribute("hidden")?.Value, "yes", StringComparison.OrdinalIgnoreCase));
            }
        }

        return entries.Values.ToArray();
    }

    public static IReadOnlyList<PlatformAchievement> ReadPs5TrophyState(string path)
    {
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;
        if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return [];

        // Modern KytyPS5 _Trophies format: { "title_id": "PPSA...", "trophies": [ { "trophy_id": 0, "type": "platinum", "unlocked": true, "unlocked_timestamp": "..." } ] }
        if (root.TryGetProperty("trophies", out var trophiesArray) && trophiesArray.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            var modern = new List<PlatformAchievement>();
            foreach (var entry in trophiesArray.EnumerateArray())
            {
                if (entry.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                string? id = null;
                if (entry.TryGetProperty("trophy_id", out var idEl)) id = idEl.ToString();
                else if (entry.TryGetProperty("id", out var altIdEl)) id = altIdEl.ToString();
                if (string.IsNullOrWhiteSpace(id)) continue;

                var earned = entry.TryGetProperty("unlocked", out var unlEl)
                             && (SteamAppIdResolver.NormalizeEarnedFlag(unlEl) ?? false);
                long ts = 0;
                if (earned)
                {
                    if (entry.TryGetProperty("unlocked_timestamp", out var tsEl))
                        ts = SteamAppIdResolver.NormalizeUnixSeconds(tsEl);
                    else if (entry.TryGetProperty("unlockedAt", out var atEl))
                        ts = SteamAppIdResolver.NormalizeUnixSeconds(atEl);
                }
                var type = entry.TryGetProperty("type", out var typeEl) ? typeEl.GetString()?.ToLowerInvariant() : null;
                var name = entry.TryGetProperty("name", out var nameEl) ? nameEl.GetString()?.Trim() : null;
                var detail = entry.TryGetProperty("detail", out var detEl) ? detEl.GetString()?.Trim()
                    : entry.TryGetProperty("description", out var descEl) ? descEl.GetString()?.Trim() : null;
                var hidden = entry.TryGetProperty("hidden", out var hidEl) && hidEl.ValueKind == System.Text.Json.JsonValueKind.True;
                modern.Add(new PlatformAchievement(id, name, detail, earned, earned ? ts : 0, TrophyType: type, Hidden: hidden));
            }
            return modern;
        }

        // Legacy KytyPS5 format: { "unlockedTrophies": [0, 1], "unlockedAt": { "0": 1700000000000 } }
        if (!root.TryGetProperty("unlockedTrophies", out var unlockedList)
            || unlockedList.ValueKind != System.Text.Json.JsonValueKind.Array) return [];

        var unlocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in unlockedList.EnumerateArray())
        {
            if (item.ValueKind == System.Text.Json.JsonValueKind.Number) unlocked.Add(item.GetRawText());
        }

        var timestamps = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("unlockedAt", out var unlockedAt) && unlockedAt.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            foreach (var property in unlockedAt.EnumerateObject())
            {
                var seconds = SteamAppIdResolver.NormalizeUnixSeconds(property.Value);
                if (seconds > 0) timestamps[property.Name] = seconds;
            }
        }

        var results = new List<PlatformAchievement>(unlocked.Count);
        foreach (var id in unlocked.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            results.Add(new PlatformAchievement(id, Earned: true,
                EarnedTimeUnix: timestamps.GetValueOrDefault(id)));
        }
        return results;
    }

    private static string TrophyType(string? value) => (value ?? string.Empty).ToUpperInvariant() switch
    {
        "P" => "platinum",
        "G" => "gold",
        "S" => "silver",
        "B" => "bronze",
        "" => "unknown",
        var other => other.ToLowerInvariant()
    };
}
