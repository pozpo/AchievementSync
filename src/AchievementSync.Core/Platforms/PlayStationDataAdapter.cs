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
        new[] { options.Rpcs3Root, options.Ps4Root, options.Ps5Root }
            .Any(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path));

    public override string? ConfigurationHint =>
        "Keep RPCS3, shadPS4 or Kyty data in its default folder, or choose it on the Platforms page.";

    public override Task<PlatformCollectResult> CollectAsync(PlatformSourceOptions options, CancellationToken cancellationToken = default)
    {
        var games = new SortedDictionary<string, PlatformGameSnapshot>(StringComparer.OrdinalIgnoreCase);
        var messages = new List<string>();
        var anyRoot = false;

        if (!string.IsNullOrWhiteSpace(options.Rpcs3Root) && Directory.Exists(options.Rpcs3Root))
        {
            anyRoot = true;
            foreach (var conf in Enumerate(options.Rpcs3Root, "TROPCONF.SFM", 10))
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
                        TrophyTitle(conf), conf, achievements);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException)
                {
                    messages.Add($"PlayStation: skipped {Path.GetFileName(conf)} ({exception.GetType().Name}).");
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(options.Ps4Root) && Directory.Exists(options.Ps4Root))
        {
            anyRoot = true;
            foreach (var xml in Enumerate(options.Ps4Root, "TROP*.XML", 10))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var xmlDir = Path.GetDirectoryName(xml)!;
                    if (!string.Equals(Path.GetFileName(xmlDir), "Xml", StringComparison.OrdinalIgnoreCase)) continue;
                    var trophyDir = Path.GetDirectoryName(xmlDir)!;
                    var slotParent = Path.GetDirectoryName(trophyDir)!;
                    var gameId = string.Equals(Path.GetFileName(slotParent), "TrophyFiles", StringComparison.OrdinalIgnoreCase)
                        ? Path.GetFileName(Path.GetDirectoryName(slotParent))!
                        : Path.GetFileName(trophyDir);
                    var achievements = ReadPs4TrophyXmlDirectory(xmlDir);
                    if (achievements.Count == 0) continue;
                    games[gameId] = new PlatformGameSnapshot(Platform, gameId, null, xmlDir, achievements);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException)
                {
                    messages.Add($"PlayStation: skipped {Path.GetFileName(xml)} ({exception.GetType().Name}).");
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(options.Ps5Root) && Directory.Exists(options.Ps5Root))
        {
            anyRoot = true;
            foreach (var stateFile in Enumerate(options.Ps5Root, "trophies_*.json", 10))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var directory = Path.GetDirectoryName(stateFile)!;
                    var gameId = Path.GetFileName(directory);
                    var achievements = ReadPs5TrophyState(stateFile);
                    if (achievements.Count == 0) continue;
                    games[gameId] = new PlatformGameSnapshot(Platform, gameId, null, stateFile, achievements);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
                {
                    messages.Add($"PlayStation: skipped {Path.GetFileName(stateFile)} ({exception.GetType().Name}).");
                }
            }
        }

        if (!anyRoot) messages.Add("No PlayStation trophy folder was found.");
        return Task.FromResult(new PlatformCollectResult(Platform, games.Values.ToList(), messages));
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
        if (root.ValueKind != System.Text.Json.JsonValueKind.Object
            || !root.TryGetProperty("unlockedTrophies", out var unlockedList)
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
                if (!property.Value.TryGetInt64(out var milliseconds) || milliseconds <= 0) continue;
                timestamps[property.Name] = milliseconds / 1000;
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
