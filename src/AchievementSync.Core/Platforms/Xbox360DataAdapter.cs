using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AchievementSync.Core.Platforms;

/// <summary>Xbox 360 reader covering Xenia GPD profile files (XDBF) and X360Recomp / ReXGlue
/// progress files (JSON, TSV, TOML). Read-only; no source is ever modified.</summary>
public sealed partial class Xbox360DataAdapter() : PlatformDataAdapter("xbox360", "Xbox 360 (Xenia GPD / X360Recomp)")
{
    private const int XdbfHeaderSize = 0x18;
    private const int XdbfEntrySize = 18;
    private const int XdbfFreeEntrySize = 8;
    private const int AchievementNamespace = 1;
    private const uint AchievementEarnedFlag = 0x20000;
    private static readonly Regex TitleIdPattern = new(@"\b[0-9A-F]{8}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static readonly Xbox360RecompRule[] RecompRules =
    [
        new("Dante's Inferno", ["dantes_inferno.exe", "dantes_inferno_native.exe", "dantesinferno.exe"], "454108CF", ["user", "userdata", "saves"]),
        new("LEGO Dimensions", ["legodimensions.exe"], "5752084D", ["user", "userdata", "saves"]),
        new("Midnight Club: Los Angeles", ["midnightclubla.exe", "mcla.exe", "larecomp.exe"], "545407F8", ["user", "userdata", "saves"]),
        new("Spider-Man: Edge of Time", ["SpiderManEOT.exe"], "415608B5", ["user", "userdata", "saves"]),
        new("Blue Dragon (re:Blue)", ["reblue.exe"], "4D5307DF", ["profiles"]),
        new("Gears of War 2 Hollow", ["gears2.exe", "hollow.exe"], "4D53082D", [Path.Combine("GearGame", "SaveData")]),
        new("Unleashed Recompiled", ["UnleashedRecomp.exe"], "53450819", ["user", "userdata", "saves"]),
        new("Castlevania: Symphony of the Night (Nocturne)", ["nocturnerecomp.exe"], "58410847", ["user", "userdata", "saves"]),
        new("Silent Hill: Downpour", ["downpour.exe"], "4B4E0823", ["user"]),
        new("Deadly Premonition", ["deadlyprem.exe", "deadlyprem_usa.exe"], "4D4A07DD", ["userdata"]),
        new("Fable II (himdo)", ["fable_2.exe"], "4D5307F1", ["saves"]),
        new("Naughty Bear (ReStuff)", ["restuff.exe"], "464F07D2", ["Documents"]),
        new("Condemned 2: Bloodshot", ["condemned2recomp.exe"], "534507EF", ["UserData"]),
        new("Project Gotham Racing 4", ["pgr4_recomp.exe", "pgr4_recompiled.exe"], "4D5307F9", ["Documents"]),
        new("Crackdown 2", ["crackdown2.exe"], "4D5308BC", ["user", "userdata", "saves"]),
        new("Banjo-Kazooie: Nuts & Bolts (reNut)", ["renut.exe"], "4D5307ED", ["renut.cfg"])
    ];

    public override bool IsConfigured => IsConfiguredFor(PlatformSourceOptions.CreateDefault());

    public override bool IsConfiguredFor(PlatformSourceOptions options) =>
        new[] { options.Xbox360Root, options.XeniaRoot, options.RecompRoot }
            .Any(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path));

    public override string? ConfigurationHint =>
        "Keep Xenia content and X360Recomp profiles in their default folders, or choose them on the Platforms page.";

    public override Task<PlatformCollectResult> CollectAsync(PlatformSourceOptions options, CancellationToken cancellationToken = default)
    {
        var roots = new[] { options.Xbox360Root, options.XeniaRoot, options.RecompRoot }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(Directory.Exists)
            .ToArray();
        var games = new SortedDictionary<string, PlatformGameSnapshot>(StringComparer.OrdinalIgnoreCase);
        var messages = new List<string>();
        if (roots.Length == 0)
            return Task.FromResult(new PlatformCollectResult(Platform, [], ["No Xenia or X360Recomp folder was found."]));

        foreach (var root in roots)
        {
            // Apply per-game Xbox 360 recomp rules (executable -> TitleID -> game-specific save roots & renut.cfg).
            CollectRecompRules(root, games, messages, cancellationToken);

            foreach (var file in EnumerateFiles(root, "*.gpd", 8))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var achievements = ParseGpd(file);
                    if (achievements.Count == 0) continue;
                    var titleId = TitleIdFromPath(file) ?? "unknown";
                    Add(titleId, null, file, achievements);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    messages.Add($"Xbox 360: skipped {Path.GetFileName(file)} ({exception.GetType().Name}).");
                }
            }

