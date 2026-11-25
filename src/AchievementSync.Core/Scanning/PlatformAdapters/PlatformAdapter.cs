using System.Text.Json.Serialization;
using AchievementSync.Core.Platforms;

namespace AchievementSync.Core.Scanning.PlatformAdapters;

/// <summary>Outcome of attempting to resolve platform achievement data for one AppID.</summary>
public sealed record PlatformResolveResult(
    string AppId,
    string Platform,
    bool Succeeded,
    IReadOnlyDictionary<string, PlatformAchievementState>? States = null,
    string? Error = null,
    bool IsConfigured = true);

/// <summary>A single achievement state normalized to the viewer contract.</summary>
public sealed record PlatformAchievementState(
    [property: JsonPropertyName("earned")] bool Earned,
    [property: JsonPropertyName("earned_time")] long EarnedTime);

/// <summary>Capability and configuration status reported by a platform adapter.</summary>
public sealed record PlatformAdapterInfo(
    string Platform,
    string DisplayName,
    bool IsConfigured,
    string? ConfigurationHint = null);

/// <summary>Abstracts a platform-specific achievement source. Each adapter resolves achievement
/// states for one AppID and the caller writes them to Metadata/&lt;AppID&gt;/achievements.json.</summary>
public abstract class PlatformAdapter
{
    protected PlatformAdapter(string platform, string displayName) { Platform = platform; DisplayName = displayName; }

    public string Platform { get; }
    public string DisplayName { get; }

    /// <summary>Whether this adapter has the credentials/API access it needs to run.</summary>
    public virtual bool IsConfigured => true;

    /// <summary>Whether this adapter can run with the supplied folders and credentials.</summary>
    public virtual bool IsConfiguredFor(PlatformSourceOptions options) => IsConfigured;

    /// <summary>Human-readable hint shown when the adapter is not configured.</summary>
    public virtual string? ConfigurationHint => null;

    /// <summary>Resolves achievement states for the given AppID, or returns an error/not-configured result.</summary>
    public abstract Task<PlatformResolveResult> ResolveAsync(string appId, CancellationToken cancellationToken = default);
}
