using AchievementSync.Core.Models;

namespace AchievementSync.Core.Scanning;

public sealed record ScanResult(
    DateTimeOffset CompletedAt,
    IReadOnlyList<GameProfile> Games,
    IReadOnlyList<ScanLocation> Locations,
    IReadOnlyList<string> Issues,
    int CandidateFiles);
