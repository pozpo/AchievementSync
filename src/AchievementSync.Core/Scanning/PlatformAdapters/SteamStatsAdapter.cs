using AchievementSync.Core.Platforms;

namespace AchievementSync.Core.Scanning.PlatformAdapters;

/// <summary>Compatibility adapter for the local metadata fallback. The Steam reader uses the
/// schema-aware appcache/KV parsers so unrelated binary strings are never treated as unlocks.</summary>
public sealed class SteamStatsAdapter : PlatformAdapter
{
    private readonly SteamDataAdapter _reader;

    public SteamStatsAdapter(params string[] steamRoots)
        : base("steam", "Steam")
    {
        var options = PlatformSourceOptions.CreateDefault();
        if (steamRoots.Length > 0) options.SteamRoot = steamRoots[0];
        _reader = new SteamDataAdapter(options);
    }

    public override bool IsConfigured => _reader.IsConfigured;

    public override string? ConfigurationHint => _reader.ConfigurationHint;

    public override Task<PlatformResolveResult> ResolveAsync(string appId, CancellationToken cancellationToken = default) =>
        _reader.ResolveAsync(appId, cancellationToken);
}
