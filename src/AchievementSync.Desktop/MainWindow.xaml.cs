using System.Windows;
using AchievementSync.Desktop.ViewModels;
using AchievementSync.Desktop.Services;
using Microsoft.Win32;
using System.Collections;
using System.IO;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace AchievementSync.Desktop;

internal static class NativeMethods
{
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);

    public const uint RDW_INVALIDATE = 0x1;
    public const uint RDW_INTERNALPAINT = 0x2;
    public const uint RDW_ERASE = 0x4;
    public const uint RDW_VALIDATE = 0x8;
    public const uint RDW_NOINTERNALPAINT = 0x10;
    public const uint RDW_NOERASE = 0x20;
    public const uint RDW_NOCHILDREN = 0x40;
    public const uint RDW_ALLCHILDREN = 0x80;
    public const uint RDW_UPDATENOW = 0x100;
    public const uint RDW_ERASEBKGND = 0x10;
    public const uint RDW_FRAME = 0x400;
}

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    internal MainViewModel ViewModel => _viewModel;
    private WindowsGameProcessMonitor? _processMonitor;
    private LocalAchievementFileMonitor? _achievementFileMonitor;
    private AchievementTrayIcon? _trayIcon;
    private bool _loaded;
    private bool _allowClose;
    private bool _monitoringPaused;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _trayIcon = new AchievementTrayIcon();
        _trayIcon.OpenRequested += Tray_OpenRequested;
        _trayIcon.PauseToggleRequested += Tray_PauseToggleRequested;
        _trayIcon.ExitRequested += Tray_ExitRequested;
        _viewModel.WatchSourcesChanged += OnWatchSourcesChanged;
        _viewModel.AchievementUnlocked += OnAchievementUnlocked;
        _viewModel.TestNotificationRequested += OnTestNotificationRequested;
        SourceInitialized += (_, _) => WindowAppearance.Apply(new WindowInteropHelper(this).Handle);
        _viewModel.GameRequested += (_, game) =>
        {
            var details = new GameDetailsWindow(game) { Owner = this };
            details.ManualNameSubmitted += title => _viewModel.SetManualGameName(game, title);
            details.ExecutableAssociationRequested += requestedGame => AddGameExecutables(requestedGame);
            details.ShowDialog();
        };
        _viewModel.ProcessTrackingConfigurationChanged += OnProcessTrackingConfigurationChanged;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
    }

    public async Task InitializeAppAsync()
    {
        if (_loaded) return;
        _loaded = true;
        await _viewModel.LoadAsync();
        _achievementFileMonitor = new LocalAchievementFileMonitor();
        _achievementFileMonitor.SourcesChanged += OnAchievementSourcesChanged;
        _achievementFileMonitor.Configure(_viewModel.WatchableSourceFiles);
        if (_monitoringPaused) _achievementFileMonitor.SetPaused(true);
        _processMonitor = new WindowsGameProcessMonitor(_viewModel.GameplayTracker);
        _processMonitor.StateChanged += OnPlaytimeChanged;
        await _processMonitor.StartAsync();
        if (_monitoringPaused) _processMonitor.SetPaused(true);
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
        {
            await InitializeAppAsync();
        }

        // Force the DWM compositor to repaint the entire window once so any
        // pixels painted before the implicit dark resource dictionaries got
        // applied (the classic WPF startup "white flash") are cleared.
        try
        {
            var helper = new WindowInteropHelper(this);
            NativeMethods.RedrawWindow(helper.Handle, IntPtr.Zero, IntPtr.Zero,
                NativeMethods.RDW_INVALIDATE | NativeMethods.RDW_ERASE | NativeMethods.RDW_FRAME | NativeMethods.RDW_ALLCHILDREN);
        }
        catch { /* best effort only */ }
    }

    private void OnProcessTrackingConfigurationChanged(object? sender, EventArgs e) => _processMonitor?.RefreshAssociations();

    private void OnWatchSourcesChanged(object? sender, IReadOnlyList<AchievementSync.Core.Scanning.DiscoveredAchievementFile> sources) =>
        _achievementFileMonitor?.Configure(sources);

    private void OnAchievementSourcesChanged(IReadOnlyList<AchievementSync.Core.Scanning.DiscoveredAchievementFile> sources) =>
        _ = _viewModel.ProcessChangedSourcesAsync(sources);

    private async void OnAchievementUnlocked(object? sender, AchievementSync.Core.Tracking.AchievementUnlockNotification notification)
    {
        var game = _viewModel.Games.FirstOrDefault(item => string.Equals(item.AppId, notification.AppId, StringComparison.Ordinal));
        ImageSource? icon = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(notification.IconUri))
                icon = await AchievementArtworkCache.GetAsync(notification.IconUri).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            AppLog.Warning($"The achievement icon could not be resolved: {exception.GetType().Name}.");
        }

        icon ??= game?.Artwork;
        AchievementToastWindow.Show(notification, icon, BuildToastPresentation());
    }

    private void OnTestNotificationRequested(object? sender, EventArgs e) => AchievementToastWindow.Show(
        new AchievementSync.Core.Tracking.AchievementUnlockNotification(
            "0", "AchievementSync", "notification_test", "Test notification · your settings are working",
            null, null, "Your achievement is ready."),
        null,
        BuildToastPresentation());

    private ToastPresentation BuildToastPresentation() =>
        new(_viewModel.NotificationPosition, _viewModel.NotificationOffsetX, _viewModel.NotificationOffsetY,
            _viewModel.NotificationSoundEnabled, _viewModel.NotificationSoundVolume,
            _viewModel.NotificationSound, _viewModel.NotificationSoundPath);

    private void OnPlaytimeChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(
        DispatcherPriority.DataBind,
        new Action(() => _viewModel.RefreshPlaytime(DateTimeOffset.UtcNow)));

    private void AddGameExecutables(GameCardViewModel game)
    {
        var picker = new OpenFileDialog
        {
            Title = $"Choose the actual game executable for {game.Title}",
            Filter = "Windows applications (*.exe)|*.exe",
            Multiselect = true,
            CheckFileExists = true,
            CheckPathExists = true
        };

        if (picker.ShowDialog(this) != true) return;

        var added = 0;
        var failures = new List<string>();
        foreach (var executable in picker.FileNames)
        {
            if (_viewModel.AddGameExecutable(game, executable, out var error)) added++;
            else if (error is not null) failures.Add(error);
        }

        if (added > 0) return;
        MessageBox.Show(
            failures.FirstOrDefault() ?? "No executable association was added.",
            "Game executable not linked",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private async void MainWindow_Closed(object? sender, EventArgs e)
    {
        _viewModel.ProcessTrackingConfigurationChanged -= OnProcessTrackingConfigurationChanged;
        _viewModel.WatchSourcesChanged -= OnWatchSourcesChanged;
        _viewModel.AchievementUnlocked -= OnAchievementUnlocked;
        _viewModel.TestNotificationRequested -= OnTestNotificationRequested;
        if (_achievementFileMonitor is not null)
        {
            _achievementFileMonitor.SourcesChanged -= OnAchievementSourcesChanged;
            _achievementFileMonitor.Dispose();
            _achievementFileMonitor = null;
        }
        if (_processMonitor is not null)
        {
            _processMonitor.StateChanged -= OnPlaytimeChanged;
            await _processMonitor.DisposeAsync();
            _processMonitor = null;
        }
        DisposeTrayIcon();
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        try { _processMonitor?.SaveCheckpoint(); }
        catch (Exception exception) { AppLog.Warning($"Could not save the gameplay checkpoint while closing: {exception.GetType().Name}."); }
        if (_allowClose) return;
        e.Cancel = true;
        Hide();
    }

    private void Tray_OpenRequested(object? sender, EventArgs e) => Dispatcher.BeginInvoke(new Action(() =>
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }));

    private void Tray_PauseToggleRequested(object? sender, EventArgs e) => Dispatcher.BeginInvoke(new Action(() =>
    {
        _monitoringPaused = !_monitoringPaused;
        _processMonitor?.SetPaused(_monitoringPaused);
        _achievementFileMonitor?.SetPaused(_monitoringPaused);
        _trayIcon?.SetPaused(_monitoringPaused);
    }));

    private async void Tray_ExitRequested(object? sender, EventArgs e)
    {
        if (_allowClose) return;
        _allowClose = true;
        DisposeTrayIcon();
        _viewModel.WatchSourcesChanged -= OnWatchSourcesChanged;
        _viewModel.AchievementUnlocked -= OnAchievementUnlocked;
        _viewModel.TestNotificationRequested -= OnTestNotificationRequested;
        if (_achievementFileMonitor is not null)
        {
            _achievementFileMonitor.SourcesChanged -= OnAchievementSourcesChanged;
            _achievementFileMonitor.Dispose();
            _achievementFileMonitor = null;
        }
        if (_processMonitor is not null)
        {
            _processMonitor.StateChanged -= OnPlaytimeChanged;
            try { await _processMonitor.DisposeAsync(); }
            catch (Exception exception) { AppLog.Warning($"Gameplay monitoring did not stop cleanly: {exception.GetType().Name}."); }
            _processMonitor = null;
        }
        Close();
        Application.Current.Shutdown();
    }

    private void DisposeTrayIcon()
    {
        if (_trayIcon is null) return;
        _trayIcon.OpenRequested -= Tray_OpenRequested;
        _trayIcon.PauseToggleRequested -= Tray_PauseToggleRequested;
        _trayIcon.ExitRequested -= Tray_ExitRequested;
        _trayIcon.Dispose();
        _trayIcon = null;
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose a folder containing achievement profiles",
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.AddCustomLocation(dialog.FolderName);
        }
    }
}
