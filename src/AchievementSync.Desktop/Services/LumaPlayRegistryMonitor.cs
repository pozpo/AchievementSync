using System.Runtime.InteropServices;

namespace AchievementSync.Desktop.Services;

/// <summary>Uses Windows registry change notifications for HKCU\SOFTWARE\LumaPlay and coalesces bursts.</summary>
internal sealed class LumaPlayRegistryMonitor(Action refresh) : IDisposable
{
    private const int ErrorSuccess = 0;
    private const uint KeyRead = 0x20019;
    private const uint KeyNotify = 0x0010;
    private const uint NotifyName = 0x00000001;
    private const uint NotifyLastSet = 0x00000004;
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 258;
    private static readonly IntPtr HKeyCurrentUser = new(unchecked((int)0x80000001));

    private readonly object _gate = new();
    private readonly Action _refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
    private CancellationTokenSource? _runCancellation;
    private CancellationTokenSource? _pendingRefresh;
    private bool _enabled;
    private bool _paused;
    private bool _disposed;

    public void Configure(bool enabled)
    {
        lock (_gate)
        {
            if (_disposed || _enabled == enabled) return;
            _enabled = enabled;
            RestartLocked();
        }
    }

    public void SetPaused(bool paused)
    {
        lock (_gate)
        {
            if (_disposed || _paused == paused) return;
            _paused = paused;
            RestartLocked();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _enabled = false;
            _runCancellation?.Cancel();
            _runCancellation?.Dispose();
            _runCancellation = null;
            _pendingRefresh?.Cancel();
            _pendingRefresh?.Dispose();
            _pendingRefresh = null;
        }
    }

    private void RestartLocked()
    {
        _runCancellation?.Cancel();
        _runCancellation?.Dispose();
        _runCancellation = null;
        if (!_enabled || _paused || !OperatingSystem.IsWindows()) return;

        _runCancellation = new CancellationTokenSource();
        var token = _runCancellation.Token;
        _ = Task.Run(() => MonitorLoop(token), token);
    }

    private void MonitorLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var watchedKey = TryOpenKey(@"SOFTWARE\LumaPlay", KeyRead | KeyNotify, out var key)
                ? key
                : IntPtr.Zero;
            var waitingForLumaKey = watchedKey == IntPtr.Zero;
            if (waitingForLumaKey && !TryOpenKey("SOFTWARE", KeyRead | KeyNotify, out watchedKey))
            {
                WaitBriefly(cancellationToken);
                continue;
            }

            var eventHandle = CreateEventW(IntPtr.Zero, true, false, null);
            if (eventHandle == IntPtr.Zero)
            {
                CloseKey(watchedKey);
                WaitBriefly(cancellationToken);
                continue;
            }

            try
            {
                var filter = waitingForLumaKey ? NotifyName : NotifyName | NotifyLastSet;
                var subtree = !waitingForLumaKey;
                while (!cancellationToken.IsCancellationRequested)
                {
                    _ = ResetEvent(eventHandle);
                    var result = RegNotifyChangeKeyValue(watchedKey, subtree, filter, eventHandle, true);
                    if (result != ErrorSuccess) break;

                    var signaled = false;
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        var wait = WaitForSingleObject(eventHandle, 500);
                        if (wait == WaitObject0)
                        {
                            signaled = true;
                            break;
                        }
                        if (wait != WaitTimeout) break;
                    }

                    if (!signaled) break;
                    var discoveredKey = IntPtr.Zero;
                    if (!waitingForLumaKey || TryOpenKey(@"SOFTWARE\LumaPlay", KeyRead, out discoveredKey))
                    {
                        if (waitingForLumaKey) CloseKey(discoveredKey);
                        QueueRefresh();
                    }
                    if (waitingForLumaKey) break;
                }
            }
            finally
            {
                _ = CloseHandle(eventHandle);
                CloseKey(watchedKey);
            }

            WaitBriefly(cancellationToken);
        }
    }

    private void QueueRefresh()
    {
        CancellationTokenSource cancellation;
        lock (_gate)
        {
            if (_disposed || !_enabled || _paused) return;
            _pendingRefresh?.Cancel();
            _pendingRefresh?.Dispose();
            cancellation = new CancellationTokenSource();
            _pendingRefresh = cancellation;
        }

        _ = DeliverRefreshAsync(cancellation);
    }

    private async Task DeliverRefreshAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(900), cancellation.Token).ConfigureAwait(false);
            lock (_gate)
            {
                if (_disposed || !_enabled || _paused || !ReferenceEquals(_pendingRefresh, cancellation)) return;
                _pendingRefresh = null;
            }
            _refresh();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            AppLog.Warning($"The LumaPlay registry change could not refresh platform data ({exception.GetType().Name}).");
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private static void WaitBriefly(CancellationToken cancellationToken)
    {
        try { cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(500)); }
        catch (ObjectDisposedException) { }
    }

    private static bool TryOpenKey(string subKey, uint access, out IntPtr key) =>
        RegOpenKeyExW(HKeyCurrentUser, subKey, 0, access, out key) == ErrorSuccess;

    private static void CloseKey(IntPtr key)
    {
        if (key != IntPtr.Zero) _ = RegCloseKey(key);
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegOpenKeyExW(IntPtr hKey, string subKey, uint options, uint samDesired, out IntPtr result);

    [DllImport("advapi32.dll")]
    private static extern int RegNotifyChangeKeyValue(IntPtr hKey, bool watchSubtree, uint notifyFilter, IntPtr eventHandle, bool asynchronous);

    [DllImport("advapi32.dll")]
    private static extern int RegCloseKey(IntPtr hKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateEventW(IntPtr eventAttributes, bool manualReset, bool initialState, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ResetEvent(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
