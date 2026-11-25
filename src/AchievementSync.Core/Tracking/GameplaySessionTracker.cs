using System.Text.Json;
using AchievementSync.Core.Metadata;

namespace AchievementSync.Core.Tracking;

public sealed record GameExecutableAssociation(string AppId, string ExecutablePath);

public sealed record RunningProcessObservation(
    int ProcessId,
    string ExecutablePath,
    DateTimeOffset StartedAtUtc,
    string? ProcessName = null);

public sealed record GamePlaytimeSnapshot(
    string AppId,
    TimeSpan TotalPlaytime,
    DateTimeOffset? LastPlayedUtc,
    DateTimeOffset? CurrentSessionStartedAtUtc,
    IReadOnlyList<string> ExecutablePaths,
    int ActiveProcessCount)
{
    public bool IsRunning => ActiveProcessCount > 0;

    public TimeSpan CurrentSessionDuration(DateTimeOffset now) =>
        CurrentSessionStartedAtUtc is { } started && now > started ? now - started : TimeSpan.Zero;
}

/// <summary>
/// Tracks the union of active process intervals for each discovered AppID. Multiple
/// explicitly-associated processes for one game count as one continuous session.
/// </summary>
public sealed class GameplaySessionTracker
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly object _gate = new();
    private readonly string _statePath;
    private readonly PersistentPlaytimeState _state;
    private HashSet<string> _libraryAppIds = new(StringComparer.Ordinal);
    private bool _storageWritable = true;

    public GameplaySessionTracker(string? statePath = null)
    {
        _statePath = statePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AchievementSync", "playtime.json");

        if (!File.Exists(_statePath))
        {
            _state = new PersistentPlaytimeState();
            return;
        }

        try
        {
            _state = JsonSerializer.Deserialize<PersistentPlaytimeState>(File.ReadAllText(_statePath), JsonOptions)
                ?? throw new JsonException("Playtime data was empty.");
            if (_state.SchemaVersion != 1)
                throw new JsonException("Playtime data has an unsupported schema version.");
            _state.Games = new Dictionary<string, PersistedGame>(_state.Games, StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // Keep malformed or unreadable user data intact. This instance may track
            // in memory, but will not replace a file it could not safely load.
            _state = new PersistentPlaytimeState();
            _storageWritable = false;
            StorageIssue = exception.GetType().Name;
        }
    }

    public string? StorageIssue { get; }

    public bool StorageAvailable
    {
        get { lock (_gate) return _storageWritable; }
    }

    public bool HasActiveSessions
    {
        get { lock (_gate) return _state.Games.Values.Any(game => game.Processes.Count > 0); }
    }

    public string StatePath => _statePath;

    public void SetLibraryGames(IEnumerable<string> appIds, DateTimeOffset? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(appIds);
        lock (_gate)
        {
            var next = appIds.Where(AppIdValidator.IsValid).ToHashSet(StringComparer.Ordinal);
            var stoppedAt = nowUtc ?? DateTimeOffset.UtcNow;
            var changed = false;
            foreach (var removedAppId in _libraryAppIds.Except(next, StringComparer.Ordinal))
            {
                if (!_state.Games.TryGetValue(removedAppId, out var game) || game.Processes.Count == 0) continue;
                CloseSessionLocked(game, stoppedAt);
                changed = true;
            }
            _libraryAppIds = next;
            if (changed) SaveLocked();
        }
    }

    public IReadOnlyList<GameExecutableAssociation> GetAssociations(string appId)
    {
        lock (_gate)
        {
            return TryGetGame(appId, out var game)
                ? game.ExecutablePaths.Select(path => new GameExecutableAssociation(appId, path)).ToArray()
                : [];
        }
    }

    public IReadOnlyList<string> GetTrackedExecutableNames()
    {
        lock (_gate)
        {
            return _state.Games
                .Where(pair => _libraryAppIds.Contains(pair.Key))
                .SelectMany(pair => pair.Value.ExecutablePaths)
                .Select(Path.GetFileNameWithoutExtension)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    public bool AddExecutable(string appId, string executablePath)
    {
        if (!AppIdValidator.IsValid(appId) || !TryNormalizePath(executablePath, out var normalizedPath)) return false;

        lock (_gate)
        {
            foreach (var (otherAppId, otherGame) in _state.Games)
            {
                if (!string.Equals(otherAppId, appId, StringComparison.Ordinal)
                    && otherGame.ExecutablePaths.Contains(normalizedPath, StringComparer.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            var game = GetOrCreateGame(appId);
            if (game.ExecutablePaths.Contains(normalizedPath, StringComparer.OrdinalIgnoreCase)) return false;
            game.ExecutablePaths.Add(normalizedPath);
            SaveLocked();
            return true;
        }
    }

    public bool RemoveExecutable(string appId, string executablePath)
    {
        if (!TryNormalizePath(executablePath, out var normalizedPath)) return false;
        lock (_gate)
        {
            if (!TryGetGame(appId, out var game)) return false;
            var removed = game.ExecutablePaths.RemoveAll(path =>
                string.Equals(path, normalizedPath, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed) SaveLocked();
            return removed;
        }
    }

    public bool IsAssociatedWithLibraryGame(string executablePath)
    {
        if (!TryNormalizePath(executablePath, out var normalizedPath)) return false;
        lock (_gate)
        {
            return _state.Games.Any(pair => _libraryAppIds.Contains(pair.Key)
                && pair.Value.ExecutablePaths.Contains(normalizedPath, StringComparer.OrdinalIgnoreCase));
        }
    }

    public bool ObserveProcessStarted(RunningProcessObservation process, DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (process.ProcessId <= 0) return false;
        string normalizedPath;
        bool unverifiable = ProcessObservationIdentity.TryReadUnverifiableName(process.ExecutablePath, out var processName);
        if (unverifiable)
        {
            normalizedPath = process.ExecutablePath;
        }
        else if (!TryNormalizePath(process.ExecutablePath, out normalizedPath)) return false;

        lock (_gate)
        {
            var appId = unverifiable
                ? FindLibraryAppIdForProcessName(processName)
                : FindLibraryAppIdForPath(normalizedPath);
            if (appId is null) return false;

            // A PID can be reused. Remove an older identity before registering the new one.
            var existing = FindProcess(process.ProcessId);
            if (existing is { } old && (!string.Equals(old.AppId, appId, StringComparison.Ordinal)
                || !SameProcessIdentity(old.Process, normalizedPath, process.StartedAtUtc)))
            {
                RemoveProcessLocked(process.ProcessId, observedAtUtc);
            }

            var game = GetOrCreateGame(appId);
            if (game.Processes.TryGetValue(process.ProcessId, out var current)
                && SameProcessIdentity(current, normalizedPath, process.StartedAtUtc))
            {
                return false;
            }

            var startedAt = process.StartedAtUtc <= observedAtUtc ? process.StartedAtUtc : observedAtUtc;
            if (game.Processes.Count == 0)
            {
                game.SessionStartedAtUtc = startedAt;
                game.LastAccruedAtUtc = startedAt;
            }
            else if (game.SessionStartedAtUtc is null || startedAt < game.SessionStartedAtUtc)
            {
                game.SessionStartedAtUtc = startedAt;
            }

            game.Processes[process.ProcessId] = new PersistedProcess
            {
                ExecutablePath = normalizedPath,
                StartedAtUtc = process.StartedAtUtc
            };
            SaveLocked();
            return true;
        }
    }

    public bool ObserveProcessStopped(int processId, DateTimeOffset stoppedAtUtc)
    {
        if (processId <= 0) return false;
        lock (_gate)
        {
            return RemoveProcessLocked(processId, stoppedAtUtc);
        }
    }

    /// <summary>
    /// Reconciles persisted processes on startup. A saved session is resumed only
    /// when the same PID, executable path, and process creation time are still live.
    /// A vanished process is finalized at its last durable checkpoint, avoiding an
    /// invented duration during application downtime.
    /// </summary>
    public void RecoverAfterRestart(IEnumerable<RunningProcessObservation> runningProcesses, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(runningProcesses);
        lock (_gate)
        {
            var observations = NormalizeObservations(runningProcesses);
            var matchedIds = new HashSet<int>();

            foreach (var (appId, game) in _state.Games)
            {
                if (game.Processes.Count == 0) continue;
                var matched = game.Processes.Where(pair => observations.TryGetValue(pair.Key, out var observation)
                        && string.Equals(pair.Value.ExecutablePath, observation.ExecutablePath, StringComparison.OrdinalIgnoreCase)
                        && SameStartTime(pair.Value.StartedAtUtc, observation.StartedAtUtc))
                    .ToDictionary(pair => pair.Key, pair => pair.Value);
                game.Processes = matched;
                foreach (var id in matched.Keys) matchedIds.Add(id);

                if (matched.Count == 0)
                {
                    game.LastPlayedUtc = game.LastAccruedAtUtc ?? game.SessionStartedAtUtc;
                    game.SessionStartedAtUtc = null;
                    game.LastAccruedAtUtc = null;
                }
            }

            foreach (var (processId, observation) in observations)
            {
                if (matchedIds.Contains(processId)) continue;
                ObserveProcessStartedLocked(observation, nowUtc);
            }

            // Verified live processes remained active while the application was down.
            CheckpointLocked(nowUtc);
        }
    }

    /// <summary>Reconciles live sessions after a missed WMI event and checkpoints totals.</summary>
    public bool ReconcileRunningProcesses(IEnumerable<RunningProcessObservation> runningProcesses, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(runningProcesses);
        lock (_gate)
        {
            var observations = NormalizeObservations(runningProcesses);
            var changed = false;
            foreach (var game in _state.Games.Values)
            {
                if (game.Processes.Count == 0) continue;
                game.Processes = game.Processes.Where(pair => observations.TryGetValue(pair.Key, out var observation)
                        && string.Equals(pair.Value.ExecutablePath, observation.ExecutablePath, StringComparison.OrdinalIgnoreCase)
                        && SameStartTime(pair.Value.StartedAtUtc, observation.StartedAtUtc))
                    .ToDictionary(pair => pair.Key, pair => pair.Value);

                if (game.Processes.Count == 0)
                {
                    CloseSessionLocked(game, nowUtc);
                    changed = true;
                }
            }

            foreach (var observation in observations.Values)
            {
                changed |= ObserveProcessStartedLocked(observation, nowUtc);
            }

            CheckpointLocked(nowUtc);
            return changed;
        }
    }

    public void Checkpoint(DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            CheckpointLocked(nowUtc);
        }
    }

    public void StopAllActiveSessions(DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            foreach (var game in _state.Games.Values)
            {
                if (game.Processes.Count > 0) CloseSessionLocked(game, nowUtc);
            }
            SaveLocked();
        }
    }

    public IReadOnlyList<GamePlaytimeSnapshot> GetSnapshots(DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            return _libraryAppIds.Select(appId =>
            {
                if (!TryGetGame(appId, out var game)) game = new PersistedGame();
                var totalTicks = game.TotalTicks;
                if (game.Processes.Count > 0 && game.LastAccruedAtUtc is { } accrued && nowUtc > accrued)
                    totalTicks = checked(totalTicks + (nowUtc - accrued).Ticks);

                return new GamePlaytimeSnapshot(
                    appId,
                    TimeSpan.FromTicks(Math.Max(0, totalTicks)),
                    game.LastPlayedUtc,
                    game.SessionStartedAtUtc,
                    game.ExecutablePaths.ToArray(),
                    game.Processes.Count);
            }).ToArray();
        }
    }

    private bool ObserveProcessStartedLocked(RunningProcessObservation process, DateTimeOffset observedAtUtc)
    {
        if (process.ProcessId <= 0) return false;
        bool unverifiable = ProcessObservationIdentity.TryReadUnverifiableName(process.ExecutablePath, out var processName);
        string normalizedPath;
        if (unverifiable)
        {
            normalizedPath = process.ExecutablePath;
        }
        else if (!TryNormalizePath(process.ExecutablePath, out normalizedPath)) return false;

        var appId = unverifiable
            ? FindLibraryAppIdForProcessName(processName)
            : FindLibraryAppIdForPath(normalizedPath);
        if (appId is null) return false;
        var game = GetOrCreateGame(appId);
        if (game.Processes.TryGetValue(process.ProcessId, out var existing)
            && SameProcessIdentity(existing, normalizedPath, process.StartedAtUtc)) return false;

        var startedAt = process.StartedAtUtc <= observedAtUtc ? process.StartedAtUtc : observedAtUtc;
        if (game.Processes.Count == 0)
        {
            game.SessionStartedAtUtc = startedAt;
            game.LastAccruedAtUtc = startedAt;
        }
        else if (game.SessionStartedAtUtc is null || startedAt < game.SessionStartedAtUtc)
        {
            game.SessionStartedAtUtc = startedAt;
        }

        game.Processes[process.ProcessId] = new PersistedProcess
        {
            ExecutablePath = normalizedPath,
            StartedAtUtc = process.StartedAtUtc
        };
        return true;
    }

    private bool RemoveProcessLocked(int processId, DateTimeOffset stoppedAtUtc)
    {
        var match = FindProcess(processId);
        if (match is null) return false;
        var game = _state.Games[match.Value.AppId];
        game.Processes.Remove(processId);
        if (game.Processes.Count == 0) CloseSessionLocked(game, stoppedAtUtc);
        SaveLocked();
        return true;
    }

    private static void CloseSessionLocked(PersistedGame game, DateTimeOffset endedAtUtc)
    {
        AccrueLocked(game, endedAtUtc);
        game.LastPlayedUtc = endedAtUtc;
        game.SessionStartedAtUtc = null;
        game.LastAccruedAtUtc = null;
        game.Processes.Clear();
    }

    private void CheckpointLocked(DateTimeOffset nowUtc)
    {
        foreach (var game in _state.Games.Values)
        {
            if (game.Processes.Count == 0) continue;
            AccrueLocked(game, nowUtc);
            game.LastAccruedAtUtc = nowUtc;
        }
        SaveLocked();
    }

    private static void AccrueLocked(PersistedGame game, DateTimeOffset throughUtc)
    {
        if (game.LastAccruedAtUtc is not { } lastAccrued || throughUtc <= lastAccrued) return;
        game.TotalTicks = checked(game.TotalTicks + (throughUtc - lastAccrued).Ticks);
    }

    private string? FindLibraryAppIdForPath(string executablePath) => _state.Games
        .Where(pair => _libraryAppIds.Contains(pair.Key))
        .FirstOrDefault(pair => pair.Value.ExecutablePaths.Contains(executablePath, StringComparer.OrdinalIgnoreCase))
        .Key;

    private string? FindLibraryAppIdForProcessName(string processName) => _state.Games
        .Where(pair => _libraryAppIds.Contains(pair.Key))
        .FirstOrDefault(pair => pair.Value.ExecutablePaths.Any(path =>
            string.Equals(Path.GetFileNameWithoutExtension(path), processName, StringComparison.OrdinalIgnoreCase)))
        .Key;

    private (string AppId, PersistedProcess Process)? FindProcess(int processId)
    {
        foreach (var (appId, game) in _state.Games)
        {
            if (game.Processes.TryGetValue(processId, out var process)) return (appId, process);
        }
        return null;
    }

    private bool TryGetGame(string appId, out PersistedGame game) =>
        _state.Games.TryGetValue(appId, out game!) && AppIdValidator.IsValid(appId);

    private PersistedGame GetOrCreateGame(string appId)
    {
        if (!_state.Games.TryGetValue(appId, out var game))
        {
            game = new PersistedGame();
            _state.Games.Add(appId, game);
        }
        return game;
    }

    private void SaveLocked()
    {
        if (!_storageWritable) return;
        var directory = Path.GetDirectoryName(_statePath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = _statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_state, JsonOptions));
            File.Move(temporaryPath, _statePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static Dictionary<int, RunningProcessObservation> NormalizeObservations(
        IEnumerable<RunningProcessObservation> processes)
    {
        var result = new Dictionary<int, RunningProcessObservation>();
        foreach (var process in processes)
        {
            if (process.ProcessId <= 0) continue;
            if (ProcessObservationIdentity.TryReadUnverifiableName(process.ExecutablePath, out _))
            {
                result[process.ProcessId] = process;
                continue;
            }

            if (!TryNormalizePath(process.ExecutablePath, out var normalizedPath)) continue;
            result[process.ProcessId] = process with { ExecutablePath = normalizedPath };
        }
        return result;
    }

    private static bool TryNormalizePath(string? path, out string normalizedPath)
    {
        normalizedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            normalizedPath = Path.GetFullPath(path.Trim());
            return Path.IsPathFullyQualified(normalizedPath);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool SameProcessIdentity(PersistedProcess process, string executablePath, DateTimeOffset startedAtUtc) =>
        string.Equals(process.ExecutablePath, executablePath, StringComparison.OrdinalIgnoreCase)
        && SameStartTime(process.StartedAtUtc, startedAtUtc);

    private static bool SameStartTime(DateTimeOffset left, DateTimeOffset right) =>
        (left - right).Duration() <= TimeSpan.FromSeconds(2);

    private sealed class PersistentPlaytimeState
    {
        public int SchemaVersion { get; set; } = 1;
        public Dictionary<string, PersistedGame> Games { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class PersistedGame
    {
        public long TotalTicks { get; set; }
        public DateTimeOffset? LastPlayedUtc { get; set; }
        public DateTimeOffset? SessionStartedAtUtc { get; set; }
        public DateTimeOffset? LastAccruedAtUtc { get; set; }
        public List<string> ExecutablePaths { get; set; } = [];
        public Dictionary<int, PersistedProcess> Processes { get; set; } = [];
    }

    private sealed class PersistedProcess
    {
        public string ExecutablePath { get; set; } = string.Empty;
        public DateTimeOffset StartedAtUtc { get; set; }
    }
}

public static class PlaytimeFormatter
{
    public static string Format(TimeSpan value)
    {
        var minutes = Math.Max(0, (long)Math.Floor(value.TotalMinutes));
        if (minutes < 60) return $"{minutes}m";
        var hours = minutes / 60;
        var remainingMinutes = minutes % 60;
        if (hours < 24) return remainingMinutes == 0 ? $"{hours}h" : $"{hours}h {remainingMinutes}m";
        var days = hours / 24;
        var remainingHours = hours % 24;
        return remainingHours == 0 ? $"{days}d" : $"{days}d {remainingHours}h";
    }
}
