namespace AchievementSync.Core.Scanning;

public sealed record ScanLocation(
    string Id,
    string Name,
    string Path,
    bool IsEnabled,
    bool IsCustom,
    bool IsAvailable,
    int FilesFound = 0,
    string? Error = null);
