namespace AchievementSync.Core.Scanning;

/// <summary>A supported scan candidate with a reliable AppID and its observed parse status.</summary>
public sealed record DiscoveredAchievementFile(
    string AppId,
    string Path,
    string FileName,
    string Extension,
    DateTime LastWriteTimeUtc,
    bool ParsedSuccessfully,
    string LocationId = "");
