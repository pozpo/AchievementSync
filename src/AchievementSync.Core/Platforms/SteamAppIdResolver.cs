using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AchievementSync.Core.Enabler;
using AchievementSync.Core.Metadata;
using AchievementSync.Core.Models;
using AchievementSync.Core.Scanning;

namespace AchievementSync.Core.Platforms;

public enum SteamMappingConfidence
{
    None = 0,
    Low = 1,
    Medium = 2,
    High = 3,
    Exact = 4,
    Override = 5
}

public sealed record SteamAppIdResolution(
    string Platform,
    string SourceGameId,
    string? SourceTitle,
    string? ResolvedSteamAppId,
    SteamMappingConfidence Confidence,
    string MatchReason,
    bool IsOverride,
    bool IsBlacklisted,
    IReadOnlyList<PlatformAchievement> NormalizedAchievements,
    int DiscardedCount)
{
    public string? SourceVersion { get; init; }
}

/// <summary>
/// Resolves non-Steam platform snapshots to Steam AppIDs using per-user overrides,
/// <c>uplay-steam.json</c>, <see cref="SteamDbAppIdLookup"/>, and <see cref="AchievementCatalogMatcher"/>,
/// and normalizes timestamps (FILETIME, milliseconds, microseconds, ISO-8601), earned flags,
/// and Steam schema API names (discarding IDs absent from the Steam schema when one is supplied).
/// </summary>
public sealed class SteamAppIdResolver
{
    private const long MaxValidUnixSeconds = 253_402_300_799L; // 9999-12-31T23:59:59Z
    private const long MinFileTimeTicks = 116_444_736_000_000_000L; // 1970-01-01 in Windows FILETIME
    private const long MaxFileTimeTicks = 2_650_467_743_999_999_999L;
    private static readonly Regex EditionSuffixPattern = new(
        @"\s*(?:[™®©]|[\(\[](?:PC|Win|Windows|Global|EU|US|NA|JP|RU|Asia|Steam|GOG|Epic|EA|Ubisoft|Remastered|Edition|GOTY|(?:Game of the Year|Definitive|Complete|Enhanced|Anniversary|Deluxe|Standard|Special|Collector'?s|Digital Deluxe|Director'?s Cut)(?:\s+Edition)?)[\)\]]|(?::|-)?\s*(?:Game of the Year|Definitive|Complete|Enhanced|Anniversary|Deluxe|Standard|Special|Collector'?s|Digital Deluxe|Director'?s Cut)\s+Edition|(?::|-)\s*(?:Remastered|GOTY))\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly Func<string, CancellationToken, Task<string?>> _titleLookupAsync;
    private readonly Func<string, IReadOnlyList<AchievementRecord>?>? _schemaLoader;
    private readonly Dictionary<string, string?> _titleCache = new(StringComparer.OrdinalIgnoreCase);

    public SteamAppIdResolver(
        Func<string, CancellationToken, Task<string?>>? titleLookupAsync = null,
        Func<string, IReadOnlyList<AchievementRecord>?>? schemaLoader = null)
    {
        _titleLookupAsync = titleLookupAsync ?? SteamDbAppIdLookup.LookupAppIdAsync;
        _schemaLoader = schemaLoader;
    }

    public static string BuildOverrideKey(string platform, string sourceGameId) =>
        $"{platform.Trim().ToLowerInvariant()}:{sourceGameId.Trim()}";

    public async Task<SteamAppIdResolution> ResolveAsync(
        PlatformGameSnapshot snapshot,
        IReadOnlyDictionary<string, string>? userOverrides = null,
        IReadOnlySet<string>? blacklistedKeys = null,
        IReadOnlyList<AchievementRecord>? explicitSteamSchema = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var platform = snapshot.Platform.Trim();
        var gameId = snapshot.GameId.Trim();
        var overrideKey = BuildOverrideKey(platform, gameId);
        var isBlacklisted = blacklistedKeys is not null
            && (blacklistedKeys.Contains(overrideKey) || blacklistedKeys.Contains(gameId));

        string? resolvedAppId = null;
        var confidence = SteamMappingConfidence.None;
        var reason = "No Steam AppID match found";
        var isOverride = false;

        // 1. Per-user explicit overrides take highest priority.
        if (userOverrides is not null)
        {
            if (TryGetValidOverride(userOverrides, overrideKey, out var perPlatformOverride))
            {
                resolvedAppId = perPlatformOverride;
                confidence = SteamMappingConfidence.Override;
                reason = "Manual AppID override";
                isOverride = true;
            }
            else if (TryGetValidOverride(userOverrides, gameId, out var idOverride))
            {
                resolvedAppId = idOverride;
                confidence = SteamMappingConfidence.Override;
                reason = "Manual AppID override";
                isOverride = true;
            }
        }

        // 2. Direct Steam AppID.
        if (resolvedAppId is null
            && platform.Equals("steam", StringComparison.OrdinalIgnoreCase)
            && AppIdValidator.IsValid(gameId))
        {
            resolvedAppId = gameId;
            confidence = SteamMappingConfidence.Exact;
            reason = "Native Steam AppID";
        }

        // 3. Embedded uplay-steam.json mapping table for Ubisoft Connect / LumaPlay / RUNE.
        if (resolvedAppId is null && IsUbisoftPlatform(platform))
        {
            var mapped = UplaySteamMapper.LookupSteamAppId(gameId);
            if (AppIdValidator.IsValid(mapped))
            {
                resolvedAppId = mapped;
                confidence = SteamMappingConfidence.High;
                reason = "uplay-steam.json mapping";
            }
        }

        // 4. Title lookup via SteamDbAppIdLookup (cleaned title fallback).
        if (resolvedAppId is null)
        {
            var candidateTitle = InferLookupTitle(snapshot);
            if (!string.IsNullOrWhiteSpace(candidateTitle))
            {
                var found = await LookupCachedTitleAsync(candidateTitle, cancellationToken).ConfigureAwait(false);
                if (!AppIdValidator.IsValid(found))
                {
                    var cleaned = CleanTitle(candidateTitle);
                    if (!string.IsNullOrWhiteSpace(cleaned)
                        && !string.Equals(cleaned, candidateTitle, StringComparison.OrdinalIgnoreCase))
                    {
                        found = await LookupCachedTitleAsync(cleaned, cancellationToken).ConfigureAwait(false);
                    }
                }

                if (AppIdValidator.IsValid(found))
                {
                    resolvedAppId = found;
                    confidence = SteamMappingConfidence.Medium;
                    reason = "Steam store title lookup";
                }
            }
        }

        var schema = explicitSteamSchema;
        if (schema is null && AppIdValidator.IsValid(resolvedAppId) && _schemaLoader is not null)
        {
            schema = _schemaLoader(resolvedAppId);
        }

        var (normalized, discarded, matchedCount) = NormalizeAchievementsCore(snapshot.Achievements, schema);
        if (!isOverride && AppIdValidator.IsValid(resolvedAppId) && schema is { Count: > 0 })
        {
            if (matchedCount > 0 && confidence == SteamMappingConfidence.Medium)
            {
                confidence = SteamMappingConfidence.High;
                reason = $"Steam store title + schema match ({matchedCount}/{schema.Count})";
            }
            else if (matchedCount == 0 && snapshot.Achievements.Count > 0 && confidence <= SteamMappingConfidence.Medium)
            {
                confidence = SteamMappingConfidence.Low;
                reason = "Title matched, but 0 achievements matched Steam schema";
            }
        }

        if (isBlacklisted && AppIdValidator.IsValid(resolvedAppId) && blacklistedKeys!.Contains(resolvedAppId))
            isBlacklisted = true;
        else if (blacklistedKeys is not null && AppIdValidator.IsValid(resolvedAppId) && blacklistedKeys.Contains(resolvedAppId))
            isBlacklisted = true;

        return new SteamAppIdResolution(
            platform,
            gameId,
            snapshot.Title,
            resolvedAppId,
            confidence,
            reason,
            isOverride,
            isBlacklisted,
            normalized,
            discarded)
        {
            SourceVersion = snapshot.Version
        };
    }

    public static IReadOnlyList<PlatformAchievement> NormalizeAchievements(
        IEnumerable<PlatformAchievement> achievements,
        IReadOnlyList<AchievementRecord>? steamSchema = null) =>
        NormalizeAchievementsCore(achievements, steamSchema).Normalized;

    private static (IReadOnlyList<PlatformAchievement> Normalized, int DiscardedCount, int MatchedCount)
        NormalizeAchievementsCore(
            IEnumerable<PlatformAchievement> achievements,
            IReadOnlyList<AchievementRecord>? steamSchema)
    {
        ArgumentNullException.ThrowIfNull(achievements);

        var hasSchema = steamSchema is { Count: > 0 };
        var schemaByApi = hasSchema
            ? steamSchema!
                .Where(item => !string.IsNullOrWhiteSpace(item.ApiName))
                .GroupBy(item => item.ApiName.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase)
            : null;
        var usedSchemaEntries = hasSchema ? new HashSet<AchievementRecord>() : null;

        var merged = new Dictionary<string, PlatformAchievement>(StringComparer.OrdinalIgnoreCase);
        var discarded = 0;
        var matchedCount = 0;

        foreach (var raw in achievements)
        {
            var normalizedId = NormalizeApiName(raw.Id);
            var normalizedTime = raw.Earned ? NormalizeUnixSeconds(raw.EarnedTimeUnix) : 0L;
            var normalizedEntry = raw with
            {
                Id = normalizedId ?? string.Empty,
                EarnedTimeUnix = normalizedTime
            };

            string canonicalId;
            if (hasSchema)
            {
                AchievementRecord? schemaMatch = null;
                if (!string.IsNullOrWhiteSpace(normalizedId) && schemaByApi!.TryGetValue(normalizedId, out var exact))
                {
                    schemaMatch = exact;
                }
                else
                {
                    schemaMatch = AchievementCatalogMatcher.FindMatch(
                        normalizedId,
                        raw.Name,
                        steamSchema!,
                        usedSchemaEntries);
                }

                if (schemaMatch is null)
                {
                    discarded++;
                    continue;
                }

                usedSchemaEntries!.Add(schemaMatch);
                matchedCount++;
                canonicalId = schemaMatch.ApiName.Trim();
                normalizedEntry = normalizedEntry with
                {
                    Id = canonicalId,
                    Name = string.IsNullOrWhiteSpace(normalizedEntry.Name) ? schemaMatch.DisplayName : normalizedEntry.Name,
                    Description = string.IsNullOrWhiteSpace(normalizedEntry.Description) ? schemaMatch.Description : normalizedEntry.Description
                };
            }
            else
            {
                if (string.IsNullOrWhiteSpace(normalizedId))
                {
                    discarded++;
                    continue;
                }
                canonicalId = normalizedId;
            }

            if (merged.TryGetValue(canonicalId, out var existing))
            {
                var earned = existing.Earned || normalizedEntry.Earned;
                var time = earned
                    ? Math.Max(existing.EarnedTimeUnix, normalizedEntry.EarnedTimeUnix)
                    : 0L;
                merged[canonicalId] = existing with
                {
                    Name = existing.Name ?? normalizedEntry.Name,
                    Description = existing.Description ?? normalizedEntry.Description,
                    Earned = earned,
                    EarnedTimeUnix = time,
                    EarnedKnown = existing.EarnedKnown || normalizedEntry.EarnedKnown,
                    IconPath = existing.IconPath ?? normalizedEntry.IconPath,
                    IconGrayPath = existing.IconGrayPath ?? normalizedEntry.IconGrayPath,
                    TrophyType = existing.TrophyType ?? normalizedEntry.TrophyType,
                    RarityPercent = existing.RarityPercent ?? normalizedEntry.RarityPercent,
                    Hidden = existing.Hidden || normalizedEntry.Hidden
                };
            }
            else
            {
                merged[canonicalId] = normalizedEntry;
            }
        }

        var ordered = merged.Values
            .OrderBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return (ordered, discarded, matchedCount);
    }

    public static string? NormalizeApiName(string? rawId)
    {
        if (string.IsNullOrWhiteSpace(rawId)) return null;
        var trimmed = rawId.Trim().Trim('"', '\'');
        if (string.IsNullOrWhiteSpace(trimmed)) return null;
        if (trimmed.Contains('/') || trimmed.Contains('\\')) return null;
        return trimmed;
    }

    public static long NormalizeUnixSeconds(long value)
    {
        if (value <= 0) return 0;

        // Windows FILETIME (100-ns ticks since 1601-01-01 UTC)
        if (value >= MinFileTimeTicks && value <= MaxFileTimeTicks)
        {
            try
            {
                var utc = DateTime.FromFileTimeUtc(value);
                var unix = new DateTimeOffset(utc).ToUnixTimeSeconds();
                return unix is > 0 and <= MaxValidUnixSeconds ? unix : 0;
            }
            catch (ArgumentOutOfRangeException)
            {
                return 0;
            }
        }

        // Epoch microseconds (e.g. > 10^13)
        if (value > 10_000_000_000_000L)
        {
            var seconds = value / 1_000_000L;
            return seconds is > 0 and <= MaxValidUnixSeconds ? seconds : 0;
        }

        // Epoch milliseconds (e.g. > 10^10)
        if (value > 10_000_000_000L)
        {
            var seconds = value / 1000L;
            return seconds is > 0 and <= MaxValidUnixSeconds ? seconds : 0;
        }

        return value <= MaxValidUnixSeconds ? value : 0;
    }

    public static long NormalizeUnixSeconds(ulong value)
    {
        if (value == 0 || value > long.MaxValue) return 0;
        return NormalizeUnixSeconds(unchecked((long)value));
    }

    public static long NormalizeUnixSeconds(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0;
        var trimmed = raw.Trim().Trim('"', '\'');
        if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
            return NormalizeUnixSeconds(numeric);
        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var floating)
            && floating > 0 && floating < long.MaxValue)
            return NormalizeUnixSeconds((long)floating);

