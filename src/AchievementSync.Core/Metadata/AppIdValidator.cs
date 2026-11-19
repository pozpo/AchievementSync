using System.Diagnostics.CodeAnalysis;

namespace AchievementSync.Core.Metadata;

/// <summary>Shared validation for numeric Steam AppIDs used by scan and metadata services.</summary>
public static class AppIdValidator
{
    public static bool IsValid([NotNullWhen(true)] string? appId) =>
        !string.IsNullOrWhiteSpace(appId)
        && appId.Length is >= 1 and <= 10
        && appId.All(char.IsAsciiDigit)
        && appId.Any(character => character != '0');
}
