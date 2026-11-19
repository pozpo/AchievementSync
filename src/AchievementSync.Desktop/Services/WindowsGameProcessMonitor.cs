using System.Diagnostics;
using System.Globalization;
using System.Management;
using AchievementSync.Core.Tracking;

namespace AchievementSync.Desktop.Services;

/// <summary>
/// Uses filtered WMI process trace events where the current user's WMI permissions
/// allow them. If WMI denies event subscriptions, a slow, name-filtered process
/// reconciliation is used instead. Full executable paths are always checked.
/// </summary>
internal sealed class WindowsGameProcessMonitor : IAsyncDisposable
{
    private static readonly TimeSpan EventModeMaintenanceInterval = TimeSpan.FromSeconds(30);
    private readonly TimeSpan _maintenanceInterval;

    private readonly GameplaySessionTracker _tracker;
    private readonly object _watcherGate = new();
    private readonly object _observationGate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private ManagementEventWatcher? _startWatcher;
    private ManagementEventWatcher? _stopWatcher;
    private Task? _maintenanceTask;
    private int _started;
    private volatile bool _usePollingFallback;
    private volatile bool _paused;
    private static volatile bool _protectedFallbackLogged;

    public WindowsGameProcessMonitor(GameplaySessionTracker tracker, TimeSpan? maintenanceIntervalOverride = null)
    {
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _maintenanceInterval = maintenanceIntervalOverride ?? EventModeMaintenanceInterval;
        if (_maintenanceInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maintenanceIntervalOverride));
    }

    public event EventHandler? StateChanged;

    public void SaveCheckpoint()
    {
        lock (_observationGate)
            _tracker.Checkpoint(DateTimeOffset.UtcNow);
    }

    public async Task StartAsync()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;

        await Task.Run(() =>
        {
            _usePollingFallback = _paused || !ConfigureWatchers();
            var now = DateTimeOffset.UtcNow;
            lock (_observationGate)
            {
                if (_paused) _tracker.Checkpoint(now);
                else _tracker.RecoverAfterRestart(EnumerateAssociatedProcesses(), now);
            }
            StateChanged?.Invoke(this, EventArgs.Empty);
        }).ConfigureAwait(true);

        _maintenanceTask = Task.Run(() => RunMaintenanceAsync(_shutdown.Token));
    }

    public void RefreshAssociations()
    {
        if (Volatile.Read(ref _started) == 0 || _shutdown.IsCancellationRequested || _paused) return;
        _ = Task.Run(() =>
        {
            _usePollingFallback = !ConfigureWatchers();
            lock (_observationGate)
                _tracker.ReconcileRunningProcesses(EnumerateAssociatedProcesses(), DateTimeOffset.UtcNow);
            StateChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    public void SetPaused(bool paused)
    {
        if (_paused == paused || _shutdown.IsCancellationRequested) return;
        _paused = paused;
        if (paused)
        {
            DisposeWatchers();
            lock (_observationGate) _tracker.StopAllActiveSessions(DateTimeOffset.UtcNow);
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            RefreshAssociations();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _started, 0) == 0) return;
        _shutdown.Cancel();
        if (_maintenanceTask is not null)
        {
            try { await _maintenanceTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        DisposeWatchers();
        try
        {
            lock (_observationGate)
                _tracker.Checkpoint(DateTimeOffset.UtcNow);
        }
        catch (Exception exception)
        {
            AppLog.Warning($"Could not save the final gameplay time checkpoint: {exception.GetType().Name}.");
        }
        _shutdown.Dispose();
    }

    internal static string BuildTraceQuery(string eventClass, IEnumerable<string> executableNames)
    {
        if (eventClass is not ("Win32_ProcessStartTrace" or "Win32_ProcessStopTrace"))
            throw new ArgumentException("Only the process start/stop trace event classes are supported.", nameof(eventClass));

        var filters = executableNames
            .Where(name => !string.IsNullOrWhiteSpace(name) && !name.Contains('"'))
            .Select(name => $"ProcessName = \"{name}.exe\"")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return filters.Length == 0
            ? string.Empty
            : $"SELECT * FROM {eventClass} WHERE {string.Join(" OR ", filters)}";
    }

    private async Task RunMaintenanceAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_maintenanceInterval);
        var elapsedIntervals = 0;
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                if (_paused) continue;
                if (_tracker.GetTrackedExecutableNames().Count == 0) continue;
                var now = DateTimeOffset.UtcNow;
                elapsedIntervals++;
                if (_usePollingFallback)
                {
                    lock (_observationGate)
                        _tracker.ReconcileRunningProcesses(EnumerateAssociatedProcesses(), now);
                }
                else if (elapsedIntervals >= 4)
                {
                    elapsedIntervals = 0;
                    lock (_observationGate)
                        _tracker.ReconcileRunningProcesses(EnumerateAssociatedProcesses(), now);
                }
                else if (elapsedIntervals % 2 == 0)
                {
                    lock (_observationGate)
                        _tracker.Checkpoint(now);
                }
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception exception)
            {
                AppLog.Warning($"Gameplay monitoring maintenance could not complete: {exception.GetType().Name}.");
            }
        }
    }

    private bool ConfigureWatchers()
    {
        DisposeWatchers();
        var names = _tracker.GetTrackedExecutableNames();
        if (names.Count == 0) return true;

        var startQuery = BuildTraceQuery("Win32_ProcessStartTrace", names);
        var stopQuery = BuildTraceQuery("Win32_ProcessStopTrace", names);
        if (startQuery.Length == 0 || stopQuery.Length == 0) return false;

        ManagementEventWatcher? startWatcher = null;
        ManagementEventWatcher? stopWatcher = null;
        try
        {
            startWatcher = new ManagementEventWatcher(new WqlEventQuery(startQuery));
            stopWatcher = new ManagementEventWatcher(new WqlEventQuery(stopQuery));
            startWatcher.EventArrived += OnProcessStarted;
            stopWatcher.EventArrived += OnProcessStopped;
            startWatcher.Start();
            try
            {
                stopWatcher.Start();
            }
            catch
            {
                startWatcher.Stop();
                startWatcher.Dispose();
                stopWatcher.Dispose();
                throw;
            }

            lock (_watcherGate)
            {
                _startWatcher = startWatcher;
                _stopWatcher = stopWatcher;
            }
            return true;
        }
        catch (Exception exception) when (exception is ManagementException or System.Runtime.InteropServices.COMException
            or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            DisposeWatcher(startWatcher);
            DisposeWatcher(stopWatcher);
            var managementStatus = exception is ManagementException managementException
                ? $", WMI={managementException.ErrorCode}"
                : string.Empty;
            AppLog.Warning($"Windows process event monitoring is unavailable; associated executable names will be reconciled every {_maintenanceInterval.TotalSeconds:0.#} seconds ({exception.GetType().Name}, HRESULT=0x{exception.HResult:X8}{managementStatus}).");
            return false;
        }
    }

    private void DisposeWatchers()
    {
        ManagementEventWatcher? start;
        ManagementEventWatcher? stop;
        lock (_watcherGate)
        {
            start = _startWatcher;
            stop = _stopWatcher;
            _startWatcher = null;
            _stopWatcher = null;
        }

        DisposeWatcher(start);
        DisposeWatcher(stop);
    }

    private void DisposeWatcher(ManagementEventWatcher? watcher)
    {
        if (watcher is null) return;
        watcher.EventArrived -= OnProcessStarted;
        watcher.EventArrived -= OnProcessStopped;
        try { watcher.Stop(); }
        catch (Exception exception) when (exception is ManagementException or InvalidOperationException or System.Runtime.InteropServices.COMException) { }
        watcher.Dispose();
    }

    private void OnProcessStarted(object sender, EventArrivedEventArgs e)
    {
        if (_paused) return;
        try
        {
            bool changed;
            lock (_observationGate)
            {
                if (!TryReadInt(e.NewEvent["ProcessID"], out var processId)
                    || !TryReadProcess(processId, out var process)) return;
                changed = _tracker.ObserveProcessStarted(process, DateTimeOffset.UtcNow);
            }
            if (changed)
                StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            AppLog.Warning($"A process start event could not be processed: {exception.GetType().Name}.");
        }
    }

    private void OnProcessStopped(object sender, EventArrivedEventArgs e)
    {
        if (_paused) return;
        try
        {
            bool changed;
            lock (_observationGate)
            {
                if (!TryReadInt(e.NewEvent["ProcessID"], out var processId)) return;
                changed = _tracker.ObserveProcessStopped(processId, DateTimeOffset.UtcNow);
            }
            if (changed)
                StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            AppLog.Warning($"A process stop event could not be processed: {exception.GetType().Name}.");
        }
    }

    private IReadOnlyList<RunningProcessObservation> EnumerateAssociatedProcesses()
    {
        var result = new List<RunningProcessObservation>();
        foreach (var name in _tracker.GetTrackedExecutableNames())
        {
            Process[] processes;
            try { processes = Process.GetProcessesByName(name); }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                AppLog.Warning($"Could not reconcile one associated executable name: {exception.GetType().Name}.");
                continue;
            }

            foreach (var process in processes)
            {
                using (process)
                {
                    if (TryReadProcess(process, out var observation)) result.Add(observation);
                }
            }
        }
        return result;
    }

    private static bool TryReadProcess(int processId, out RunningProcessObservation observation)
    {
        observation = null!;
        try
        {
            using var process = Process.GetProcessById(processId);
            return TryReadProcess(process, out observation);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryReadProcess(Process process, out RunningProcessObservation observation)
    {
        observation = null!;
        try
        {
            var executablePath = process.MainModule?.FileName;
            var startedAtUtc = process.StartTime.ToUniversalTime();
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                observation = UnverifiableObservation(process, startedAtUtc);
                return true;
            }

            observation = new RunningProcessObservation(
                process.Id,
                executablePath,
                new DateTimeOffset(DateTime.SpecifyKind(startedAtUtc, DateTimeKind.Utc)));
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            try
            {
                var startedAtUtc = process.StartTime.ToUniversalTime();
                observation = UnverifiableObservation(process, startedAtUtc);
                return true;
            }
            catch (Exception innerException) when (innerException is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                return false;
            }
        }
    }

    private static RunningProcessObservation UnverifiableObservation(Process process, DateTime startedAtUtc)
    {
        // Known limit: protected games expose no readable image path (QueryFullProcessImageName,
        // WMI ExecutablePath, .NET MainModule all fail). Match them by process name instead; the
        // path association stays authoritative whenever the path is readable.
        if (!_protectedFallbackLogged)
        {
            _protectedFallbackLogged = true;
            AppLog.Info($"Detected a protected game process ({process.ProcessName}); it will be matched by process name because its path cannot be read.");
        }

        var name = !string.IsNullOrWhiteSpace(process.ProcessName) ? process.ProcessName : "unknown";
        return new RunningProcessObservation(
            process.Id,
            AchievementSync.Core.Tracking.ProcessObservationIdentity.UnverifiablePath(name),
            new DateTimeOffset(DateTime.SpecifyKind(startedAtUtc, DateTimeKind.Utc)),
            name);
    }

    private static bool TryReadInt(object? value, out int result)
    {
        try
        {
            result = Convert.ToInt32(value, CultureInfo.InvariantCulture);
            return result > 0;
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            result = 0;
            return false;
        }
    }
}
