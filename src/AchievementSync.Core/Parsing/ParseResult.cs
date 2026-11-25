using AchievementSync.Core.Models;

namespace AchievementSync.Core.Parsing;

public sealed record ParseResult(ParsedProfile? Profile, string? Error)
{
    public bool Success => Profile is not null;

    public static ParseResult Parsed(ParsedProfile profile) => new(profile, null);
    public static ParseResult Unsupported(string reason) => new(null, reason);
}
