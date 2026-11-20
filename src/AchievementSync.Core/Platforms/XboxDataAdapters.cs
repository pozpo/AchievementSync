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
