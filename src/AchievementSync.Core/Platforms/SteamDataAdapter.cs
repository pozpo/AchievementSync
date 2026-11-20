using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AchievementSync.Core.Metadata;
using AchievementSync.Core.Scanning.PlatformAdapters;

namespace AchievementSync.Core.Platforms;

/// <summary>Base class for adapters that enumerate local platform data stores and convert them
/// to viewer-compatible achievement states. Adapters never modify their sources.</summary>
public abstract class PlatformDataAdapter(string platform, string displayName) : PlatformAdapter(platform, displayName)
{
    public abstract Task<PlatformCollectResult> CollectAsync(PlatformSourceOptions options, CancellationToken cancellationToken = default);

    public override async Task<PlatformResolveResult> ResolveAsync(string appId, CancellationToken cancellationToken = default)
    {
        var collected = await CollectAsync(new PlatformSourceOptions(), cancellationToken).ConfigureAwait(false);
        var match = collected.Games.FirstOrDefault(game =>
            string.Equals(game.GameId, appId, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return new PlatformResolveResult(appId, Platform, false,
                Error: "No local achievement data found for this game id.", IsConfigured: IsConfigured);
        }

        var states = match.Achievements
            .Where(item => item.EarnedKnown)
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key,
                group => new PlatformAchievementState(group.Any(item => item.Earned), group.Max(item => item.EarnedTimeUnix)),
                StringComparer.OrdinalIgnoreCase);
        return new PlatformResolveResult(appId, Platform, states.Count > 0, states);
    }
}

