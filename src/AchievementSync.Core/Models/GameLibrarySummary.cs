namespace AchievementSync.Core.Models;

public sealed record GameLibrarySummary(
    int GameCount,
    int TotalUnlocked,
    int TotalKnownAchievements,
    int? TotalCatalogAchievements,
    double? CompletionPercent,
    int CataloguedGameCount,
    int MeasurableGameCount,
    int CompletedGameCount,
    int GamesMissingCatalog,
    int GamesWithUnknownPlayerState)
{
    public static GameLibrarySummary Calculate(IEnumerable<GameProfile> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var games = source.ToArray();
        var knownStates = games.Sum(game => game.KnownStateCount);
        var catalogued = games.Where(game => game.TotalCount.HasValue).ToArray();
        var catalogTotal = catalogued.Length > 0 ? catalogued.Sum(game => game.TotalCount!.Value) : (int?)null;
        var measurable = games.Where(game => game.CompletionPercent.HasValue).ToArray();
        var total = measurable.Sum(game => game.TotalCount!.Value);
        var unlocked = measurable.Sum(game => game.UnlockedCount);
        var completion = measurable.Length > 0 && total > 0
            ? Math.Round(unlocked * 100d / total, 1)
            : (double?)null;

        return new GameLibrarySummary(
            games.Length,
            games.Sum(game => game.UnlockedCount),
            knownStates,
            catalogTotal,
            completion,
            catalogued.Length,
            measurable.Length,
            games.Count(game => game.CompletionPercent == 100),
            games.Count(game => !game.TotalCount.HasValue),
            games.Count(game => game.TotalCount.HasValue && game.Achievements.Any(achievement => !achievement.IsUnlocked.HasValue)));
    }
}
