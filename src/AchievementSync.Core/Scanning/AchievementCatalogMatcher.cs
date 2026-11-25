using System.Text.RegularExpressions;
using AchievementSync.Core.Models;

namespace AchievementSync.Core.Scanning;

/// <summary>Matches progress identifiers with catalog names/descriptions when sources use different IDs.</summary>
public static class AchievementCatalogMatcher
{
    private static readonly HashSet<string> IgnoredWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "are", "at", "be", "by", "do", "for", "from", "get", "in", "into", "of", "on",
        "the", "to", "use", "while", "perform", "earn", "unlock", "obtain", "collect", "reach", "win", "beat", "finish", "row"
    };

    private static readonly Dictionary<string, string> RomanNumerals = new(StringComparer.OrdinalIgnoreCase)
    {
        ["I"] = "number1", ["II"] = "number2", ["III"] = "number3", ["IV"] = "number4", ["V"] = "number5",
        ["VI"] = "number6", ["VII"] = "number7", ["VIII"] = "number8", ["IX"] = "number9", ["X"] = "number10"
    };

    /// <summary>Returns a unique, high-confidence catalog match; ambiguous names are left unmatched.</summary>
    public static AchievementRecord? FindMatch(
        string? apiName,
        string? displayName,
        IEnumerable<AchievementRecord> candidates,
        ISet<AchievementRecord>? excluded = null)
    {
        var identifierWords = GetWords(apiName, identifier: true);
        if (identifierWords.Count == 0) identifierWords = GetWords(displayName, identifier: false);
        if (identifierWords.Count == 0) return null;

        var scored = candidates
            .Where(candidate => excluded is null || !excluded.Contains(candidate))
            .Select(candidate => (Candidate: candidate, Score: Score(identifierWords, candidate)))
            .Where(item => item.Score >= 0.45)
            .OrderByDescending(item => item.Score)
            .ToArray();
        if (scored.Length == 0 || (scored.Length > 1 && scored[0].Score - scored[1].Score < 0.075)) return null;
        return scored[0].Candidate;
    }

    private static double Score(HashSet<string> identifierWords, AchievementRecord candidate)
    {
        var catalogWords = GetWords(candidate.DisplayName, identifier: false);
        catalogWords.UnionWith(GetWords(candidate.Description, identifier: false));
        if (catalogWords.Count == 0) return 0;
        var shared = identifierWords.Intersect(catalogWords, StringComparer.OrdinalIgnoreCase).Count();
        if (shared == 0) return 0;
        var extra = Math.Max(0, catalogWords.Count - shared);
        return shared / (identifierWords.Count + (extra * 0.6));
    }

    private static HashSet<string> GetWords(string? value, bool identifier)
    {
        if (string.IsNullOrWhiteSpace(value)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var text = value;
        if (identifier)
        {
            text = Regex.Replace(text, "^(ACHIEVEMENT|ACH)[_-]+", string.Empty,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            text = text.Replace("_", " ").Replace("-", " ");
        }

        return Regex.Matches(text, "[\\p{L}\\p{N}]+", RegexOptions.CultureInvariant)
            .Select(match => NormalizeWord(match.Value))
            .Where(word => word.Length > 0 && !IgnoredWords.Contains(word))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizeWord(string word)
    {
        if (RomanNumerals.TryGetValue(word, out var number)) return number;
        var normalized = word.ToLowerInvariant();
        if (normalized == "no") return "without";
        if (normalized.Length > 4 && normalized.EndsWith("ies", StringComparison.Ordinal)) return normalized[..^3] + "y";
        if (normalized.Length > 4 && normalized.EndsWith("s", StringComparison.Ordinal)) return normalized[..^1];
        return normalized;
    }
}