        if (DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            var unix = parsed.ToUnixTimeSeconds();
            return unix is > 0 and <= MaxValidUnixSeconds ? unix : 0;
        }

        return 0;
    }

    public static long NormalizeUnixSeconds(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number when element.TryGetInt64(out var numeric) => NormalizeUnixSeconds(numeric),
        JsonValueKind.Number when element.TryGetUInt64(out var unsigned) => NormalizeUnixSeconds(unsigned),
        JsonValueKind.Number when element.TryGetDouble(out var floating) && floating > 0 && floating < long.MaxValue
            => NormalizeUnixSeconds((long)floating),
        JsonValueKind.String => NormalizeUnixSeconds(element.GetString()),
        _ => 0
    };

    public static bool? NormalizeEarnedFlag(object? value) => value switch
    {
        null => null,
        bool flag => flag,
        byte b => b != 0,
        sbyte sb => sb != 0,
        short s => s != 0,
        ushort us => us != 0,
        int i => i != 0,
        uint ui => ui != 0,
        long l => l != 0,
        ulong ul => ul != 0,
        double d => d != 0,
        string text => NormalizeEarnedFlag(text),
        JsonElement element => NormalizeEarnedFlag(element),
        _ => null
    };

    public static bool? NormalizeEarnedFlag(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim().Trim('"', '\'').ToLowerInvariant();
        return normalized switch
        {
            "1" or "true" or "yes" or "y" or "earned" or "unlocked" or "achieved" => true,
            "0" or "false" or "no" or "n" or "locked" or "notachieved" or "unearned" => false,
            _ when int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var num) => num != 0,
            _ => null
        };
    }

    public static bool? NormalizeEarnedFlag(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number when element.TryGetInt64(out var numeric) => numeric != 0,
        JsonValueKind.String => NormalizeEarnedFlag(element.GetString()),
        _ => null
    };

    private async Task<string?> LookupCachedTitleAsync(string title, CancellationToken cancellationToken)
    {
        if (_titleCache.TryGetValue(title, out var cached)) return cached;
        var result = await _titleLookupAsync(title, cancellationToken).ConfigureAwait(false);
        _titleCache[title] = result;
        return result;
    }

    private static bool TryGetValidOverride(
        IReadOnlyDictionary<string, string> overrides,
        string key,
        out string? appId)
    {
        if (overrides.TryGetValue(key, out var raw) && AppIdValidator.IsValid(raw?.Trim()))
        {
            appId = raw!.Trim();
            return true;
        }
        foreach (var kv in overrides)
        {
            if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase) && AppIdValidator.IsValid(kv.Value?.Trim()))
            {
                appId = kv.Value!.Trim();
                return true;
            }
        }
        appId = null;
        return false;
    }

    private static bool IsUbisoftPlatform(string platform) =>
        platform.Equals("uplay", StringComparison.OrdinalIgnoreCase)
        || platform.Equals("ubisoft", StringComparison.OrdinalIgnoreCase)
        || platform.Equals("uplay-lumaplay", StringComparison.OrdinalIgnoreCase)
        || platform.Equals("uplay-rune", StringComparison.OrdinalIgnoreCase);

    private static string? InferLookupTitle(PlatformGameSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.Title)) return snapshot.Title.Trim();
        var id = snapshot.GameId.Trim();
        if (string.IsNullOrWhiteSpace(id)) return null;
        // Skip opaque IDs (pure digits, 32-char hex namespaces, NPWR/CUSA/PPSA/8-char hex title IDs)
        if (id.All(char.IsAsciiDigit)) return null;
        if (id.Length == 32 && id.All(Uri.IsHexDigit)) return null;
        if (id.Length == 8 && id.All(Uri.IsHexDigit)) return null;
        if (Regex.IsMatch(id, @"^(?:NPWR|CUSA|PPSA|BLUS|BLES|NPUB|NPEB)\d+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return null;
        return id.Replace('_', ' ').Replace('-', ' ').Trim();
    }

    private static string CleanTitle(string title)
    {
        var cleaned = EditionSuffixPattern.Replace(title, " ");
        return Regex.Replace(cleaned, @"\s+", " ").Trim();
    }
}
