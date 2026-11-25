using System.Collections.Concurrent;

namespace AchievementSync.Core.Scanning.PlatformAdapters;

/// <summary>Registry of platform adapters keyed by platform id.</summary>
public sealed class PlatformAdapterRegistry
{
    private readonly ConcurrentDictionary<string, PlatformAdapter> _adapters = new(StringComparer.OrdinalIgnoreCase);

    public void Register(PlatformAdapter adapter)
    {
        if (adapter is null) throw new ArgumentNullException(nameof(adapter));
        _adapters[adapter.Platform] = adapter;
    }

    public bool TryGet(string platform, out PlatformAdapter? adapter) =>
        _adapters.TryGetValue(platform, out adapter);

    public IReadOnlyList<PlatformAdapterInfo> GetAllInfo() =>
        _adapters.Values.Select(adapter => new PlatformAdapterInfo(
            adapter.Platform,
            adapter.DisplayName,
            adapter.IsConfigured,
            adapter.ConfigurationHint)).ToList();

    public IReadOnlyList<string> ConfiguredPlatforms() =>
        _adapters.Values.Where(adapter => adapter.IsConfigured).Select(adapter => adapter.Platform).ToList();
}