/// <summary>Deep Steam reader: appcache schema + UserGameStats binaries (KV1) and userdata
/// achievement files. Maps stat bit vectors to named achievements without any network call.</summary>
public sealed partial class SteamDataAdapter(PlatformSourceOptions? defaults = null)
    : PlatformDataAdapter("steam", "Steam (appcache + schema + KV stats)")
{
    private static readonly Regex SchemaFilePattern = new(@"^UserGameStatsSchema_(\d{1,10})\.bin$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex UserStatsFilePattern = new(@"^UserGameStats_(\d+)_(\d{1,10})\.bin$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly PlatformSourceOptions _defaults = defaults ?? PlatformSourceOptions.CreateDefault();

    public override bool IsConfigured => IsConfiguredFor(_defaults);

    public override bool IsConfiguredFor(PlatformSourceOptions options) => Directory.Exists(ResolveRoot(options));

    public override string? ConfigurationHint =>
        "Point AchievementSync at your Steam installation so appcache stats can be read.";

    public string GetRoot(PlatformSourceOptions? options = null) => ResolveRoot(options ?? _defaults);

    private static string ResolveRoot(PlatformSourceOptions options) => options.SteamRoot ?? string.Empty;

    public override async Task<PlatformCollectResult> CollectAsync(PlatformSourceOptions options, CancellationToken cancellationToken = default)
    {
        var root = ResolveRoot(options);
        var messages = new List<string>();
        var games = new List<PlatformGameSnapshot>();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return new PlatformCollectResult(Platform, games, ["The Steam folder was not found."]);

        var statsDirectory = Path.Combine(root, "appcache", "stats");
        var schemas = new Dictionary<string, string>(StringComparer.Ordinal);
        var userBins = new Dictionary<string, (string Path, DateTime Modified)>(StringComparer.Ordinal);

        if (Directory.Exists(statsDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(statsDirectory, "*.bin", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(file);
                var schemaMatch = SchemaFilePattern.Match(name);
                if (schemaMatch.Success)
                {
                    schemas[schemaMatch.Groups[1].Value] = file;
                    continue;
                }

                var userMatch = UserStatsFilePattern.Match(name);
                if (!userMatch.Success) continue;
                var appId = userMatch.Groups[2].Value;
                DateTime modified;
                try { modified = File.GetLastWriteTimeUtc(file); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { continue; }
                if (!userBins.TryGetValue(appId, out var existing) || modified > existing.Modified)
                    userBins[appId] = (file, modified);
            }
        }

        foreach (var (appId, (path, _)) in userBins)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!schemas.TryGetValue(appId, out var schemaPath)) continue;
            try
            {
                var schema = SteamSchemaReader.Parse(schemaPath);
                if (schema.Count == 0) continue;
                var userStats = SteamUserStatsReader.Parse(path);
                var achievements = SteamSchemaReader.BuildAchievements(schema, userStats);
                if (achievements.Count == 0) continue;
                games.Add(new PlatformGameSnapshot(Platform, appId, null, path, achievements));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or KvFormatException)
            {
                messages.Add($"Steam: skipped app {appId} ({exception.GetType().Name}).");
            }
        }

        CollectUserData(root, games, messages, cancellationToken);
        return new PlatformCollectResult(Platform, games, messages);
    }

    public override async Task<PlatformResolveResult> ResolveAsync(string appId, CancellationToken cancellationToken = default)
    {
        if (!AppIdValidator.IsValid(appId))
            return new PlatformResolveResult(appId, Platform, false, Error: "Invalid AppID.", IsConfigured: IsConfigured);

        var collected = await CollectAsync(_defaults, cancellationToken).ConfigureAwait(false);
        var match = collected.Games.FirstOrDefault(game => string.Equals(game.GameId, appId, StringComparison.Ordinal));
        if (match is null)
            return new PlatformResolveResult(appId, Platform, false,
                Error: "No local Steam achievement data found for this AppID.", IsConfigured: IsConfigured);

        var states = match.Achievements
            .Where(item => item.EarnedKnown)
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key,
                group => new PlatformAchievementState(group.Any(item => item.Earned), group.Max(item => item.EarnedTimeUnix)),
                StringComparer.OrdinalIgnoreCase);
        return new PlatformResolveResult(appId, Platform, states.Count > 0, states, IsConfigured: IsConfigured);
    }

    private void CollectUserData(string root, List<PlatformGameSnapshot> games, List<string> messages,
        CancellationToken cancellationToken)
    {
        var userData = Path.Combine(root, "userdata");
        if (!Directory.Exists(userData)) return;
        foreach (var accountDir in Directory.EnumerateDirectories(userData))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var appDir in Directory.EnumerateDirectories(accountDir))
            {
                var appId = Path.GetFileName(appDir);
                if (!IsNumericId(appId)) continue;
                if (games.Any(game => string.Equals(game.GameId, appId, StringComparison.Ordinal))) continue;
                var states = new SortedDictionary<string, PlatformAchievement>(StringComparer.OrdinalIgnoreCase);
                foreach (var candidate in EnumerateAchievementFiles(appDir))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var parsed = ParseAchievementMap(candidate);
                        foreach (var (id, achievement) in parsed) states.TryAdd(id, achievement);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or KvFormatException)
                    {
                        messages.Add($"Steam: skipped {Path.GetFileName(candidate)} ({exception.GetType().Name}).");
                    }
                }

                if (states.Count > 0)
                    games.Add(new PlatformGameSnapshot(Platform, appId, null, appDir, states.Values.ToArray()));
            }
        }
    }

    private static IEnumerable<string> EnumerateAchievementFiles(string appDir)
    {
        foreach (var name in new[] { "achievements.bin", "achievements.json", "user_stats.bin" })
        {
            var statsPath = Path.Combine(appDir, "stats", name);
            if (File.Exists(statsPath)) yield return statsPath;
        }
    }

    private static IReadOnlyDictionary<string, PlatformAchievement> ParseAchievementMap(string path)
    {
        var result = new Dictionary<string, PlatformAchievement>(StringComparer.OrdinalIgnoreCase);
        if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path));
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return result;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                var earned = false;
                long earnedTime = 0;
                foreach (var field in property.Value.EnumerateObject())
                {
                    if (field.NameEquals("earned") || field.NameEquals("achieved"))
                        earned = field.Value.ValueKind == System.Text.Json.JsonValueKind.True;
                    else if (field.NameEquals("earned_time") || field.NameEquals("unlocktime"))
                        earned = earned || field.Value.TryGetInt64(out var time) && time > 0;
                    if (field.NameEquals("earned_time") && field.Value.TryGetInt64(out var ts)) earnedTime = ts;
                }
                result[property.Name] = new PlatformAchievement(property.Name, Earned: earned, EarnedTimeUnix: earnedTime);
            }
            return result;
        }

        var root = KvBinaryParser.ParseFile(path);
        Walk(root);

        void Walk(KvObject node)
        {
            foreach (var item in node.Items.OfType<KvObject>())
            {
                if (LooksLikeAchievement(item))
                {
                    var name = item.Name;
                    var earned = ReadEarned(item);
                    var time = item.Long("earned_time") ?? item.Long("unlocktime") ?? item.Long("time") ?? 0;
                    result[name] = new PlatformAchievement(name, Earned: earned, EarnedTimeUnix: time);
                }
                Walk(item);
            }
        }

        static bool LooksLikeAchievement(KvObject node) =>
            node.TryGet("earned", out _) || node.TryGet("achieved", out _) ||
            node.TryGet("earned_time", out _) || node.TryGet("unlocktime", out _);

        static bool ReadEarned(KvObject node)
        {
            foreach (var key in new[] { "earned", "achieved" })
            {
                if (!node.TryGet(key, out var value)) continue;
                return value switch
                {
                    KvInt32 number => number.Value != 0,
                    KvUInt64 number => number.Value != 0,
                    KvString text => text.Value is "1" or "true" or "True",
                    _ => false
                };
            }
            var time = node.Long("earned_time") ?? node.Long("unlocktime") ?? 0;
            return time > 0;
        }

        return result;
    }

    internal static bool IsNumericId(string value) =>
        value.Length is >= 1 and <= 12 && value.All(char.IsAsciiDigit) && value.Any(character => character != '0');
}

