using System.Collections.Concurrent;
using System.IO;

namespace AchievementSync.Desktop.Services;

/// <summary>
/// Watches configured local platform folders and asks the owning platform adapter to refresh
/// after relevant achievement or save data changes.
/// </summary>
public sealed class PlatformSourceFileMonitor : IDisposable
{
    private static readonly HashSet<string> RelevantExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".acf", ".bak", ".bin", ".cfg", ".dat", ".db", ".gpd", ".ini", ".json", ".jpg", ".jpeg", ".key", ".log",
        ".mcr", ".old", ".profile", ".py", ".sav", ".save", ".sfm", ".slt", ".srm",
        ".png", ".sqlite", ".slot", ".toml", ".tres", ".trp", ".webp", ".xml", ".vdf", ".rpgsave", ".ess", ".fos", ".autosave"
    };

    private readonly object _gate = new();
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pending = new(StringComparer.OrdinalIgnoreCase);
    private (string Key, string Path)[] _roots = [];
    private bool _paused;
    private bool _disposed;

    public event Action<string>? PlatformChanged;

    public void Configure(IEnumerable<(string Key, string Path)> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _roots = roots.ToArray();
            StopWatchersLocked();
            foreach (var cancellation in _pending.Values) cancellation.Cancel();
            _pending.Clear();

            if (_paused) return;
            foreach (var (key, path) in _roots)
            {
                var platform = PlatformForRoot(key);
                if (platform is null || string.IsNullOrWhiteSpace(path)) continue;

                string fullPath;
                try { fullPath = Path.GetFullPath(path); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
                {
                    continue;
                }

                var watchPath = fullPath;
                var waitingForRoot = false;
                if (!Directory.Exists(watchPath))
                {
                    waitingForRoot = true;
                    var ancestor = Path.GetDirectoryName(watchPath);
                    while (!string.IsNullOrWhiteSpace(ancestor) && !Directory.Exists(ancestor))
                        ancestor = Path.GetDirectoryName(ancestor);
                    if (string.IsNullOrWhiteSpace(ancestor)) continue;
                    watchPath = ancestor;
                }

                var watcherKey = platform + "|" + watchPath + (waitingForRoot ? "|pending|" + fullPath : string.Empty);
                if (_watchers.ContainsKey(watcherKey)) continue;
                try
                {
                    var watcher = new FileSystemWatcher(watchPath)
                    {
                        IncludeSubdirectories = !waitingForRoot,
                        NotifyFilter = waitingForRoot
                            ? NotifyFilters.DirectoryName
                            : NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                        InternalBufferSize = 16 * 1024,
                        EnableRaisingEvents = false
                    };
                    if (waitingForRoot)
                    {
                        watcher.Created += (_, e) => OnAncestorDirectoryCreated(platform, fullPath, e.FullPath);
                        watcher.Renamed += (_, e) => OnAncestorDirectoryCreated(platform, fullPath, e.FullPath);
                    }
                    else
                    {
                        watcher.Changed += (_, e) => OnChanged(platform, e.FullPath);
                        watcher.Created += (_, e) => OnChanged(platform, e.FullPath);
                        watcher.Deleted += (_, e) => OnChanged(platform, e.FullPath);
                        watcher.Renamed += (_, e) => OnChanged(platform, e.FullPath);
                    }
                    watcher.Error += (_, _) =>
                    {
                        AppLog.Warning($"The {platform} platform folder watcher overflowed; refreshing that platform.");
                        Queue(platform);
                    };
                    _watchers[watcherKey] = watcher;
                    watcher.EnableRaisingEvents = true;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
                {
                    AppLog.Warning($"A platform source folder could not be watched ({platform}): {exception.GetType().Name}.");
                }
            }
        }
    }

    public void SetPaused(bool paused)
    {
        (string Key, string Path)[] roots;
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
            roots = _roots;
        }
        if (!paused) Configure(roots);
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
            _roots = [];
        }
    }

    private void OnChanged(string platform, string path)
    {
        if (!RelevantExtensions.Contains(Path.GetExtension(path))) return;
        Queue(platform);
    }

    private void OnAncestorDirectoryCreated(string platform, string configuredRoot, string changedPath)
    {
        if (!Directory.Exists(changedPath) || !IsAncestorOrSame(changedPath, configuredRoot)) return;
        (string Key, string Path)[] roots;
        lock (_gate)
        {
            if (_disposed || _paused) return;
            roots = _roots;
        }
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                Configure(roots);
                Queue(platform);
            }
            catch (ObjectDisposedException) { }
        });
    }

    private static bool IsAncestorOrSame(string ancestor, string path)
    {
        try
        {
            var relative = Path.GetRelativePath(Path.GetFullPath(ancestor), Path.GetFullPath(path));
            return relative == "."
                || (!Path.IsPathRooted(relative)
                    && relative != ".."
                    && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private void Queue(string platform)
    {
        lock (_gate)
        {
            if (_disposed || _paused) return;
            var cancellation = new CancellationTokenSource();
            if (_pending.TryGetValue(platform, out var previous)) previous.Cancel();
            _pending[platform] = cancellation;
            _ = DeliverAfterDebounceAsync(platform, cancellation);
        }
    }

    private async Task DeliverAfterDebounceAsync(string platform, CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(900), cancellation.Token).ConfigureAwait(false);
            lock (_gate)
            {
                if (_disposed || _paused) return;
                if (_pending.TryGetValue(platform, out var current) && ReferenceEquals(current, cancellation))
                    _pending.TryRemove(platform, out _);
                else
                    return;
            }
            PlatformChanged?.Invoke(platform);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            AppLog.Warning($"A changed platform source could not be queued: {exception.GetType().Name}.");
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private void StopWatchersLocked()
    {
        foreach (var watcher in _watchers.Values)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        _watchers.Clear();
    }

    private static string? PlatformForRoot(string key) => key.ToLowerInvariant() switch
    {
        "steam" => "steam",
        "xblen" => "xbox",
        "xboxpc" => "xboxpc",
        "xbox360" or "xenia" => "xbox360",
        "gfwl" => "gfwl",
        "rpcs3" or "ps4" or "ps5" => "playstation",
        "epic" => "epic",
        "gog" => "gog",
        "uplay" or "uplaycache" or "uplayrune" => "uplay",
        "ea" => "ea",
        "recomp" or "ladxhd" => "recomp",
        "lichii" or "eglantine" => "switch2",
        "switch2recomp" => "switch2recomp",
        "retroachievements" or "ra" => "retroachievements",
        _ => null
    };
}
