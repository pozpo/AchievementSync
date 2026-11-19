using AchievementSync.Core.Scanning;
using System.Collections.Concurrent;
using System.IO;

namespace AchievementSync.Desktop.Services;

/// <summary>Watches only source files found by the last library scan; it never enumerates folders.</summary>
public sealed class LocalAchievementFileMonitor : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DiscoveredAchievementFile> _sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pending = new(StringComparer.OrdinalIgnoreCase);
    private bool _paused;
    private bool _disposed;

    public event Action<IReadOnlyList<DiscoveredAchievementFile>>? SourcesChanged;

    public void Configure(IEnumerable<DiscoveredAchievementFile> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            StopWatchersLocked();
            foreach (var cancellation in _pending.Values) cancellation.Cancel();
            _pending.Clear();
            _sources.Clear();
            foreach (var source in sources)
            {
                try { _sources[Path.GetFullPath(source.Path)] = source with { Path = Path.GetFullPath(source.Path) }; }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
                {
                    AppLog.Warning($"A discovered achievement file could not be watched: {exception.GetType().Name}.");
                }
            }

            if (!_paused) StartWatchersLocked();
        }
    }

    public void SetPaused(bool paused)
    {
        lock (_gate)
        {
            if (_disposed || _paused == paused) return;
            _paused = paused;
            if (paused)
            {
                StopWatchersLocked();
                foreach (var cancellation in _pending.Values) cancellation.Cancel();
                _pending.Clear();
            }
            else
            {
                StartWatchersLocked();
                ReconcileKnownFilesLocked();
            }
        }
    }

    /// <summary>Requeues the already-discovered file set, used after watcher overflow and resume.</summary>
    public void ReconcileKnownFiles()
    {
        lock (_gate)
        {
            if (_disposed || _paused) return;
            ReconcileKnownFilesLocked();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            StopWatchersLocked();
            foreach (var cancellation in _pending.Values) cancellation.Cancel();
            _pending.Clear();
            _sources.Clear();
        }
    }

    private void StartWatchersLocked()
    {
        foreach (var directory in _sources.Keys.Select(Path.GetDirectoryName)
                     .Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var watcher = new FileSystemWatcher(directory!)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                    InternalBufferSize = 4096,
                    EnableRaisingEvents = false
                };
                watcher.Changed += OnChanged;
                watcher.Created += OnChanged;
                watcher.Deleted += OnChanged;
                watcher.Renamed += OnRenamed;
                watcher.Error += OnError;
                _watchers[directory!] = watcher;
                watcher.EnableRaisingEvents = true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
            {
                AppLog.Warning($"An achievement folder could not be watched: {exception.GetType().Name}.");
            }
        }
    }

    private void StopWatchersLocked()
    {
        foreach (var watcher in _watchers.Values)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Changed -= OnChanged;
            watcher.Created -= OnChanged;
            watcher.Deleted -= OnChanged;
            watcher.Renamed -= OnRenamed;
            watcher.Error -= OnError;
            watcher.Dispose();
        }
        _watchers.Clear();
    }

    private void OnChanged(object sender, FileSystemEventArgs e) => Queue([e.FullPath]);

    private void OnRenamed(object sender, RenamedEventArgs e) => Queue([e.FullPath, e.OldFullPath]);

    private void OnError(object sender, ErrorEventArgs e)
    {
        AppLog.Warning("An achievement folder watcher overflowed; reconciling the already-discovered files in that folder.");
        if (sender is not FileSystemWatcher watcher) return;
        lock (_gate)
        {
            if (_disposed || _paused) return;
            var folderSources = _sources.Keys.Where(path => string.Equals(Path.GetDirectoryName(path), watcher.Path, StringComparison.OrdinalIgnoreCase)).ToArray();
            Queue(folderSources);
        }
    }

    private void ReconcileKnownFilesLocked() => Queue(_sources.Keys.ToArray());

    private void Queue(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            string fullPath;
            try { fullPath = Path.GetFullPath(path); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException) { continue; }

            lock (_gate)
            {
                if (_disposed || _paused || !_sources.ContainsKey(fullPath)) continue;
                var cancellation = new CancellationTokenSource();
                if (_pending.TryGetValue(fullPath, out var previous))
                {
                    previous.Cancel();
                }
                _pending[fullPath] = cancellation;
                _ = DeliverAfterDebounceAsync(fullPath, cancellation);
            }
        }
    }

    private async Task DeliverAfterDebounceAsync(string path, CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(700), cancellation.Token).ConfigureAwait(false);
            DiscoveredAchievementFile? source;
            lock (_gate)
            {
                if (_disposed || _paused || !_sources.TryGetValue(path, out source)) return;
                if (_pending.TryGetValue(path, out var current) && ReferenceEquals(current, cancellation))
                    _pending.TryRemove(path, out _);
            }
            SourcesChanged?.Invoke([source]);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            AppLog.Warning($"An achievement file change could not be queued: {exception.GetType().Name}.");
        }
        finally
        {
            cancellation.Dispose();
        }
    }
}
