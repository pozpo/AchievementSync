using System.Text.Json;
using AchievementSync.Core.Models;

namespace AchievementSync.Core.Tracking;

public sealed record AchievementUnlockNotification(
    string AppId,
    string GameTitle,
    string AchievementId,
    string AchievementName,
    string? IconUri,
    long? UnlockTimeUnix,
    string? Description = null,
    string? TrophyType = null,
    int? ProgressCurrent = null,
    int? ProgressMax = null);

/// <summary>Persists observed states and reports only previously confirmed locked-to-unlocked transitions.</summary>
public sealed class AchievementUnlockTracker
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<string, bool?>> _states = new(StringComparer.Ordinal);
    private readonly HashSet<string> _baselinedAppIds = new(StringComparer.Ordinal);
    private readonly string _statePath;
    private bool _writesAllowed = true;

    public AchievementUnlockTracker(string? statePath = null)
    {
        _statePath = statePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AchievementSync", "unlock-state.json");
        Load();
    }

    public string? StorageIssue { get; private set; }

    public IReadOnlyList<AchievementUnlockNotification> Observe(GameProfile profile, string? gameTitle = null)
        => ObserveMany([(profile, gameTitle)]);

    public IReadOnlyList<AchievementUnlockNotification> ObserveMany(IEnumerable<(GameProfile Profile, string? GameTitle)> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        lock (_gate)
        {
            var notifications = new List<AchievementUnlockNotification>();
            var changed = false;
            foreach (var (profile, gameTitle) in profiles)
            {
                ArgumentNullException.ThrowIfNull(profile);
                if (!IsAppId(profile.AppId)) continue;

                var hadBaseline = _baselinedAppIds.Contains(profile.AppId);
                if (!_states.TryGetValue(profile.AppId, out var prior))
                    _states[profile.AppId] = prior = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);

                foreach (var achievement in profile.Achievements)
                {
                    if (string.IsNullOrWhiteSpace(achievement.ApiName)) continue;
                    var hadPrior = prior.TryGetValue(achievement.ApiName, out var oldState);
                    if (hadBaseline && hadPrior && oldState == false && achievement.IsUnlocked == true)
                    {
                        notifications.Add(new AchievementUnlockNotification(
                            profile.AppId,
                            string.IsNullOrWhiteSpace(gameTitle) ? profile.Title ?? $"AppID {profile.AppId}" : gameTitle,
                            achievement.ApiName,
                            achievement.DisplayName ?? achievement.ApiName,
                            achievement.IconUri,
                            achievement.UnlockTimeUnix,
                            achievement.Description,
                            achievement.TrophyType));
                    }

                    // Unknown is recorded explicitly so unknown-to-unlocked never looks like a locked transition.
                    if (!hadPrior || oldState != achievement.IsUnlocked) changed = true;
                    prior[achievement.ApiName] = achievement.IsUnlocked;
                }

                if (_baselinedAppIds.Add(profile.AppId)) changed = true;
            }

            if (changed) Save();

            // Never present an event that cannot be durably deduplicated across restarts.
            return StorageIssue is null ? notifications : [];
        }
    }

    private void Load()
    {
        if (!File.Exists(_statePath)) return;
        try
        {
            var state = JsonSerializer.Deserialize<TrackerState>(File.ReadAllText(_statePath), JsonOptions);
            if (state is null || state.Version != 1 || state.States is null || state.BaselinedAppIds is null)
                throw new JsonException("Unlock state has an unsupported shape.");

            foreach (var (appId, entries) in state.States)
            {
                if (!IsAppId(appId) || entries is null) continue;
                _states[appId] = new Dictionary<string, bool?>(entries, StringComparer.OrdinalIgnoreCase);
            }
            foreach (var appId in state.BaselinedAppIds.Where(IsAppId)) _baselinedAppIds.Add(appId);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _writesAllowed = false;
            StorageIssue = exception.GetType().Name;
        }
    }

    private void Save()
    {
        if (!_writesAllowed) return;
        var temporaryPath = _statePath + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            var state = new TrackerState(1, _states, _baselinedAppIds.Order(StringComparer.Ordinal).ToArray());
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state, JsonOptions));
            File.Move(temporaryPath, _statePath, overwrite: true);
            StorageIssue = null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            StorageIssue = exception.GetType().Name;
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (IOException) { }
        }
    }

    private static bool IsAppId(string appId) => appId.Length is >= 1 and <= 10 && appId.All(char.IsAsciiDigit);

    private sealed record TrackerState(int Version, Dictionary<string, Dictionary<string, bool?>> States, string[] BaselinedAppIds);
}