            foreach (var file in EnumerateFiles(root, "*.*", 8))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var extension = Path.GetExtension(file);
                if (!extension.Equals(".toml", StringComparison.OrdinalIgnoreCase)
                    && !extension.Equals(".tsv", StringComparison.OrdinalIgnoreCase)
                    && !extension.Equals(".json", StringComparison.OrdinalIgnoreCase)) continue;
                if (!IsRecompProgressFile(file)) continue;
                try
                {
                    var achievements = extension.ToLowerInvariant() switch
                    {
                        ".tsv" => ParseTsv(file),
                        ".json" => ParseRecompJson(file),
                        _ => ParseTomlUnlocked(file)
                    };
                    if (achievements.Count == 0) continue;
                    Add(TitleIdFromPath(file) ?? "unknown", null, file, achievements);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
                {
                    messages.Add($"Xbox 360: skipped {Path.GetFileName(file)} ({exception.GetType().Name}).");
                }
            }
        }

        return Task.FromResult(new PlatformCollectResult(Platform, games.Values.ToList(), messages));

        void Add(string gameId, string? title, string source, IReadOnlyList<PlatformAchievement> achievements)
        {
            if (!games.TryGetValue(gameId, out var existing) || achievements.Count > existing.Achievements.Count)
                games[gameId] = new PlatformGameSnapshot(Platform, gameId, title ?? existing?.Title, source, achievements);
        }
    }

    private static void CollectRecompRules(
        string root,
        SortedDictionary<string, PlatformGameSnapshot> games,
        List<string> messages,
        CancellationToken cancellationToken)
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        foreach (var rule in RecompRules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? detectedExeDir = null;
            foreach (var exeName in rule.Executables)
            {
                var found = EnumerateFiles(root, exeName, 4).FirstOrDefault();
                if (found is not null)
                {
                    detectedExeDir = Path.GetDirectoryName(found);
                    break;
                }
            }

            // Special rule for Gears of War 2 Hollow when folder has GearGame\SaveData without specific exe name
            if (detectedExeDir is null && rule.TitleId == "4D53082D")
            {
                var gearSave = Path.Combine(root, "GearGame", "SaveData");
                if (Directory.Exists(gearSave)) detectedExeDir = root;
            }

            if (detectedExeDir is null) continue;

            var searchDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { detectedExeDir };
            foreach (var hint in rule.SaveSubPaths)
            {
                if (hint.Equals("Documents", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var exeName in rule.Executables)
                    {
                        var appName = Path.GetFileNameWithoutExtension(exeName);
                        searchDirs.Add(Path.Combine(docs, appName));
                    }
                    searchDirs.Add(Path.Combine(docs, rule.GameName));
                }
                else if (hint.Equals("renut.cfg", StringComparison.OrdinalIgnoreCase))
                {
                    var cfgPath = Path.Combine(detectedExeDir, "renut.cfg");
                    if (File.Exists(cfgPath))
                    {
                        foreach (var line in SafeReadLines(cfgPath))
                        {
                            var eq = line.IndexOf('=');
                            if (eq <= 0) continue;
                            var val = line[(eq + 1)..].Trim().Trim('"');
                            if (Directory.Exists(val)) searchDirs.Add(val);
                        }
                    }
                }
                else
                {
                    searchDirs.Add(Path.Combine(detectedExeDir, hint));
                    searchDirs.Add(Path.Combine(local, Path.GetFileNameWithoutExtension(rule.Executables[0]), hint));
                    searchDirs.Add(Path.Combine(roaming, Path.GetFileNameWithoutExtension(rule.Executables[0]), hint));
                }
            }

            foreach (var dir in searchDirs.Where(Directory.Exists))
            {
                foreach (var file in EnumerateFiles(dir, "*.*", 4))
                {
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    if (ext is not (".gpd" or ".toml" or ".tsv" or ".json")) continue;
                    if (ext != ".gpd" && !IsRecompProgressFile(file) && !Path.GetFileName(file).Contains("achiev", StringComparison.OrdinalIgnoreCase))
                        continue;

                    try
                    {
                        var achievements = ext switch
                        {
                            ".gpd" => ParseGpd(file),
                            ".tsv" => ParseTsv(file),
                            ".json" => ParseRecompJson(file),
                            _ => ParseTomlUnlocked(file)
                        };
                        if (achievements.Count == 0) continue;
                        var key = TitleIdFromPath(file) ?? rule.TitleId;
                        if (!games.TryGetValue(key, out var existing) || achievements.Count > existing.Achievements.Count)
                            games[key] = new PlatformGameSnapshot("xbox360", key, rule.GameName, file, achievements, "xbox360-recomp");
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
                    {
                        messages.Add($"Xbox 360 Recomp ({rule.GameName}): skipped {Path.GetFileName(file)}.");
                    }
                }
            }
        }
    }

    private static IEnumerable<string> SafeReadLines(string path)
    {
        try { return File.ReadAllLines(path); }
        catch { return []; }
    }

    public sealed record Xbox360RecompRule(string GameName, string[] Executables, string TitleId, string[] SaveSubPaths);

    private static bool IsRecompProgressFile(string file)
    {
        var parent = Path.GetFileName(Path.GetDirectoryName(file));
        if (string.Equals(parent, "achievements", StringComparison.OrdinalIgnoreCase)) return true;
        return Path.GetExtension(file).Equals(".toml", StringComparison.OrdinalIgnoreCase)
               && Path.GetFileNameWithoutExtension(file).Length == 8
               && Path.GetFileNameWithoutExtension(file).All(Uri.IsHexDigit);
    }

    private static string? TitleIdFromPath(string file)
    {
        for (var directory = Path.GetDirectoryName(file); directory is not null; directory = Path.GetDirectoryName(directory))
        {
            var name = Path.GetFileName(directory);
            var match = TitleIdPattern.Match(name);
            if (match.Success) return match.Value.ToUpperInvariant();
        }
        var stem = Path.GetFileNameWithoutExtension(file);
        return stem.Length == 8 && stem.All(Uri.IsHexDigit) ? stem.ToUpperInvariant() : null;
    }

    private static IEnumerable<string> EnumerateFiles(string root, string pattern, int maxDepth)
    {
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));
        var visited = 0;
        while (pending.Count > 0 && visited++ < 30_000)
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

    /// <summary>Parses an XDBF GPD profile and returns the achievements stored in namespace 1.</summary>
    public static IReadOnlyList<PlatformAchievement> ParseGpd(string path)
    {
        var data = File.ReadAllBytes(path);
        return ParseGpd(data);
    }

    public static IReadOnlyList<PlatformAchievement> ParseGpd(ReadOnlySpan<byte> data)
    {
        if (data.Length < XdbfHeaderSize) return [];
        if (!data[..4].SequenceEqual("XDBF"u8)) return [];
        var bigEndian = false;
        var entryTableLength = ReadUInt32(data, 0x08, false);
        var entryCount = ReadUInt32(data, 0x0C, false);
        if (entryCount > 100_000 || entryTableLength > data.Length * 4L)
        {
            bigEndian = true;
            entryTableLength = ReadUInt32(data, 0x08, true);
            entryCount = ReadUInt32(data, 0x0C, true);
        }
        if (entryCount > 100_000 || entryTableLength > data.Length * 4L) return [];

        var freeCount = ReadUInt32(data, 0x14, bigEndian);
        var entryEntries = entryTableLength % XdbfEntrySize == 0 ? entryTableLength / XdbfEntrySize : entryTableLength;
        var freeEntries = freeCount;
        var baseData = XdbfHeaderSize + entryEntries * XdbfEntrySize + freeEntries * XdbfFreeEntrySize;
        if (baseData < 0 || baseData >= data.Length) return [];

        var results = new List<PlatformAchievement>();
        var count = Math.Min(entryCount, entryEntries);
        for (var index = 0; index < count; index++)
        {
            var entry = XdbfHeaderSize + (int)index * XdbfEntrySize;
            if (entry + XdbfEntrySize > data.Length) break;
            var ns = ReadUInt16(data, entry, bigEndian);
            if (ns != AchievementNamespace) continue;
            var offset = ReadUInt32(data, entry + 10, bigEndian);
            var length = ReadUInt32(data, entry + 14, bigEndian);
            if (length == 0) continue;
            var start = baseData + offset;
            if (start < 0 || start + length > data.Length) continue;
            var payload = data.Slice((int)start, (int)length);
            var achievement = ParseAchievementPayload(payload, bigEndian);
            if (achievement is not null) results.Add(achievement);
        }

        return results;
    }

    private static PlatformAchievement? ParseAchievementPayload(ReadOnlySpan<byte> payload, bool bigEndian)
    {
        if (payload.Length < 0x1C) return null;
        var structSize = ReadUInt32(payload, 0x00, bigEndian);
        if (structSize != 0x1C) return null;
        var achievementId = ReadUInt32(payload, 0x04, bigEndian);
        var flags = ReadUInt32(payload, 0x10, bigEndian);
        var unlockRaw = bigEndian
            ? BinaryPrimitives.ReadUInt64BigEndian(payload[0x14..0x1C])
            : BinaryPrimitives.ReadUInt64LittleEndian(payload[0x14..0x1C]);
        var cursor = 0x1C;
        var name = ReadUtf16Be(payload, ref cursor);
        var unlockedDescription = ReadUtf16Be(payload, ref cursor);
        var lockedDescription = ReadUtf16Be(payload, ref cursor);
        var earned = (flags & AchievementEarnedFlag) != 0;
        return new PlatformAchievement(
            achievementId.ToString(CultureInfo.InvariantCulture),
            string.IsNullOrWhiteSpace(name) ? null : name,
            earned ? unlockedDescription : lockedDescription,
            earned,
            earned ? XboxDataAdapter.FileTimeToUnixSeconds(unlockRaw) : 0,
            TrophyType: earned ? "earned" : "locked");
    }

    private static string ReadUtf16Be(ReadOnlySpan<byte> data, ref int cursor)
    {
        var start = cursor;
        while (cursor + 1 < data.Length && (data[cursor] != 0 || data[cursor + 1] != 0)) cursor += 2;
        if (cursor + 1 >= data.Length) { cursor = data.Length; return string.Empty; }
        var text = Encoding.BigEndianUnicode.GetString(data[start..cursor]);
        cursor += 2;
        return text;
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset, bool bigEndian) =>
        bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(data[offset..]) : BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset, bool bigEndian) =>
        offset + 4 <= data.Length
            ? bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(data[offset..]) : BinaryPrimitives.ReadUInt32LittleEndian(data[offset..])
            : 0;

    public static IReadOnlyList<PlatformAchievement> ParseTsv(string path)
    {
        var lines = File.ReadAllLines(path);
        if (lines.Length < 2) return [];
        var results = new List<PlatformAchievement>();
        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var columns = line.Split('\t');
            if (columns.Length < 2) continue;
            var id = columns[0].Trim();
            if (id.Length == 0) continue;
            long time = 0;
            if (columns.Length > 2 && long.TryParse(columns[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var raw))
                time = XboxDataAdapter.FileTimeToUnixSeconds(unchecked((ulong)raw));
            results.Add(new PlatformAchievement(id, Earned: true, EarnedTimeUnix: time));
        }
        return results;
    }

    public static IReadOnlyList<PlatformAchievement> ParseRecompJson(string path)
    {
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path),
            new System.Text.Json.JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = System.Text.Json.JsonCommentHandling.Skip });
        var results = new List<PlatformAchievement>();
        var root = document.RootElement;
        if (root.ValueKind == System.Text.Json.JsonValueKind.Object
            && root.TryGetProperty("entries", out var entries)
            && entries.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                var id = entry.TryGetProperty("id", out var idValue) ? idValue.ToString() : null;
                if (string.IsNullOrWhiteSpace(id)) continue;
                var unlocked = entry.TryGetProperty("unlocked", out var unlockedValue) && unlockedValue.ValueKind == System.Text.Json.JsonValueKind.True;
                results.Add(new PlatformAchievement(id, Earned: unlocked));
            }
            return results;
        }

        if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return results;
        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind == System.Text.Json.JsonValueKind.True)
                results.Add(new PlatformAchievement(property.Name, Earned: true));
            else if (property.Value.ValueKind == System.Text.Json.JsonValueKind.Object
                     && property.Value.TryGetProperty("earned", out var earned))
                results.Add(new PlatformAchievement(property.Name, Earned: earned.ValueKind == System.Text.Json.JsonValueKind.True));
        }
        return results;
    }

    public static IReadOnlyList<PlatformAchievement> ParseTomlUnlocked(string path)
    {
        var text = File.ReadAllText(path);
        var results = new List<PlatformAchievement>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var arrayMatch = Regex.Match(text, @"(?s)\bunlocked\s*=\s*\[(.*?)\]");
        if (arrayMatch.Success)
        {
            foreach (Match token in Regex.Matches(arrayMatch.Groups[1].Value, "\"([^\"]+)\"|'([^']+)'|\\b(\\d+)\\b"))
            {
                var id = token.Groups[1].Success ? token.Groups[1].Value
                    : token.Groups[2].Success ? token.Groups[2].Value : token.Groups[3].Value;
                if (!seen.Add(id)) continue;
                results.Add(new PlatformAchievement(id, Earned: true));
            }
            return results;
        }

        foreach (Match table in Regex.Matches(text, @"(?ms)^\s*\[\s*unlocked\.(?<id>[^\]]+)\]\s*(?<body>.*?)(?=^\s*\[|\z)"))
        {
            var id = table.Groups["id"].Value.Trim().Trim('"', '\'');
            if (!seen.Add(id)) continue;
            var filetimeMatch = Regex.Match(table.Groups["body"].Value, @"filetime\s*=\s*(\d+)");
            long time = filetimeMatch.Success && long.TryParse(filetimeMatch.Groups[1].Value, out var raw)
                ? XboxDataAdapter.FileTimeToUnixSeconds(unchecked((ulong)raw))
                : 0;
            results.Add(new PlatformAchievement(id, Earned: true, EarnedTimeUnix: time));
        }
        return results;
    }
}
