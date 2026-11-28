using System.Windows;
using AchievementSync.Core.Models;
using AchievementSync.Desktop.ViewModels;
using AchievementSync.Desktop.Services;
using AchievementSync.Desktop.Controls;
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
using System.Windows.Forms;

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
    private PlatformSourceFileMonitor? _platformSourceMonitor;
    private LumaPlayRegistryMonitor? _lumaPlayRegistryMonitor;
    private readonly SteamDbScreenshotCache _steamDbScreenshotCache = new();
    private AchievementTrayIcon? _trayIcon;
    private bool _loaded;
    private bool _allowClose;
    private bool _monitoringPaused;

    public MainWindow()
    {
        InitializeComponent();
        AchievementEnablerHost.Content = new AchievementEnablerControl(_viewModel.EnablerService);
        DataContext = _viewModel;
        _trayIcon = new AchievementTrayIcon();
        _trayIcon.OpenRequested += Tray_OpenRequested;
        _trayIcon.PauseToggleRequested += Tray_PauseToggleRequested;
        _trayIcon.ExitRequested += Tray_ExitRequested;
        _viewModel.WatchSourcesChanged += OnWatchSourcesChanged;
        _viewModel.PlatformSourceRootsChanged += OnPlatformSourceRootsChanged;
        _viewModel.AchievementUnlocked += OnAchievementUnlocked;
        _viewModel.GameSessionStateChanged += OnGameSessionStateChanged;
        _viewModel.TestNotificationRequested += OnTestNotificationRequested;
        _viewModel.UpdateRestartRequested += OnUpdateRestartRequested;
        _viewModel.UpdateAvailable += OnUpdateAvailable;
        SourceInitialized += (_, _) => WindowAppearance.Apply(new WindowInteropHelper(this).Handle);
        _viewModel.GameRequested += (_, game) =>
        {
            AppLog.Action($"Opened achievement details for AppID {game.AppId} ({game.Title}).");
            var details = new GameDetailsWindow(game) { Owner = this };
            details.ManualNameSubmitted += title => _viewModel.SetManualGameName(game, title);
            details.ExecutableAssociationRequested += requestedGame => AddGameExecutables(requestedGame);
            details.ShowDialog();
        };
        _viewModel.ProcessTrackingConfigurationChanged += OnProcessTrackingConfigurationChanged;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
    }

    public async Task InitializeAppAsync()
    {
        if (_loaded) return;
        _loaded = true;
        AppLog.Action("Application startup initialization began.");
        _viewModel.RepairStartupRegistration();
        await _viewModel.LoadAsync();
        _achievementFileMonitor = new LocalAchievementFileMonitor();
        _achievementFileMonitor.SourcesChanged += OnAchievementSourcesChanged;
        _achievementFileMonitor.Configure(_viewModel.WatchableSourceFiles);
        if (_monitoringPaused) _achievementFileMonitor.SetPaused(true);
        _platformSourceMonitor = new PlatformSourceFileMonitor();
        _platformSourceMonitor.PlatformChanged += OnPlatformSourceChanged;
        _platformSourceMonitor.Configure(_viewModel.WatchablePlatformRoots);
        if (_monitoringPaused) _platformSourceMonitor.SetPaused(true);
        _lumaPlayRegistryMonitor = new LumaPlayRegistryMonitor(() =>
        {
            Dispatcher.BeginInvoke(new Action(() => _ = _viewModel.ScanPlatformAsync("uplay")));
        });
        _lumaPlayRegistryMonitor.Configure(_viewModel.LumaPlayWatcherEnabled);
        if (_monitoringPaused) _lumaPlayRegistryMonitor.SetPaused(true);
        _processMonitor = new WindowsGameProcessMonitor(_viewModel.GameplayTracker);
        _processMonitor.StateChanged += OnPlaytimeChanged;
        await _processMonitor.StartAsync();
        if (_monitoringPaused) _processMonitor.SetPaused(true);
        _ = _viewModel.CheckForUpdatesAsync();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
        {
            await InitializeAppAsync();
        }

        // Ensure the window is fully within the visible screen bounds so the title bar
        // is always reachable on smaller screens or after moving between monitors.
        if (_viewModel.CompactModeEnabled)
        {
            ApplyCompactMode(true);
        }
        else
        {
            try
            {
                var screen = Screen.FromHandle(new WindowInteropHelper(this).Handle);
                if (screen.WorkingArea.Height <= 800)
                {
                    ApplyCompactMode(true);
                }
            }
            catch { }
        }
        ClampWindowToScreen();

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

    private void ClampWindowToScreen()
    {
        try
        {
            var screen = Screen.FromHandle(new WindowInteropHelper(this).Handle);
            var working = screen.WorkingArea;

            var left = Left;
            var top = Top;
            var width = Width;
            var height = Height;

            // Clamp width/height to fit within the screen working area.
            if (width > working.Width) width = working.Width * 0.95;
            if (height > working.Height) height = working.Height * 0.95;

            // Clamp position so the window (and its title bar) stays visible.
            if (left < working.Left) left = working.Left + 10;
            if (top < working.Top) top = working.Top + 10;
            if (left + width > working.Right) left = working.Right - width - 10;
            if (top + height > working.Bottom) top = working.Bottom - height - 10;

            Left = left;
            Top = top;
            Width = width;
            Height = height;
        }
        catch
        {
            // Best effort only — if screen info is unavailable, let Windows position the window.
        }
    }

    private void OnProcessTrackingConfigurationChanged(object? sender, EventArgs e) => _processMonitor?.RefreshAssociations();

    private void SavePlatformCredentials_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.SavePlatformCredentials(RetroAchievementsApiKeyBox.Password);
        RetroAchievementsApiKeyBox.Clear();
    }

    private async void ConnectXboxPlatform_Click(object sender, RoutedEventArgs e) =>
        await _viewModel.ConnectXboxPlatformAsync();

    private async void ConnectGitHubWithToken_Click(object sender, RoutedEventArgs e)
    {
        var token = GitHubTokenBox.Password;
        GitHubTokenBox.Clear();
        await _viewModel.ConnectGitHubWithTokenAsync(token);
    }

    private void ClearPlatformCredentials_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ClearPlatformCredentials();
        RetroAchievementsApiKeyBox.Clear();
    }

    private void OnWatchSourcesChanged(object? sender, IReadOnlyList<AchievementSync.Core.Scanning.DiscoveredAchievementFile> sources) =>
        _achievementFileMonitor?.Configure(sources);

    private void OnPlatformSourceRootsChanged(object? sender, EventArgs e) =>
        _platformSourceMonitor?.Configure(_viewModel.WatchablePlatformRoots);

    private void OnPlatformSourceChanged(string platform) =>
        Dispatcher.BeginInvoke(new Action(() => _ = _viewModel.ScanPlatformAsync(platform)));

    private void OnAchievementSourcesChanged(IReadOnlyList<AchievementSync.Core.Scanning.DiscoveredAchievementFile> sources) =>
        _ = _viewModel.ProcessChangedSourcesAsync(sources);

    private async void OnAchievementUnlocked(object? sender, AchievementSync.Core.Tracking.AchievementUnlockNotification notification)
    {
        AppLog.Action($"Achievement unlock notification received for AppID {notification.AppId} ({notification.AchievementName}).");
        var game = _viewModel.Games.FirstOrDefault(item => string.Equals(item.AppId, notification.AppId, StringComparison.Ordinal));
        ImageSource? icon = null;
        try
        {
            if (AchievementIdentity.IsMetadataImageUri(notification.IconUri))
                icon = await AchievementArtworkCache.GetAsync(notification.IconUri).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            AppLog.Warning($"The achievement icon could not be resolved: {exception.GetType().Name}.");
        }

        icon ??= game?.Artwork;
        AchievementToastWindow.Show(notification, icon, BuildToastPresentation());
    }

    private void OnGameSessionStateChanged(object? sender, GameSessionEventArgs e)
    {
        var game = e.Game;
        AppLog.Action($"Play session {(e.Started ? "started" : "ended")} for AppID {game.AppId} ({game.Title}); durationSeconds={e.SessionDuration.TotalSeconds:0}.");
        if (!e.Started && _viewModel.AutoBackupSavesAfterPlaytime)
            _ = _viewModel.AutoBackupSavesAfterGameSessionAsync(game.AppId);
        if (!_viewModel.PlaytimeNotificationsEnabled) return;

        var screenshotTask = _steamDbScreenshotCache.GetRandomScreenshotAsync(game.AppId);

        if (e.Started)
        {
            PlaytimeNotificationWindow.ShowPlaytimeNotification(
                appId: game.AppId,
                gameTitle: game.Title,
                isStart: true,
                message: "Playtime tracking started · Tracking achievements",
                artwork: game.Artwork,
                compactMode: _viewModel.CompactModeEnabled,
                screenshotTask: screenshotTask);
        }
        else
        {
            var sessionStr = FormatFriendlyDuration(e.SessionDuration);
            var totalStr = FormatFriendlyDuration(e.TotalPlaytime);
            var message = $"Session: {sessionStr}   •   Total: {totalStr}";

            PlaytimeNotificationWindow.ShowPlaytimeNotification(
                appId: game.AppId,
                gameTitle: game.Title,
                isStart: false,
                message: message,
                artwork: game.Artwork,
                compactMode: _viewModel.CompactModeEnabled,
                screenshotTask: screenshotTask);
        }
    }

    private static string FormatFriendlyDuration(TimeSpan duration)
    {
        if (duration.TotalMinutes < 1)
            return $"{Math.Max(1, (int)duration.TotalSeconds)}s";
        if (duration.TotalHours < 1)
            return $"{(int)duration.TotalMinutes}m";
        var hours = (int)duration.TotalHours;
        var minutes = duration.Minutes;
        return minutes > 0 ? $"{hours}h {minutes}m" : $"{hours}h";
    }

    private void OnTestNotificationRequested(object? sender, EventArgs e) => AchievementToastWindow.Show(
        new AchievementSync.Core.Tracking.AchievementUnlockNotification(
            "0", "AchievementSync", "notification_test", "Test notification · your settings are working",
            null, null, "Your achievement is ready."),
        null,
        BuildToastPresentation());

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CompactModeEnabled))
        {
            ApplyCompactMode(_viewModel.CompactModeEnabled);
        }
        else if (e.PropertyName == nameof(MainViewModel.LumaPlayWatcherEnabled))
        {
            _lumaPlayRegistryMonitor?.Configure(_viewModel.LumaPlayWatcherEnabled);
        }
    }

    private void ApplyCompactMode(bool enabled)
    {
        if (enabled)
        {
            MinWidth = 760;
            MinHeight = 460;
            if (Width > 960) Width = 960;
            if (Height > 560) Height = 560;
            ClampWindowToScreen();
        }
        else
        {
            MinWidth = 900;
            MinHeight = 600;
        }
    }

    private ToastPresentation BuildToastPresentation() =>
        new(_viewModel.NotificationPosition, _viewModel.NotificationOffsetX, _viewModel.NotificationOffsetY,
            _viewModel.NotificationSoundEnabled, _viewModel.NotificationSoundVolume,
            _viewModel.NotificationSound, _viewModel.NotificationSoundPath,
            _viewModel.CompactModeEnabled, _viewModel.AutoDetectActiveGameScreen);

    private void OnPlaytimeChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(
        DispatcherPriority.DataBind,
        new Action(() => _viewModel.RefreshPlaytime(DateTimeOffset.UtcNow)));

    private void OnUpdateRestartRequested(object? sender, EventArgs e) => Dispatcher.BeginInvoke(new Action(() =>
    {
        if (_allowClose) return;
        _allowClose = true;
        DisposeTrayIcon();
        Close();
        Application.Current.Shutdown();
    }));

    private void OnUpdateAvailable(string version)
    {
        if (!IsLoaded || _allowClose) return;
        var choice = MessageBox.Show(
            this,
            $"AchievementSync v{version} is available. Download and install it now? The app will restart after the download is verified.",
            "AchievementSync update available",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (choice == MessageBoxResult.Yes)
            _viewModel.InstallUpdateCommand.Execute(null);
    }

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
        _viewModel.PlatformSourceRootsChanged -= OnPlatformSourceRootsChanged;
        _viewModel.AchievementUnlocked -= OnAchievementUnlocked;
        _viewModel.GameSessionStateChanged -= OnGameSessionStateChanged;
        _viewModel.TestNotificationRequested -= OnTestNotificationRequested;
        _viewModel.UpdateRestartRequested -= OnUpdateRestartRequested;
        _viewModel.UpdateAvailable -= OnUpdateAvailable;
        if (_achievementFileMonitor is not null)
        {
            _achievementFileMonitor.SourcesChanged -= OnAchievementSourcesChanged;
            _achievementFileMonitor.Dispose();
            _achievementFileMonitor = null;
        }
        if (_platformSourceMonitor is not null)
        {
            _platformSourceMonitor.PlatformChanged -= OnPlatformSourceChanged;
            _platformSourceMonitor.Dispose();
            _platformSourceMonitor = null;
        }
        _lumaPlayRegistryMonitor?.Dispose();
        _lumaPlayRegistryMonitor = null;
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
        _platformSourceMonitor?.SetPaused(_monitoringPaused);
        _lumaPlayRegistryMonitor?.SetPaused(_monitoringPaused);
        _trayIcon?.SetPaused(_monitoringPaused);
    }));

    private async void Tray_ExitRequested(object? sender, EventArgs e)
    {
        if (_allowClose) return;
        _allowClose = true;
        DisposeTrayIcon();
        _viewModel.WatchSourcesChanged -= OnWatchSourcesChanged;
        _viewModel.PlatformSourceRootsChanged -= OnPlatformSourceRootsChanged;
        _viewModel.AchievementUnlocked -= OnAchievementUnlocked;
        _viewModel.GameSessionStateChanged -= OnGameSessionStateChanged;
        _viewModel.TestNotificationRequested -= OnTestNotificationRequested;
        _viewModel.UpdateRestartRequested -= OnUpdateRestartRequested;
        _viewModel.UpdateAvailable -= OnUpdateAvailable;
        if (_achievementFileMonitor is not null)
        {
            _achievementFileMonitor.SourcesChanged -= OnAchievementSourcesChanged;
            _achievementFileMonitor.Dispose();
            _achievementFileMonitor = null;
        }
        if (_platformSourceMonitor is not null)
        {
            _platformSourceMonitor.PlatformChanged -= OnPlatformSourceChanged;
            _platformSourceMonitor.Dispose();
            _platformSourceMonitor = null;
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