/// <summary>Parsed Steam achievement schema entry: stat id, bit index and viewer metadata.</summary>
internal sealed record SteamSchemaEntry(int StatId, int Bit, string ApiName, string? DisplayName, string? Description,
    string? Icon, string? IconGray);

internal static class SteamSchemaReader
{
    public static List<SteamSchemaEntry> Parse(string schemaPath)
    {
        var root = KvBinaryParser.ParseFile(schemaPath);
        var entries = new List<SteamSchemaEntry>();
        Walk(root);
        return entries;

        void Walk(KvObject node)
        {
            if (int.TryParse(node.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var statId)
                && node.Child("bits") is { } bits)
            {
                foreach (var bitItem in bits.Items.OfType<KvObject>())
                {
                    if (!int.TryParse(bitItem.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bitFromKey)) continue;
                    var bit = (int)(bitItem.Long("bit") ?? bitFromKey);
                    var apiName = bitItem.String("name");
                    var display = bitItem.Child("display");
                    apiName ??= display?.Child("name")?.String("token");
                    if (string.IsNullOrWhiteSpace(apiName)) continue;
                    entries.Add(new SteamSchemaEntry(statId, bit, apiName,
                        Localized(display?.Child("name")),
                        Localized(display?.Child("desc")),
                        display?.String("icon") ?? bitItem.String("icon"),
                        display?.String("icon_gray") ?? display?.String("icongray")));
                }
            }

            foreach (var child in node.Items.OfType<KvObject>()) Walk(child);
        }

        static string? Localized(KvObject? node)
        {
            if (node is null) return null;
            return node.String("english") ?? node.String("token") ??
                   node.Items.OfType<KvString>().Select(item => item.Value).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        }
    }

    public static IReadOnlyList<PlatformAchievement> BuildAchievements(
        IReadOnlyList<SteamSchemaEntry> schema,
        IReadOnlyDictionary<int, SteamUserStat> userStats)
    {
        var results = new List<PlatformAchievement>(schema.Count);
        foreach (var entry in schema)
        {
            var stat = userStats.GetValueOrDefault(entry.StatId);
            var mask = stat?.Data ?? 0;
            var earned = entry.Bit is >= 0 and < 32 && ((mask >> entry.Bit) & 1) == 1;
            var earnedTime = earned && stat is not null && stat.AchievementTimes.TryGetValue(entry.Bit, out var time) ? time : 0;
            results.Add(new PlatformAchievement(entry.ApiName, entry.DisplayName, entry.Description,
                earned, earnedTime, entry.Icon, entry.IconGray));
        }

        return results;
    }
}

internal sealed record SteamUserStat(int StatId, uint Data, IReadOnlyDictionary<int, long> AchievementTimes);

internal static class SteamUserStatsReader
{
    public static Dictionary<int, SteamUserStat> Parse(string path)
    {
        var root = KvBinaryParser.ParseFile(path);
        var stats = new Dictionary<int, SteamUserStat>();
        Walk(root);
        return stats;

        void Walk(KvObject node)
        {
            if (int.TryParse(node.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var statId)
                && node.TryGet("data", out var dataValue))
            {
                var data = dataValue switch
                {
                    KvInt32 number => unchecked((uint)number.Value),
                    KvUInt64 number => unchecked((uint)number.Value),
                    KvFloat number => unchecked((uint)(long)number.Value),
                    _ => 0u
                };
                var times = new Dictionary<int, long>();
                if (node.Child("AchievementTimes") is { } timesNode)
                {
                    foreach (var item in timesNode.Items)
                    {
                        if (int.TryParse(item.Name, out var bit) && TryReadLong(item, out var seconds))
                            times[bit] = seconds;
                    }
                }

                stats[statId] = new SteamUserStat(statId, data, times);
            }

            foreach (var child in node.Items.OfType<KvObject>()) Walk(child);
        }

        static bool TryReadLong(KvValue value, out long number)
        {
            switch (value)
            {
                case KvInt32 int32: number = int32.Value; return true;
                case KvUInt64 uint64: number = unchecked((long)uint64.Value); return true;
                case KvInt64 int64: number = int64.Value; return true;
                case KvString text when long.TryParse(text.Value, out var parsed): number = parsed; return true;
                default: number = 0; return false;
            }
        }
    }
}
