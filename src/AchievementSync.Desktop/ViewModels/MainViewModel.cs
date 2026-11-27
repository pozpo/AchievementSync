using System.IO;
using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows;
using System.Diagnostics;
using System.Reflection;
using System.Net.Http;
using System.Text.Json;
using AchievementSync.Core.Models;
using AchievementSync.Core.Metadata;
using AchievementSync.Core.Parsing;
using AchievementSync.Core.Scanning;
using AchievementSync.Core.Scanning.PlatformAdapters;
using AchievementSync.Core.Settings;
using AchievementSync.Core.Synchronization;
using AchievementSync.Core.Tracking;
using AchievementSync.Core.Platforms;
using AchievementSync.Core.Enabler;
using AchievementSync.Desktop.Services;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace AchievementSync.Desktop.ViewModels;

public sealed record GameSessionEventArgs(GameCardViewModel Game, bool Started, TimeSpan SessionDuration, TimeSpan TotalPlaytime);

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly AchievementScanner _scanner = new();
    private readonly LocalAchievementMetadataCache _localMetadataCache;
    private readonly LocalAchievementCatalogCache _localCatalogCache;
    private readonly JsonAchievementParser _metadataProgressParser = new();
    private readonly PlatformAdapterRegistry _platformAdapters = new();
    private readonly SteamMetadataCache _metadataCache;
    private readonly SteamDbScreenshotCache _screenshotCache = new();
    private readonly SemaphoreSlim _metadataSlots = new(5, 5);
    private readonly SemaphoreSlim _viewerDataSlots = new(4, 4);
    private readonly HttpClient _viewerCatalogHttp = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly ViewerGameInfoClient _viewerGameInfoClient;
    private readonly ViewerAchievementProgressClient _viewerProgressClient;
    private readonly SteamCommunityCatalogClient _steamCommunityCatalogClient = new();
    private readonly SettingsStore _settingsStore = new();
    private readonly HttpClient _githubHttpClient = new() { Timeout = TimeSpan.FromSeconds(45) };
    private readonly GitHubDeviceFlowClient _githubDeviceFlow;
    private readonly GitHubAchievementSyncClient _githubSyncClient;
    private readonly GitHubReleaseUpdateService _releaseUpdateService = new();
    private readonly WindowsCredentialTokenStore _githubTokenStore = new();
    private readonly WindowsCredentialTokenStore _retroAchievementsKeyStore = new("AchievementSync/Platforms/RetroAchievementsApiKey");
    private readonly WindowsCredentialTokenStore _xboxCredentialStore = new("AchievementSync/Platforms/XboxCredential");
    private readonly WindowsCredentialTokenStore _epicCredentialStore = new("AchievementSync/Platforms/EpicCredential");
    private readonly GameplaySessionTracker _gameplayTracker;
    private readonly AchievementUnlockTracker _unlockTracker;
    private readonly IStartupRegistration _startupRegistration;
    private readonly SemaphoreSlim _changedSourceGate = new(1, 1);
    private readonly SemaphoreSlim _saveBackupGate = new(1, 1);
    private readonly SemaphoreSlim _platformScanGate = new(1, 1);
    private readonly Dictionary<string, bool> _gameRunningStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _gameSessionStartedAt = new(StringComparer.Ordinal);
    private bool _playtimeBaselineInitialized;
    private IReadOnlyList<DiscoveredAchievementFile> _sourceFiles = [];
    private readonly bool _enableDiagnostics;
    private AppSettings _settings;
    private string _searchText = string.Empty;
    private string _scanStatus = "Loading your local achievement library";
    private string _lastScanText = "Not scanned yet";
    private string _currentPage = "Games";
    private bool _isScanning;
    private bool _scanRequestedDuringRun;
    private bool _isLibraryEmpty = true;
    private bool _isGameResultsEmpty = true;
    private int _totalGames;
    private int _totalKnownAchievements;
    private int _totalUnlocked;
    private string _completionText = "—";
    private string _completionCoverageText = "No games have measurable completion yet";
    private string _achievementTotalText = "—";
    private string _measurableGamesText = "0 games with measurable progress";
    private int _completedGames;
    private string _issueText = string.Empty;
    private string _previewStatus = "Connect GitHub, then build a preview. Nothing has been uploaded.";
    private ExportPreviewFileViewModel? _selectedExport;
    private string _githubStatus = string.Empty;
    private bool _isGitHubSignedIn;
    private GitHubSyncPreview? _githubPreview;
    private readonly object _autoSyncGate = new();
    private readonly HashSet<string> _autoSyncPending = new(StringComparer.Ordinal);
    private bool _autoSyncRunning;
    private bool _isSignInRunning;
    private bool _isPushPreviewRunning;
    private bool _isPushRunning;
    private bool _isSyncAllRunning;
    private bool _isBackupRunning;
    private bool _isRestoreRunning;
    private int _backupFilesProcessed;
    private int _backupFilesTotal;
    private long _backupBytesProcessed;
    private long _backupBytesTotal;
    private string _backupStage = string.Empty;
    private string _bulkSyncStatus = "Sync all pushes only every game's achievements.json (AppID/<id>/achievements.json) in one commit, then reports each file.";
    private string _autoSyncStatus = "Auto-sync is off. Enable it to push AppID files on genuine unlocks only.";
    private string _backupStatus = "Manual save backups require confirmation. Automatic backups are off.";
    private string _restoreStatus = "Restore save files and achievements from your repository. This overwrites local files only after you confirm.";
    private string _selectedGameFilter = "All games";
    private string _selectedGameSort = "Name";
    private string _startupStatus = "Windows startup is off.";
    private string _updateStatus = "AchievementSync checks GitHub releases when it starts.";
    private GitHubAppReleaseUpdate? _availableUpdate;
    private bool _isCheckingForUpdates;
    private bool _isDownloadingUpdate;
    private RelayCommand? _browseNotificationSoundCommand;
    private System.Windows.Media.Imaging.BitmapImage? _githubAvatarImage;

    // Platforms
    private readonly PlatformScanService _platformScanService = new();
    private readonly AchievementEnablerService _enablerService;
    internal AchievementEnablerService EnablerService => _enablerService;
    private PlatformSourceOptions _platformSourceOptions = PlatformSourceOptions.CreateDefault();
    private bool _isPlatformScanRunning;
    private string _platformScanStatus = "Platform scan not started.";
    private IReadOnlyList<PlatformRunStatus> _platformStatuses = [];
    private string _platformCredentialStatus = "No platform API credentials saved.";
    private static readonly (string Key, string Label)[] PlatformRootDefinitions =
    [
        ("steam", "Steam"), ("xblen", "XBLessness"), ("xboxpc", "Xbox PC"),
        ("xbox360", "Xenia / Xbox 360"), ("gfwl", "GFWL"), ("rpcs3", "RPCS3 / PS3"),
        ("ps4", "shadPS4"), ("ps5", "Kyty PS5"), ("epic", "Epic Games Store"),
        ("gog", "GOG Galaxy"), ("uplay", "Ubisoft Connect"),
        ("uplaycache", "Ubisoft Connect Cache"), ("uplayrune", "Ubisoft RUNE Saves"),
        ("ea", "EA Desktop Logs"), ("recomp", "Recomp Ports (PS3/PS4)"), ("ladxhd", "LADXHD Recomp")
    ];

    public MainViewModel() : this(null, null, readStoredCredential: true, enableDiagnostics: true)
    {
    }

    public MainViewModel(
        SteamMetadataCache? metadataCache,
        AppSettings? settings,
        bool readStoredCredential,
        bool enableDiagnostics,
        GameplaySessionTracker? gameplayTracker = null,
        AchievementUnlockTracker? unlockTracker = null,
        IStartupRegistration? startupRegistration = null)
    {
        _enableDiagnostics = enableDiagnostics;
        _settings = settings ?? _settingsStore.Load();
        if (_settings.AutoBackupSavesAfterPlaytime)
            _backupStatus = "Automatic save backups are enabled. New and changed saves upload after a tracked game session ends.";
        MigrateLegacyPlatformCredentials();
        _platformSourceOptions = BuildPlatformSourceOptions();
        _enablerService = new AchievementEnablerService(
            string.IsNullOrWhiteSpace(_settings.EnablerAdaptersRoot) ? null : _settings.EnablerAdaptersRoot,
            MetadataRoot);
        _gameplayTracker = gameplayTracker ?? new GameplaySessionTracker();
        _unlockTracker = unlockTracker ?? new AchievementUnlockTracker();
        _startupRegistration = startupRegistration ?? new WindowsStartupRegistration();
        StartupStatus = _settings.StartWithWindows
            ? "AchievementSync will start minimized to the tray when you sign in."
            : "Windows startup is off.";
        if (_gameplayTracker.StorageIssue is { } playtimeStorageIssue && _enableDiagnostics)
        {
            AppLog.Warning($"Gameplay time data could not be loaded and will be kept untouched: {playtimeStorageIssue}.");
        }
        if (_unlockTracker.StorageIssue is { } unlockStorageIssue && _enableDiagnostics)
            AppLog.Warning($"Achievement notification history could not be loaded and will be kept untouched: {unlockStorageIssue}.");
        _metadataCache = metadataCache ?? new SteamMetadataCache();
        _localMetadataCache = new LocalAchievementMetadataCache(null, _platformAdapters);
        _localCatalogCache = new LocalAchievementCatalogCache(MetadataRoot, _settings.EnablerAdaptersRoot);

        _platformAdapters.Register(new SteamDataAdapter(_platformSourceOptions));

        // Register platform data adapters for the dedicated Platforms page
        _platformScanService.Register(new SteamDataAdapter(_platformSourceOptions));
        _platformScanService.Register(new XboxDataAdapter("xbox", "Xbox / Xbox PC (XBLessness)", includeGfwl: false));
        _platformScanService.Register(new XboxPcDataAdapter(_platformSourceOptions, _viewerCatalogHttp));
        _platformScanService.Register(new GfwlDataAdapter());
        _platformScanService.Register(new Xbox360DataAdapter());
        _platformScanService.Register(new PlayStationDataAdapter());
        _platformScanService.Register(new GogDataAdapter(_platformSourceOptions));
        _platformScanService.Register(new EpicDataAdapter(_platformSourceOptions, _viewerCatalogHttp));
        _platformScanService.Register(new UplayDataAdapter(_platformSourceOptions));
        _platformScanService.Register(new EaDesktopDataAdapter(_platformSourceOptions));
        _platformScanService.Register(new RecompDataAdapter(_platformSourceOptions));
        _platformScanService.Register(new RetroAchievementsDataAdapter(_viewerCatalogHttp));

        PlatformStatuses = _platformScanService.Adapters.Select(adapter =>
        {
            var configured = adapter.IsConfiguredFor(_platformSourceOptions);
            return new PlatformRunStatus(adapter.Platform, adapter.DisplayName, configured, adapter.ConfigurationHint,
                0, configured ? "Ready to scan" : "Not configured");
        }).ToArray();

        PlatformSourceEntries = new ObservableCollection<PlatformSourceRootViewModel>(
            PlatformRootDefinitions.Select(definition => new PlatformSourceRootViewModel(
                definition.Key, definition.Label, GetPlatformRoot(_platformSourceOptions, definition.Key), UpdatePlatformSourceRoot)));
        UpdatePlatformCredentialStatus();

        _viewerGameInfoClient = new ViewerGameInfoClient(_viewerCatalogHttp, cacheDirectory: Path.Combine(MetadataRoot, "ViewerCatalog"));
        _viewerProgressClient = new ViewerAchievementProgressClient(_viewerCatalogHttp, cacheDirectory: Path.Combine(MetadataRoot, "ViewerProgress"));
        _githubDeviceFlow = new GitHubDeviceFlowClient(_githubHttpClient);
        _githubSyncClient = new GitHubAchievementSyncClient(_githubHttpClient);
        Locations = new ObservableCollection<LocationOptionViewModel>(
            LocationCatalog.Build(_settings).Select(location => new LocationOptionViewModel(location, OnLocationChanged)));
        Games = new ObservableCollection<GameCardViewModel>();
        GamesView = CollectionViewSource.GetDefaultView(Games);
        GamesView.Filter = FilterGame;
        NavigationCommand = new RelayCommand(parameter => CurrentPage = parameter?.ToString() ?? "Games");
        RemoveLocationCommand = new RelayCommand(parameter => RemoveLocation(parameter as LocationOptionViewModel));
        SignInToGitHubCommand = new AsyncCommand(SignInToGitHubAsync, () => CanStartGitHubSignIn);
        DisconnectGitHubCommand = new RelayCommand(_ => DisconnectGitHub());
        BuildGitHubPreviewCommand = new AsyncCommand(BuildGitHubPreviewAsync, () => CanBuildGitHubPreview);
        PushGitHubPreviewCommand = new AsyncCommand(() => PushGitHubPreviewAsync(), () => CanPushGitHubPreview);
        TestNotificationCommand = new RelayCommand(_ => TestNotificationRequested?.Invoke(this, EventArgs.Empty));
        PreviewNotificationSoundCommand = new RelayCommand(_ => UnlockSoundPlayer.TryPlay(true, _settings.NotificationSoundVolume, _settings.NotificationSoundKind, _settings.NotificationSoundPath));
        SyncAllMetadataCommand = new AsyncCommand(SyncAllMetadataAsync, () => CanSyncAll);
        BackupSavesCommand = new AsyncCommand(BackupSavesAsync, () => CanBackup);
        RestoreSavesCommand = new AsyncCommand(RestoreSavesAsync, () => CanRestore);
        CheckForUpdatesCommand = new AsyncCommand(CheckForUpdatesAsync, () => CanCheckForUpdates);
        InstallUpdateCommand = new AsyncCommand(DownloadAndInstallUpdateAsync, () => CanInstallUpdate);
        ScanPlatformCommand = new AsyncParameterCommand(
            parameter => ScanPlatformAsync(parameter?.ToString() ?? string.Empty),
            _ => CanScanPlatform);
        ClearBulkSyncReportCommand = new RelayCommand(_ => ClearBulkSyncReport());
        OpenMappingReviewCommand = new AsyncCommand(OpenMappingReviewAsync);
        OpenEnablerWizardCommand = new RelayCommand(_ => OpenEnablerWizard());
        OpenGameCommand = new RelayCommand(parameter =>
        {
            if (parameter is GameCardViewModel game)
            {
                GameRequested?.Invoke(this, game);
            }
        });

        try
        {
            _isGitHubSignedIn = readStoredCredential && GitHubStoredCredential.Parse(_githubTokenStore.Read()) is not null;
        }
        catch (Exception exception)
        {
            if (_enableDiagnostics) AppLog.Warning($"Could not read GitHub sign-in status from Windows Credential Manager: {exception.GetType().Name}.");
        }
        _githubStatus = _isGitHubSignedIn
            ? $"Connected as {_settings.GitHubLogin}. Preview changes before every push."
            : !HasGitHubOAuthClientId
                ? "OAuth sign-in is not configured in this build. Connect with a classic token that has public_repo access."
                : "Connect your GitHub account and select your public achievement-viewer repository.";
        RefreshAutoSyncStatus();
        RefreshGitHubAvatar();
        if (_isGitHubSignedIn && (string.IsNullOrWhiteSpace(_settings.GitHubDisplayName) || string.IsNullOrWhiteSpace(_settings.GitHubAvatarUrl)))
            _ = RefreshGitHubAccountDetailsAsync();
    }

    private void RefreshGitHubAvatar()
    {
        var url = IsGitHubSignedIn ? _settings.GitHubAvatarUrl : string.Empty;
        if (string.IsNullOrWhiteSpace(url)) { GitHubAvatarImage = null; return; }
        _ = LoadGitHubAvatarAsync(url);
    }

    private async Task LoadGitHubAvatarAsync(string url)
    {
        try
        {
            var image = await AchievementArtworkCache.GetAsync(url).ConfigureAwait(false);
            if (image is null || !IsGitHubSignedIn || !string.Equals(_settings.GitHubAvatarUrl, url, StringComparison.Ordinal)) return;
            await Application.Current!.Dispatcher.InvokeAsync(() => GitHubAvatarImage = image);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or NotSupportedException or ArgumentException or UnauthorizedAccessException)
        {
            AppLog.Warning($"The GitHub avatar could not be loaded: {exception.GetType().Name}.");
        }
    }

    private async Task RefreshGitHubAccountDetailsAsync()
    {
        var token = await ReadGitHubTokenAsync().ConfigureAwait(false);
        if (token is null) return;
        try
        {
            var account = await _githubSyncClient.GetCurrentUserAsync(token).ConfigureAwait(false);
            await Application.Current!.Dispatcher.InvokeAsync(() =>
            {
                _settings.GitHubDisplayName = string.IsNullOrWhiteSpace(account.DisplayName) ? account.Login : account.DisplayName!;
                _settings.GitHubAvatarUrl = account.AvatarUrl ?? string.Empty;
                SaveSettings();
                OnPropertyChanged(nameof(GitHubDisplayName));
            });
            RefreshGitHubAvatar();
        }
        catch (Exception exception) when (exception is GitHubApiException or GitHubAuthorizationException or HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            AppLog.Warning($"The GitHub account details could not be refreshed: {exception.GetType().Name}.");
        }
    }

    public ObservableCollection<LocationOptionViewModel> Locations { get; }
    public ObservableCollection<GameCardViewModel> Games { get; }
    public ObservableCollection<ExportPreviewFileViewModel> ExportFiles { get; } = [];
    public ICollectionView GamesView { get; }
    public ICommand NavigationCommand { get; }
    public ICommand RemoveLocationCommand { get; }
    public ICommand SignInToGitHubCommand { get; }
    public ICommand DisconnectGitHubCommand { get; }
    public ICommand BuildGitHubPreviewCommand { get; }
    public ICommand PushGitHubPreviewCommand { get; }
    public ICommand OpenGameCommand { get; }
    public ICommand TestNotificationCommand { get; }
    public ICommand PreviewNotificationSoundCommand { get; }
    public ICommand SyncAllMetadataCommand { get; }
    public ICommand BackupSavesCommand { get; }
    public ICommand RestoreSavesCommand { get; }
    public ICommand ScanPlatformCommand { get; }
    public ICommand CheckForUpdatesCommand { get; }
    public ICommand InstallUpdateCommand { get; }
    public ICommand ClearBulkSyncReportCommand { get; }
    public ICommand OpenMappingReviewCommand { get; }
    public string XboxClientId
    {
        get => _settings.XboxClientId;
        set
        {
            if (_settings.XboxClientId == value) return;
            _settings.XboxClientId = value ?? string.Empty;
            SaveSettings();
            OnPropertyChanged(nameof(XboxClientId));
            _platformSourceOptions = BuildPlatformSourceOptions();
        }
    }
    public string EpicClientId
    {
        get => _settings.EpicClientId;
        set
        {
            if (_settings.EpicClientId == value) return;
            _settings.EpicClientId = value ?? string.Empty;
            SaveSettings();
            OnPropertyChanged(nameof(EpicClientId));
            _platformSourceOptions = BuildPlatformSourceOptions();
        }
    }
    public ObservableCollection<string> BulkSyncResults { get; } = [];
    public ObservableCollection<string> BackupResults { get; } = [];
    public ObservableCollection<string> RestoreResults { get; } = [];
    public string BulkSyncStatus { get => _bulkSyncStatus; private set => Set(ref _bulkSyncStatus, value); }
    public string AutoSyncStatus { get => _autoSyncStatus; private set => Set(ref _autoSyncStatus, value); }
    public string BackupStatus { get => _backupStatus; private set => Set(ref _backupStatus, value); }
    public string RestoreStatus { get => _restoreStatus; private set => Set(ref _restoreStatus, value); }
    public string UpdateStatus { get => _updateStatus; private set => Set(ref _updateStatus, value); }
    public string AvailableUpdateVersion => _availableUpdate is null ? string.Empty : $"Download & install v{_availableUpdate.VersionText}";
    public bool HasAvailableUpdate => _availableUpdate is not null;
    public bool IsCheckingForUpdates
    {
        get => _isCheckingForUpdates;
        private set
        {
            if (!Set(ref _isCheckingForUpdates, value)) return;
            OnPropertyChanged(nameof(CanCheckForUpdates));
            CommandManager.InvalidateRequerySuggested();
        }
    }
    public bool IsDownloadingUpdate
    {
        get => _isDownloadingUpdate;
        private set
        {
            if (!Set(ref _isDownloadingUpdate, value)) return;
            OnPropertyChanged(nameof(CanCheckForUpdates));
            OnPropertyChanged(nameof(CanInstallUpdate));
            CommandManager.InvalidateRequerySuggested();
        }
    }
    public bool CanCheckForUpdates => !IsCheckingForUpdates && !IsDownloadingUpdate;
    public bool CanInstallUpdate => HasAvailableUpdate && !IsDownloadingUpdate && IsPackagedExecutable;
    public event EventHandler? UpdateRestartRequested;
    public event Action<string>? UpdateAvailable;

    private static bool IsPackagedExecutable => string.Equals(
        Path.GetFileName(Environment.ProcessPath), "AchievementSync.exe", StringComparison.OrdinalIgnoreCase);

    private static Version CurrentApplicationVersion
    {
        get
        {
            var informational = typeof(MainViewModel).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            var versionText = informational?.Split('+', 2)[0].TrimStart('v', 'V');
            return Version.TryParse(versionText, out var parsed) ? parsed : new Version(1, 2, 0);
        }
    }
    public bool HasBackupReport => BackupResults.Count > 0;
    public bool HasRestoreReport => RestoreResults.Count > 0;
    public bool HasBulkSyncReport => BulkSyncResults.Count > 0;

    private void RefreshAutoSyncStatus()
    {
        if (!AutoSyncAfterUnlock)
            AutoSyncStatus = "Automatic sync is off.";
        else if (!IsGitHubSignedIn)
            AutoSyncStatus = "Automatic sync is saved, but GitHub must be connected before it can run.";
        else
            AutoSyncStatus = "New-unlock sync is on. It pushes AppID files after a genuine unlock.";
    }

    public bool CanSyncAll => IsGitHubSignedIn && !_isSyncAllRunning && !_isBackupRunning && !_isRestoreRunning && !_isPushRunning && !_isPushPreviewRunning;
    public bool CanBackup => IsGitHubSignedIn && !_isBackupRunning && !_isRestoreRunning && !_isSyncAllRunning && !_isPushRunning && !_isPushPreviewRunning;
    public bool CanRestore => IsGitHubSignedIn && !_isRestoreRunning && !_isBackupRunning && !_isSyncAllRunning && !_isPushRunning && !_isPushPreviewRunning;
    public GameplaySessionTracker GameplayTracker => _gameplayTracker;
    public IReadOnlyList<DiscoveredAchievementFile> WatchableSourceFiles => _sourceFiles;
    public IReadOnlyList<(string Key, string Path)> WatchablePlatformRoots => PlatformSourceEntries
        .Where(item => !string.IsNullOrWhiteSpace(item.Path))
        .Select(item => (item.Key, Path: Environment.ExpandEnvironmentVariables(item.Path)))
        .ToArray();

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<object?, GameCardViewModel>? GameRequested;
    public event EventHandler? ProcessTrackingConfigurationChanged;
    public event Action<object?, AchievementUnlockNotification>? AchievementUnlocked;
    public event Action<object?, GameSessionEventArgs>? GameSessionStateChanged;
    public event Action<object?, IReadOnlyList<DiscoveredAchievementFile>>? WatchSourcesChanged;
    public event EventHandler? PlatformSourceRootsChanged;
    public event EventHandler? TestNotificationRequested;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText == value) return;
            _searchText = value;
            OnPropertyChanged();
            RefreshGamesView();
        }
    }

    public string ScanStatus { get => _scanStatus; private set => Set(ref _scanStatus, value); }
    public string LastScanText { get => _lastScanText; private set => Set(ref _lastScanText, value); }
    public string CurrentPage
    {
        get => _currentPage;
        private set
        {
            if (!Set(ref _currentPage, value)) return;
            AppLog.Action($"Opened the {value} page.");
            OnPropertyChanged(nameof(IsGamesVisible));
            OnPropertyChanged(nameof(IsSettingsVisible));
            OnPropertyChanged(nameof(IsActivityVisible));
            OnPropertyChanged(nameof(IsPlatformsVisible));
            OnPropertyChanged(nameof(IsEnablerVisible));
        }
    }

    public bool IsEnablerVisible => CurrentPage == "Enabler";
    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (!Set(ref _isScanning, value)) return;
            CommandManager.InvalidateRequerySuggested();
            OnPropertyChanged(nameof(GameResultsEmptyText));
            OnPropertyChanged(nameof(GameResultsEmptyHint));
        }
    }
    public bool IsGamesVisible => CurrentPage == "Games";
    public bool IsSettingsVisible => CurrentPage == "Settings";
    public bool IsActivityVisible => CurrentPage == "Activity";
    public bool AchievementNotificationsEnabled
    {
        get => _settings.AchievementNotificationsEnabled;
        set
        {
            if (_settings.AchievementNotificationsEnabled == value) return;
            _settings.AchievementNotificationsEnabled = value;
            SaveSettings();
            OnPropertyChanged();
        }
    }

    public bool PlaytimeNotificationsEnabled
    {
        get => _settings.PlaytimeNotificationsEnabled;
        set
        {
            if (_settings.PlaytimeNotificationsEnabled == value) return;
            _settings.PlaytimeNotificationsEnabled = value;
            SaveSettings();
            OnPropertyChanged();
        }
    }

    public bool CompactModeEnabled
    {
        get => _settings.CompactModeEnabled;
        set
        {
            if (_settings.CompactModeEnabled == value) return;
            _settings.CompactModeEnabled = value;
            SaveSettings();
            OnPropertyChanged();
        }
    }

    public bool AutoDetectActiveGameScreen
    {
        get => _settings.AutoDetectActiveGameScreen;
        set
        {
            if (_settings.AutoDetectActiveGameScreen == value) return;
            _settings.AutoDetectActiveGameScreen = value;
            SaveSettings();
            OnPropertyChanged();
        }
    }
    public IReadOnlyList<NotificationPositionPreset> NotificationPositionOptions => NotificationPlacement.Presets;
    public string NotificationPosition
    {
        get => _settings.NotificationPosition;
        set
        {
            if (string.Equals(_settings.NotificationPosition, value, StringComparison.OrdinalIgnoreCase)) return;
            if (!NotificationPositionOptions.Any(option => string.Equals(option.Code, value, StringComparison.OrdinalIgnoreCase))) return;
            _settings.NotificationPosition = NotificationPositionOptions.First(option => string.Equals(option.Code, value, StringComparison.OrdinalIgnoreCase)).Code;
            SaveSettings();
            OnPropertyChanged();
        }
    }
    public int NotificationOffsetX
    {
        get => _settings.NotificationOffsetX;
        set
        {
            var clamped = Math.Clamp(value, -400, 400);
            if (_settings.NotificationOffsetX == clamped) return;
            _settings.NotificationOffsetX = clamped;
            SaveSettings();
            OnPropertyChanged();
        }
    }
    public int NotificationOffsetY
    {
        get => _settings.NotificationOffsetY;
        set
        {
            var clamped = Math.Clamp(value, -400, 400);
            if (_settings.NotificationOffsetY == clamped) return;
            _settings.NotificationOffsetY = clamped;
            SaveSettings();
            OnPropertyChanged();
        }
    }
    public bool NotificationSoundEnabled
    {
        get => _settings.NotificationSoundEnabled;
        set
        {
            if (_settings.NotificationSoundEnabled == value) return;
            _settings.NotificationSoundEnabled = value;
            SaveSettings();
            OnPropertyChanged();
        }
    }
    public int NotificationSoundVolume
    {
        get => _settings.NotificationSoundVolume;
        set
        {
            var clamped = Math.Clamp(value, 0, 100);
            if (_settings.NotificationSoundVolume == clamped) return;
            _settings.NotificationSoundVolume = clamped;
            SaveSettings();
            OnPropertyChanged();
        }
    }
    public IReadOnlyList<NotificationSoundOption> NotificationSoundOptions { get; } =
    [
        new(Services.UnlockSoundPlayer.AchievementSync, "Achievement unlock chime"),
        new(Services.UnlockSoundPlayer.SoftTick, "Soft tick"),
        new(Services.UnlockSoundPlayer.VictoryArpeggio, "Victory arpeggio"),
        new(Services.UnlockSoundPlayer.Custom, "Custom .wav file…"),
    ];
    public string NotificationSound
    {
        get => _settings.NotificationSoundKind;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || !NotificationSoundOptions.Any(option => string.Equals(option.Code, value, StringComparison.OrdinalIgnoreCase))) return;
            var normalized = NotificationSoundOptions.First(option => string.Equals(option.Code, value, StringComparison.OrdinalIgnoreCase)).Code;
            if (string.Equals(_settings.NotificationSoundKind, normalized, StringComparison.OrdinalIgnoreCase)) return;
            _settings.NotificationSoundKind = normalized;
            SaveSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanBrowseNotificationSound));
            OnPropertyChanged(nameof(NotificationSoundPath));
            OnPropertyChanged(nameof(NotificationSoundCustomPath));
        }
    }
    public bool CanBrowseNotificationSound => string.Equals(_settings.NotificationSoundKind, Services.UnlockSoundPlayer.Custom, StringComparison.OrdinalIgnoreCase);
    public string NotificationSoundPath
    {
        get => _settings.NotificationSoundPath;
        set
        {
            var cleaned = value ?? string.Empty;
            if (string.Equals(_settings.NotificationSoundPath, cleaned, StringComparison.Ordinal)) return;
            _settings.NotificationSoundPath = cleaned;
            SaveSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(NotificationSoundCustomPath));
        }
    }
    public string NotificationSoundCustomPath => string.IsNullOrWhiteSpace(_settings.NotificationSoundPath) ? "No custom file selected" : _settings.NotificationSoundPath;
    public ICommand BrowseNotificationSoundCommand => _browseNotificationSoundCommand ??= new RelayCommand(_ => BrowseNotificationSound());

    private void BrowseNotificationSound()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose an achievement sound",
            Filter = "Wave audio (*.wav)|*.wav",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog() != true) return;
        NotificationSoundPath = dialog.FileName;
        NotificationSound = Services.UnlockSoundPlayer.Custom;
        if (!CanBrowseNotificationSound) OnPropertyChanged(nameof(CanBrowseNotificationSound));
    }

    public bool AutoSyncAfterUnlock
    {
        get => _settings.AutoSyncAfterUnlock;
        set
        {
            if (_settings.AutoSyncAfterUnlock == value) return;
            _settings.AutoSyncAfterUnlock = value;
            SaveSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(AutoSyncStatusVisibility));
            RefreshAutoSyncStatus();
        }
    }
    public bool AutoBackupSavesAfterPlaytime
    {
        get => _settings.AutoBackupSavesAfterPlaytime;
        set
        {
            if (_settings.AutoBackupSavesAfterPlaytime == value) return;
            _settings.AutoBackupSavesAfterPlaytime = value;
            SaveSettings();
            OnPropertyChanged();
            BackupStatus = value
                ? "Automatic save backups are enabled. New and changed saves upload to GitHub after a tracked game session ends."
                : "Automatic save backups are off.";
        }
    }
    public Visibility AutoSyncStatusVisibility => AutoSyncAfterUnlock ? Visibility.Visible : Visibility.Collapsed;

    public bool IsPlatformsVisible => CurrentPage == "Platforms";

    public PlatformSourceOptions PlatformSourceOptions
    {
        get => _platformSourceOptions;
        private set
        {
            if (Set(ref _platformSourceOptions, value))
            {
                OnPropertyChanged(nameof(PlatformSourceOptions));
            }
        }
    }

    public ObservableCollection<PlatformSourceRootViewModel> PlatformSourceEntries { get; }
    public string PlatformCredentialStatus { get => _platformCredentialStatus; private set => Set(ref _platformCredentialStatus, value); }
    public string RetroAchievementsUser
    {
        get => _settings.RetroAchievementsUser;
        set
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (string.Equals(_settings.RetroAchievementsUser, normalized, StringComparison.Ordinal)) return;
            _settings.RetroAchievementsUser = normalized;
            SaveSettings();
            OnPropertyChanged();
        }
    }

    public string PlatformScanStatus { get => _platformScanStatus; private set => Set(ref _platformScanStatus, value); }
    public bool IsPlatformScanRunning { get => _isPlatformScanRunning; private set { if (Set(ref _isPlatformScanRunning, value)) { OnPropertyChanged(nameof(CanScanPlatform)); CommandManager.InvalidateRequerySuggested(); } } }
    public bool CanScanPlatform => !IsPlatformScanRunning;
    public IReadOnlyList<PlatformRunStatus> PlatformStatuses { get => _platformStatuses; private set => Set(ref _platformStatuses, value); }

    public ICommand OpenEnablerWizardCommand { get; }

    public bool StartWithWindows
    {
        get => _settings.StartWithWindows;
        set
        {
            if (_settings.StartWithWindows == value) return;
            try
            {
                _startupRegistration.SetEnabled(value);
                _settings.StartWithWindows = value;
                StartupStatus = value
                    ? "AchievementSync will start minimized to the tray when you sign in."
                    : "Windows startup is off.";
                SaveSettings();
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException or IOException)
            {
                StartupStatus = "The Windows startup setting could not be changed. The existing startup entry was left unchanged.";
                if (_enableDiagnostics) AppLog.Warning($"Windows startup registration could not be changed: {exception.GetType().Name}.");
            }
            OnPropertyChanged();
        }
    }
    public string StartupStatus { get => _startupStatus; private set => Set(ref _startupStatus, value); }

    public void RepairStartupRegistration()
    {
        if (!_settings.StartWithWindows)
        {
            StartupStatus = "Windows startup is off.";
            return;
        }

        try
        {
            _startupRegistration.SetEnabled(true);
            StartupStatus = "Windows startup is enabled for this executable location.";
        }
        catch (Exception exception)
        {
            StartupStatus = "Windows could not refresh the startup entry. Open the app from this folder and enable startup again in Settings.";
            if (_enableDiagnostics)
                AppLog.Warning($"Could not refresh the Windows startup registration: {exception.GetType().Name}.");
        }
    }
    public bool HasGitHubOAuthClientId => !string.IsNullOrWhiteSpace(GitHubOAuthConfiguration.ClientId);
    public bool IsGitHubSignedIn { get => _isGitHubSignedIn; private set { if (Set(ref _isGitHubSignedIn, value)) { OnPropertyChanged(nameof(CanStartGitHubSignIn)); OnPropertyChanged(nameof(CanBuildGitHubPreview)); OnPropertyChanged(nameof(CanSyncAll)); OnPropertyChanged(nameof(CanBackup)); OnPropertyChanged(nameof(CanRestore)); OnPropertyChanged(nameof(GitHubAccountText)); OnPropertyChanged(nameof(GitHubLogin)); OnPropertyChanged(nameof(GitHubDisplayName)); OnPropertyChanged(nameof(GitHubAccountVisibility)); OnPropertyChanged(nameof(GitHubAccountHasAvatar)); OnPropertyChanged(nameof(AutoSyncStatusVisibility)); RefreshAutoSyncStatus(); CommandManager.InvalidateRequerySuggested(); } } }

    public bool IsSignInRunning { get => _isSignInRunning; private set { if (Set(ref _isSignInRunning, value)) { OnPropertyChanged(nameof(CanStartGitHubSignIn)); OnPropertyChanged(nameof(CanBuildGitHubPreview)); OnPropertyChanged(nameof(CanPushGitHubPreview)); CommandManager.InvalidateRequerySuggested(); } } }
    public bool IsPushPreviewRunning { get => _isPushPreviewRunning; private set { if (Set(ref _isPushPreviewRunning, value)) { OnPropertyChanged(nameof(CanSyncAll)); OnPropertyChanged(nameof(CanBackup)); OnPropertyChanged(nameof(CanRestore)); OnPropertyChanged(nameof(CanBuildGitHubPreview)); OnPropertyChanged(nameof(CanPushGitHubPreview)); OnPropertyChanged(nameof(CanStartGitHubSignIn)); CommandManager.InvalidateRequerySuggested(); } } }
    public bool IsPushRunning { get => _isPushRunning; private set { if (Set(ref _isPushRunning, value)) { OnPropertyChanged(nameof(CanSyncAll)); OnPropertyChanged(nameof(CanBackup)); OnPropertyChanged(nameof(CanRestore)); OnPropertyChanged(nameof(CanBuildGitHubPreview)); OnPropertyChanged(nameof(CanPushGitHubPreview)); CommandManager.InvalidateRequerySuggested(); } } }
    public bool IsSyncAllRunning { get => _isSyncAllRunning; private set { if (Set(ref _isSyncAllRunning, value)) { OnPropertyChanged(nameof(CanSyncAll)); OnPropertyChanged(nameof(CanBackup)); OnPropertyChanged(nameof(CanRestore)); OnPropertyChanged(nameof(CanBuildGitHubPreview)); OnPropertyChanged(nameof(CanPushGitHubPreview)); CommandManager.InvalidateRequerySuggested(); } } }
    public bool IsBackupRunning { get => _isBackupRunning; private set { if (Set(ref _isBackupRunning, value)) { OnPropertyChanged(nameof(CanBackup)); OnPropertyChanged(nameof(CanSyncAll)); OnPropertyChanged(nameof(CanRestore)); OnPropertyChanged(nameof(CanBuildGitHubPreview)); OnPropertyChanged(nameof(CanPushGitHubPreview)); OnPropertyChanged(nameof(IsBackupRunningReport)); CommandManager.InvalidateRequerySuggested(); } } }
    public bool IsRestoreRunning { get => _isRestoreRunning; private set { if (Set(ref _isRestoreRunning, value)) { OnPropertyChanged(nameof(CanRestore)); OnPropertyChanged(nameof(CanSyncAll)); OnPropertyChanged(nameof(CanBackup)); OnPropertyChanged(nameof(CanBuildGitHubPreview)); OnPropertyChanged(nameof(CanPushGitHubPreview)); CommandManager.InvalidateRequerySuggested(); } } }

    public bool IsBackupRunningReport => _isBackupRunning;
    public string BackupStage { get => _backupStage; private set => Set(ref _backupStage, value); }
    public int BackupFilesProcessed => _backupFilesProcessed;
    public int BackupFilesTotal => _backupFilesTotal;
    public long BackupBytesProcessed => _backupBytesProcessed;
    public long BackupBytesTotal => _backupBytesTotal;
    public int BackupProgressPercent
    {
        get
        {
            if (_backupFilesTotal <= 0 && _backupBytesTotal <= 0) return 0;
            if (_backupBytesTotal > 0) return (int)Math.Clamp((_backupBytesProcessed * 100) / _backupBytesTotal, 0, 100);
            return (int)Math.Clamp((_backupFilesProcessed * 100) / Math.Max(1, _backupFilesTotal), 0, 100);
        }
    }
    public double BackupRemainingBytesText => 0; // placeholder no-op for backward compat if bound

    public bool CanStartGitHubSignIn => HasGitHubOAuthClientId && !_isSignInRunning && !IsGitHubSignedIn;
    public bool CanBuildGitHubPreview => IsGitHubSignedIn && !_isSignInRunning && !_isPushPreviewRunning && !_isPushRunning && !_isSyncAllRunning && !_isBackupRunning && !_isRestoreRunning && !string.IsNullOrWhiteSpace(GitHubOwner) && !string.IsNullOrWhiteSpace(GitHubRepository) && !string.IsNullOrWhiteSpace(GitHubBranch);
    public bool CanPushGitHubPreview => _githubPreview is { Changes.Count: > 0 } && IsGitHubSignedIn && !_isPushRunning && !_isPushPreviewRunning && !_isSyncAllRunning && !_isBackupRunning && !_isRestoreRunning;
    public string GitHubStatus { get => _githubStatus; private set => Set(ref _githubStatus, value); }
    public string GitHubAccountText => IsGitHubSignedIn ? $"Connected as {_settings.GitHubLogin}" : "GitHub account not connected";
    public string GitHubLogin => IsGitHubSignedIn ? _settings.GitHubLogin : string.Empty;
    public string GitHubDisplayName => IsGitHubSignedIn
        ? (string.IsNullOrWhiteSpace(_settings.GitHubDisplayName) ? _settings.GitHubLogin : _settings.GitHubDisplayName)
        : string.Empty;
    public Visibility GitHubAccountVisibility => IsGitHubSignedIn ? Visibility.Visible : Visibility.Collapsed;
    public System.Windows.Media.Imaging.BitmapImage? GitHubAvatarImage
    {
        get => _githubAvatarImage;
        private set
        {
            if (ReferenceEquals(_githubAvatarImage, value)) return;
            _githubAvatarImage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(GitHubAccountHasAvatar));
        }
    }
    public bool GitHubAccountHasAvatar => IsGitHubSignedIn && GitHubAvatarImage is not null;
    public string GitHubOwner
    {
        get => _settings.GitHubOwner;
        set { var cleaned = value?.Trim() ?? string.Empty; if (_settings.GitHubOwner == cleaned) return; _settings.GitHubOwner = cleaned; SaveSettings(); InvalidateGitHubPreview(); OnPropertyChanged(nameof(CanBuildGitHubPreview)); }
    }
    public string GitHubRepository
    {
        get => _settings.GitHubRepository;
        set { var cleaned = value?.Trim() ?? string.Empty; if (_settings.GitHubRepository == cleaned) return; _settings.GitHubRepository = cleaned; SaveSettings(); InvalidateGitHubPreview(); OnPropertyChanged(nameof(CanBuildGitHubPreview)); }
    }
    public string GitHubBranch
    {
        get => _settings.GitHubBranch;
        set { var cleaned = value?.Trim() ?? string.Empty; if (_settings.GitHubBranch == cleaned) return; _settings.GitHubBranch = cleaned; SaveSettings(); InvalidateGitHubPreview(); OnPropertyChanged(nameof(CanBuildGitHubPreview)); }
    }
    public bool IsLibraryEmpty { get => _isLibraryEmpty; private set => Set(ref _isLibraryEmpty, value); }
    public bool IsGameResultsEmpty { get => _isGameResultsEmpty; private set => Set(ref _isGameResultsEmpty, value); }
    public int TotalGames { get => _totalGames; private set => Set(ref _totalGames, value); }
    public int TotalKnownAchievements { get => _totalKnownAchievements; private set => Set(ref _totalKnownAchievements, value); }
    public int TotalUnlocked { get => _totalUnlocked; private set => Set(ref _totalUnlocked, value); }
    public int CompletedGames { get => _completedGames; private set => Set(ref _completedGames, value); }
    public string CompletionText { get => _completionText; private set => Set(ref _completionText, value); }
    public string CompletionCoverageText { get => _completionCoverageText; private set => Set(ref _completionCoverageText, value); }
    public string AchievementTotalText { get => _achievementTotalText; private set => Set(ref _achievementTotalText, value); }
    public string MeasurableGamesText { get => _measurableGamesText; private set => Set(ref _measurableGamesText, value); }
    public IReadOnlyList<string> GameFilterOptions { get; } = ["All games", "Measurable progress", "Completed", "Needs information"];
    public IReadOnlyList<string> GameSortOptions { get; } = ["Name", "Progress", "Recent unlock"];
    public string SelectedGameFilter
    {
        get => _selectedGameFilter;
        set { if (Set(ref _selectedGameFilter, value)) RefreshGamesView(); }
    }
    public string SelectedGameSort
    {
        get => _selectedGameSort;
        set { if (Set(ref _selectedGameSort, value)) RefreshGamesView(); }
    }
    public string IssueText { get => _issueText; private set => Set(ref _issueText, value); }
    public string PreviewStatus { get => _previewStatus; private set => Set(ref _previewStatus, value); }
    public ExportPreviewFileViewModel? SelectedExport
    {
        get => _selectedExport;
        set
        {
            if (!Set(ref _selectedExport, value)) return;
            OnPropertyChanged(nameof(EmptyExportMessageVisibility));
        }
    }
    public bool HasPreviewFiles => ExportFiles.Count > 0;
    public Visibility EmptyExportMessageVisibility => SelectedExport is null ? Visibility.Visible : Visibility.Collapsed;

    public async Task CheckForUpdatesAsync()
    {
        if (IsCheckingForUpdates || IsDownloadingUpdate) return;
        IsCheckingForUpdates = true;
        UpdateStatus = "Checking GitHub for a newer release…";
        try
        {
            var update = await _releaseUpdateService.CheckForUpdateAsync(CurrentApplicationVersion);
            _availableUpdate = update;
            OnPropertyChanged(nameof(HasAvailableUpdate));
            OnPropertyChanged(nameof(AvailableUpdateVersion));
            OnPropertyChanged(nameof(CanInstallUpdate));
            CommandManager.InvalidateRequerySuggested();
            UpdateStatus = update is null
                ? $"No newer release than v{CurrentApplicationVersion.Major}.{CurrentApplicationVersion.Minor}.{Math.Max(0, CurrentApplicationVersion.Build)} is available."
                : IsPackagedExecutable
                    ? $"AchievementSync v{update.VersionText} is available. Choose whether to download and install it; the app will restart to finish."
                    : $"AchievementSync v{update.VersionText} is available. Open the packaged AchievementSync.exe to install it.";
            if (update is not null && IsPackagedExecutable)
                UpdateAvailable?.Invoke(update.VersionText);
        }
        catch (Exception exception)
        {
            UpdateStatus = "Could not check for updates. Check your connection and try again.";
            AppLog.Warning($"GitHub release update check failed: {exception.GetType().Name}.");
        }
        finally
        {
            IsCheckingForUpdates = false;
        }
    }

    private async Task DownloadAndInstallUpdateAsync()
    {
        var update = _availableUpdate;
        if (update is null) return;
        if (!IsPackagedExecutable)
        {
            UpdateStatus = "Run the packaged AchievementSync.exe to install updates.";
            return;
        }

        IsDownloadingUpdate = true;
        var downloadedPath = string.Empty;
        try
        {
            UpdateStatus = $"Downloading v{update.VersionText}… 0%";
            var progress = new Progress<UpdateDownloadProgress>(item =>
                UpdateStatus = $"Downloading v{update.VersionText}… {item.Percent}%");
            downloadedPath = await _releaseUpdateService.DownloadAndVerifyAsync(update, progress);
            var executablePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("The running application path could not be determined.");
            GitHubReleaseUpdateService.LaunchReplacement(executablePath, downloadedPath, update.VersionText);
            UpdateStatus = $"v{update.VersionText} is ready. Restarting AchievementSync to install it…";
            UpdateRestartRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            UpdateStatus = "The update could not be installed. Your current app is still available.";
            AppLog.Warning($"GitHub release update installation failed: {exception}");
        }
        finally
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(downloadedPath) && File.Exists(downloadedPath))
                    File.Delete(downloadedPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                AppLog.Warning($"A downloaded update file could not be removed: {exception.GetType().Name}.");
            }
            IsDownloadingUpdate = false;
        }
    }

    public async Task LoadAsync()
    {
        await ScanAsync();
    }

    public void AddCustomLocation(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath)) return;
        if (Locations.Any(item => string.Equals(item.Path, folderPath, StringComparison.OrdinalIgnoreCase))) return;

        var name = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var preference = new CustomLocationPreference
        {
            Name = string.IsNullOrWhiteSpace(name) ? folderPath : name,
            Path = folderPath,
            Enabled = true
        };
        _settings.CustomLocations.Add(preference);
        Locations.Add(new LocationOptionViewModel(new ScanLocation(
            "custom-" + preference.Id,
            preference.Name,
            preference.Path,
            true,
            true,
            true), OnLocationChanged));
        SaveSettings();
        _ = ScanAsync();
    }

    public IReadOnlyList<GameExecutableAssociation> GetGameExecutables(GameCardViewModel game) =>
        Games.Contains(game) ? _gameplayTracker.GetAssociations(game.AppId) : [];

    public bool AddGameExecutable(GameCardViewModel game, string executablePath, out string? error)
    {
        error = null;
        if (!Games.Contains(game))
        {
            error = "This game is no longer in the current library. Restart AchievementSync and retry.";
            return false;
        }

        try
        {
            if (!_gameplayTracker.AddExecutable(game.AppId, executablePath))
            {
                error = "That executable is already linked, is invalid, or is linked to another library game.";
                return false;
            }
            RefreshPlaytimeSnapshots(DateTimeOffset.UtcNow);
            ProcessTrackingConfigurationChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Error("Could not save the game's executable association.", exception);
            error = "AchievementSync could not save this executable association. Check the local log.";
            return false;
        }
    }

    public void RefreshPlaytime(DateTimeOffset nowUtc) => RefreshPlaytimeSnapshots(nowUtc);

    public async Task ProcessChangedSourcesAsync(IReadOnlyList<DiscoveredAchievementFile> changedSources)
    {
        if (changedSources.Count == 0 || Application.Current?.Dispatcher is not { } dispatcher) return;
        await _changedSourceGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var appIds = changedSources.Select(item => item.AppId).Distinct(StringComparer.Ordinal).ToArray();
            foreach (var appId in appIds)
            {
                var knownSources = _sourceFiles.Where(item => string.Equals(item.AppId, appId, StringComparison.Ordinal)).ToArray();
                IReadOnlyList<GameProfile> parsed = [];
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    parsed = await _scanner.ParseKnownSourcesAsync(knownSources).ConfigureAwait(false);
                    if (parsed.Count > 0) break;
                    if (attempt < 3) await Task.Delay(TimeSpan.FromMilliseconds(150 * (attempt + 1))).ConfigureAwait(false);
                }

                var localProfile = parsed.FirstOrDefault(item => string.Equals(item.AppId, appId, StringComparison.Ordinal));
                if (localProfile is null) continue;
                IReadOnlyList<AchievementRecord>? viewerAchievements = null;
                await dispatcher.InvokeAsync(() =>
                {
                    var game = Games.FirstOrDefault(item => string.Equals(item.AppId, appId, StringComparison.Ordinal));
                    var title = game?.Title ?? localProfile.Title ?? $"AppID {appId}";
                    var notificationProfile = EnrichProfileWithCatalog(localProfile, game);
                    foreach (var notification in _unlockTracker.Observe(notificationProfile, title))
                    {
                        if (_settings.AchievementNotificationsEnabled)
                            AchievementUnlocked?.Invoke(this, notification);
                        RequestAutoSyncAfterUnlock(notification.AppId);
                    }

                    if (game is null) return;
                    game.ApplyLocalProfileUpdate(localProfile);
                    viewerAchievements = game.Profile.Achievements;
                    UpdateLibrarySummary();
                    RefreshGamesView();
                }, System.Windows.Threading.DispatcherPriority.DataBind);

                if (viewerAchievements is { Count: > 0 })
                {
                    try
                    {
                        await Task.Run(() => ViewerAchievementsWriter.WriteAchievementsJson(appId, viewerAchievements))
                            .ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        AppLog.Warning($"Could not update viewer achievements.json for AppID {appId}: {exception.GetType().Name}.");
                    }
                }
            }
        }
        catch (Exception exception)
        {
            AppLog.Warning($"A changed achievement profile could not be refreshed: {exception.GetType().Name}.");
        }
        finally
        {
            _changedSourceGate.Release();
        }
    }

    private GameProfile EnrichProfileWithCatalog(GameProfile profile, GameCardViewModel? game)
    {
        var catalogEntries = game?.Profile?.Achievements;
        var catalogComplete = game?.Profile?.HasCompleteCatalog == true;
        var catalogTitle = game?.Title;
        if (catalogEntries is null || catalogEntries.Count == 0)
        {
            var cached = _localCatalogCache.LoadCatalog(profile.AppId);
            catalogEntries = cached?.Achievements;
            catalogComplete = cached?.HasCompleteCatalog == true;
            catalogTitle = cached?.Title;
        }

        if (catalogEntries is null || catalogEntries.Count == 0) return profile;
        var catalog = new ParsedProfile(
            profile.AppId,
            "Cached achievement catalog",
            $"local://AppID/{profile.AppId}/catalog",
            DateTimeOffset.UtcNow,
            catalogEntries.Select(item => item with { IsUnlocked = null, UnlockTimeUnix = null }).ToArray(),
            catalogComplete,
            catalogTitle);
        return ProfileMerger.EnrichWithCatalog(profile, catalog);
    }

    private GameProfile MergeCachedMetadataProgress(GameProfile game)
    {
        var path = Path.Combine(MetadataRoot, game.AppId, "achievements.json");
        if (!File.Exists(path)) return game;
        var parsed = _metadataProgressParser.Parse(path, game.AppId).Profile;
        if (parsed is null) return game;

        var current = new ParsedProfile(
            game.AppId,
            "Scanned local achievement profiles",
            string.Join(";", game.SourceFiles),
            DateTimeOffset.UtcNow,
            game.Achievements,
            game.HasCompleteCatalog,
            game.Title,
            game.UnlistedAchievementsAreLocked);
        var merged = ProfileMerger.Merge([current, parsed]).Single();
        // This file is derived cache data, so keep the actual watched paths on the library card.
        return merged with { SourceFiles = game.SourceFiles };
    }

    private async Task ScanAsync()
    {
        if (IsScanning)
        {
            _scanRequestedDuringRun = true;
            return;
        }
        IsScanning = true;
        ScanStatus = "Discovering local achievement profiles…";
        IssueText = string.Empty;
        ExportFiles.Clear();
        SelectedExport = null;
        PreviewStatus = IsGitHubSignedIn
            ? "Refreshing the pending GitHub changes…"
            : "Connect GitHub, then build a preview. Nothing has been uploaded.";
        OnPropertyChanged(nameof(HasPreviewFiles));
        AppLog.Info("Startup library discovery started.");

        try
        {
            var scanLocations = Locations.Select(item => new ScanLocation(
                item.Id,
                item.Name,
                item.Path,
                item.IsEnabled,
                item.IsCustom,
                Directory.Exists(item.Path))).ToArray();
            var result = await _scanner.ScanAsync(scanLocations);
            var cacheUpdate = await Task.Run(() => _localMetadataCache.UpdateAsync(result.SourceFiles, _platformAdapters));
            if (cacheUpdate.UpdatedFiles > 0 || cacheUpdate.FailedFiles > 0)
            {
                AppLog.Info($"Local metadata cache update finished. Updated={cacheUpdate.UpdatedFiles}; unchanged={cacheUpdate.UnchangedFiles}; skipped={cacheUpdate.FailedFiles}.");
            }

            var catalogUpdate = await Task.Run(() => _localCatalogCache.UpdateAsync(result.SourceFiles));
            if (catalogUpdate.GamesUpdated > 0 || catalogUpdate.Failed > 0)
                AppLog.Info($"Local achievement catalog cache update finished. Games={catalogUpdate.GamesUpdated}; images={catalogUpdate.ImagesStored}; failed={catalogUpdate.Failed}.");

            var libraryGames = await Task.Run(() => result.Games.Select(game =>
            {
                var withCachedProgress = MergeCachedMetadataProgress(game);
                var catalog = _localCatalogCache.LoadCatalog(game.AppId);
                return catalog is null ? withCachedProgress : ProfileMerger.EnrichWithCatalog(withCachedProgress, catalog);
            }).ToArray());

            _sourceFiles = result.SourceFiles;
            WatchSourcesChanged?.Invoke(this, _sourceFiles);
            Games.Clear();
            var gamesToLoadMetadata = new List<GameCardViewModel>();
            var gamesToLoadViewerData = new List<GameCardViewModel>();
            foreach (var game in libraryGames)
            {
                var card = new GameCardViewModel(game);
                if (_settings.ManualGameNames.TryGetValue(game.AppId, out var manualName))
                {
                    card.ApplyManualTitle(manualName);
                }
                Games.Add(card);
                gamesToLoadMetadata.Add(card);
                if (!game.CompletionPercent.HasValue || NeedsCatalogMetadata(game)) gamesToLoadViewerData.Add(card);
            }
            _ = CacheScreenshotsForGamesAsync(Games.Select(game => game.AppId)
                .Distinct(StringComparer.Ordinal).ToArray());
            // Load public metadata/viewer data per game, but only refresh the UI once
            // after each batch settles instead of per-game.
            _ = Task.Run(async () =>
            {
                await Task.WhenAll(gamesToLoadMetadata.Select(card => LoadMetadataAsync(card)));
                await Task.WhenAll(gamesToLoadViewerData.Select(card => LoadViewerDataAsync(card)));
                var viewerExports = await Application.Current!.Dispatcher.InvokeAsync(() =>
                    gamesToLoadMetadata
                        .Where(card => card.Profile.Achievements.Count > 0)
                        .Select(card => (card.AppId, card.Profile.Achievements))
                        .ToArray());
                foreach (var export in viewerExports)
                {
                    try
                    {
                        ViewerAchievementsWriter.WriteAchievementsJson(export.AppId, export.Achievements);
                    }
                    catch (Exception exception)
                    {
                        AppLog.Warning($"Could not write viewer achievements.json for AppID {export.AppId}: {exception.GetType().Name}.");
                    }
                }
                await Application.Current!.Dispatcher.InvokeAsync(() =>
                {
                    RefreshGamesView();
                    UpdateLibrarySummary();
                }, System.Windows.Threading.DispatcherPriority.Background);
            });
            var newUnlockNotifications = _unlockTracker.ObserveMany(Games.Select(game => (game.Profile, (string?)game.Title))).ToList();
            foreach (var notification in newUnlockNotifications)
            {
                if (_settings.AchievementNotificationsEnabled)
                    AchievementUnlocked?.Invoke(this, notification);
                if (_settings.AutoSyncAfterUnlock)
                    RequestAutoSyncAfterUnlock(notification.AppId);
            }
            var removedHelperAssociations = 0;
            foreach (var game in libraryGames)
            {
                foreach (var association in _gameplayTracker.GetAssociations(game.AppId))
                {
                    if (!SteamInstalledExecutableDiscovery.IsExcludedExecutablePath(association.ExecutablePath)) continue;
                    if (_gameplayTracker.RemoveExecutable(game.AppId, association.ExecutablePath))
                        removedHelperAssociations++;
                }
            }
            if (removedHelperAssociations > 0)
                AppLog.Warning($"Removed {removedHelperAssociations} helper executable association(s) that could not represent a game process.");

            var steamAppIdsNeedingAssociations = libraryGames
                .Where(game => _gameplayTracker.GetAssociations(game.AppId).Count == 0)
                .Select(game => game.AppId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var discoveredExecutables = await Task.Run(() => SteamInstalledExecutableDiscovery.Discover(
                steamAppIdsNeedingAssociations,
                _platformSourceOptions.SteamRoot));
            var autoLinkedExecutables = 0;
            foreach (var game in libraryGames)
            {
                if (!discoveredExecutables.TryGetValue(game.AppId, out var executables)) continue;
                foreach (var executable in executables)
                {
                    if (_gameplayTracker.AddExecutable(game.AppId, executable)) autoLinkedExecutables++;
                }
            }
            if (autoLinkedExecutables > 0)
                AppLog.Info($"Steam install discovery linked {autoLinkedExecutables} game executable(s) for playtime tracking.");

            _gameplayTracker.SetLibraryGames(libraryGames.Select(game => game.AppId), DateTimeOffset.UtcNow);
            RefreshPlaytimeSnapshots(DateTimeOffset.UtcNow);
            ProcessTrackingConfigurationChanged?.Invoke(this, EventArgs.Empty);
            IsLibraryEmpty = libraryGames.Length == 0;

            foreach (var location in result.Locations)
            {
                Locations.FirstOrDefault(item => item.Id == location.Id)?.Update(location);
            }

            UpdateLibrarySummary();

            LastScanText = $"Library loaded {result.CompletedAt.ToLocalTime():MMM d, h:mm tt}";
            ScanStatus = libraryGames.Length == 0
                ? "No supported achievement profiles found yet"
                : $"Loaded {libraryGames.Length} game profile{(libraryGames.Length == 1 ? string.Empty : "s")}";
            IssueText = result.Issues.Count == 0
                ? ""
                : $"{result.Issues.Count} file or folder item(s) were skipped. Open the log for details.";
            RefreshGamesView();
            AppLog.Info($"Startup library discovery finished. Games={libraryGames.Length}; files={result.CandidateFiles}; issues={result.Issues.Count}.");
            AppLog.Action($"Finished library scan: games={libraryGames.Length}; sourceFiles={result.SourceFiles.Count}; issues={result.Issues.Count}.");
            foreach (var issue in result.Issues.Take(200)) AppLog.Warning(issue);

            // Keep the FILES IN PREVIEW list live: rebuild the GitHub preview in the background so it always reflects the library.
            if (CanBuildGitHubPreview)
                _ = BuildGitHubPreviewAsync();
        }
        catch (Exception exception)
        {
            ScanStatus = "The library could not be loaded. Check the local log for details.";
            AppLog.Error("Startup library discovery failed.", exception);
        }
        finally
        {
            IsScanning = false;
            if (_scanRequestedDuringRun)
            {
                _scanRequestedDuringRun = false;
                await ScanAsync();
            }
        }
    }

    private async Task SignInToGitHubAsync()
    {
        if (!HasGitHubOAuthClientId)
        {
            GitHubStatus = "This build has no publisher-configured GitHub OAuth Client ID. Sign-in cannot start yet.";
            return;
        }

        IsSignInRunning = true;
        GitHubStatus = "Requesting a one-time GitHub device code…";
        try
        {
            var session = await _githubDeviceFlow.StartAsync(GitHubOAuthConfiguration.ClientId);
            if (!string.Equals(session.VerificationUri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
                throw new GitHubAuthorizationException("GitHub returned an unexpected authorization address.");

            Process.Start(new ProcessStartInfo(session.VerificationUri.ToString()) { UseShellExecute = true });
            var choice = MessageBox.Show(
                $"Enter this one-time code on GitHub:\n\n{session.UserCode}\n\nThe code expires in about {Math.Max(1, session.ExpiresInSeconds / 60)} minutes. Continue waiting for authorization?",
                "Connect AchievementSync to GitHub",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Information);
            if (choice != MessageBoxResult.OK)
            {
                GitHubStatus = "GitHub sign-in was cancelled. No data was sent.";
                return;
            }

            GitHubStatus = "Waiting for approval in the browser…";
            var token = await _githubDeviceFlow.PollForTokenAsync(GitHubOAuthConfiguration.ClientId, session);
            var account = await _githubSyncClient.GetCurrentUserAsync(token.AccessToken);
            if (!account.HasPublicRepositoryAccess)
                throw new GitHubAuthorizationException("GitHub did not grant the required public_repo permission. Review the authorization request and connect again.");
            _githubTokenStore.Write(token.ToStoredCredential().Serialize());
            var previousLogin = _settings.GitHubLogin;
            _settings.GitHubLogin = account.Login;
            _settings.GitHubDisplayName = string.IsNullOrWhiteSpace(account.DisplayName) ? account.Login : account.DisplayName!;
            _settings.GitHubAvatarUrl = account.AvatarUrl ?? string.Empty;
            if (string.IsNullOrWhiteSpace(_settings.GitHubOwner)) _settings.GitHubOwner = account.Login;
            if (string.IsNullOrWhiteSpace(_settings.GitHubRepository)) _settings.GitHubRepository = "achievement-viewer";
            if (string.IsNullOrWhiteSpace(_settings.GitHubBranch)) _settings.GitHubBranch = "user";
            if (!string.Equals(previousLogin, account.Login, StringComparison.OrdinalIgnoreCase))
            {
                _settings.AutoSyncAfterUnlock = false;
                OnPropertyChanged(nameof(AutoSyncAfterUnlock));
            }
            SaveSettings();
            IsGitHubSignedIn = true;
            GitHubStatus = $"Connected as {account.Login}. Choose your public repository, then build a preview.";
            RefreshGitHubAvatar();
            AppLog.Info($"GitHub account connected: {account.Login}.");
        }
        catch (Exception exception)
        {
            IsGitHubSignedIn = false;
            GitHubStatus = exception is GitHubAuthorizationException or GitHubApiException
                ? exception.Message
                : "GitHub sign-in failed. Check your network and the local log.";
            AppLog.Warning($"GitHub sign-in failed: {exception.GetType().Name}.");
        }
        finally
        {
            IsSignInRunning = false;
        }
    }

    public async Task ConnectGitHubWithTokenAsync(string token)
    {
        if (_isSignInRunning) return;
        var cleaned = token?.Trim() ?? string.Empty;
        if (cleaned.Length is < 20 or > 5000)
        {
            GitHubStatus = "Paste a valid classic GitHub personal access token, then choose Connect with token.";
            return;
        }

        IsSignInRunning = true;
        GitHubStatus = "Checking the token with GitHub…";
        try
        {
            var account = await _githubSyncClient.GetCurrentUserAsync(cleaned);
            if (!account.HasPublicRepositoryAccess)
                throw new GitHubAuthorizationException("This token does not report public_repo access. Create a classic token with public_repo permission and try again.");

            var previousLogin = _settings.GitHubLogin;
            _githubTokenStore.Write(new GitHubStoredCredential(cleaned, "bearer", "public_repo", null, null, null).Serialize());
            _settings.GitHubLogin = account.Login;
            _settings.GitHubDisplayName = string.IsNullOrWhiteSpace(account.DisplayName) ? account.Login : account.DisplayName!;
            _settings.GitHubAvatarUrl = account.AvatarUrl ?? string.Empty;
            if (string.IsNullOrWhiteSpace(_settings.GitHubOwner)) _settings.GitHubOwner = account.Login;
            if (string.IsNullOrWhiteSpace(_settings.GitHubRepository)) _settings.GitHubRepository = "achievement-viewer";
            if (string.IsNullOrWhiteSpace(_settings.GitHubBranch)) _settings.GitHubBranch = "user";
            if (!string.Equals(previousLogin, account.Login, StringComparison.OrdinalIgnoreCase))
            {
                _settings.AutoSyncAfterUnlock = false;
                OnPropertyChanged(nameof(AutoSyncAfterUnlock));
            }
            SaveSettings();
            IsGitHubSignedIn = true;
            GitHubStatus = $"Connected as {account.Login}. Review the target repository and preview before syncing.";
            RefreshGitHubAvatar();
            AppLog.Info($"GitHub account connected with a personal access token: {account.Login}.");
        }
        catch (Exception exception)
        {
            IsGitHubSignedIn = false;
            GitHubStatus = exception is GitHubAuthorizationException or GitHubApiException
                ? exception.Message
                : "GitHub token verification failed. Check the token and local log.";
            AppLog.Warning($"GitHub personal access token was rejected: {exception.GetType().Name}.");
        }
        finally
        {
            IsSignInRunning = false;
        }
    }

    private void DisconnectGitHub()
    {
        try
        {
            _githubTokenStore.Delete();
            _settings.GitHubLogin = string.Empty;
            _settings.GitHubDisplayName = string.Empty;
            _settings.GitHubAvatarUrl = string.Empty;
            SaveSettings();
            GitHubAvatarImage = null;
            IsGitHubSignedIn = false;
            InvalidateGitHubPreview();
            GitHubStatus = "GitHub account disconnected. The credential was removed from Windows Credential Manager.";
        }
        catch (Exception exception)
        {
            GitHubStatus = "Could not remove the GitHub credential. Check the local log.";
            AppLog.Warning($"Could not disconnect GitHub: {exception.GetType().Name}.");
        }
    }

    private async Task BuildGitHubPreviewAsync()
    {
        var token = await ReadGitHubTokenAsync();
        if (token is null) return;
        IsPushPreviewRunning = true;
        GitHubStatus = "Checking your repository and merging achievement progress…";
        try
        {
            if (!string.Equals(GitHubOwner, _settings.GitHubLogin, StringComparison.OrdinalIgnoreCase))
                throw new GitHubApiException("Choose a repository owned by the connected GitHub account.");

            var target = new GitHubSyncTarget(GitHubOwner, GitHubRepository, GitHubBranch);
            var appIds = new HashSet<string>(Games.Select(game => game.AppId), StringComparer.Ordinal);
            var (exports, failures) = MetadataAchievementExportPlanner.Build(MetadataRoot, appIds);
            var planned = exports.Select(file => new AchievementExportFile(
                file.RelativePath, file.AppId, file.KnownStates, file.Json)).ToList();
            if (planned.Count == 0)
            {
                _githubPreview = null;
                ExportFiles.Clear();
                SelectedExport = null;
                OnPropertyChanged(nameof(HasPreviewFiles));
                OnPropertyChanged(nameof(CanPushGitHubPreview));
                PreviewStatus = failures.Count == 0
                    ? "No cached achievements.json files to preview. Scan for profiles with known unlock states."
                    : "No usable cached achievements.json files. " + string.Join(" ", failures.Take(3).Select(failure => $"{failure.AppId}: {failure.Reason}."));
                GitHubStatus = "Nothing to preview.";
                return;
            }
            if (failures.Count > 0)
                AppLog.Warning($"GitHub preview skipped {failures.Count} metadata file(s): {string.Join("; ", failures.Take(5).Select(failure => $"{failure.AppId} {failure.Reason}"))}");
            _githubPreview = await _githubSyncClient.BuildPreviewAsync(token, target, planned);
            ExportFiles.Clear();
            foreach (var change in _githubPreview.Changes)
            {
                ExportFiles.Add(new ExportPreviewFileViewModel(new AchievementExportFile(
                    change.RelativePath, change.AppId, change.AchievementCount, change.ProposedJson), change.NewUnlockCount));
            }
            SelectedExport = ExportFiles.FirstOrDefault();
            OnPropertyChanged(nameof(HasPreviewFiles));
            OnPropertyChanged(nameof(CanPushGitHubPreview));
            PreviewStatus = _githubPreview.Changes.Count == 0
                ? "No remote changes are needed. Nothing will be uploaded."
                : $"{_githubPreview.Changes.Count:N0} exact AppID file(s) ready · {_githubPreview.Changes.Sum(change => change.NewUnlockCount):N0} new unlock(s). Review each JSON file, then explicitly confirm Push.";
            GitHubStatus = _githubPreview.Changes.Count == 0
                ? $"{target.Owner}/{target.Repository} ({target.Branch}) is up to date."
                : $"Preview ready for {target.Owner}/{target.Repository} ({target.Branch}). Nothing has been uploaded.";
        }
        catch (Exception exception)
        {
            _githubPreview = null;
            ExportFiles.Clear();
            SelectedExport = null;
            OnPropertyChanged(nameof(HasPreviewFiles));
            OnPropertyChanged(nameof(CanPushGitHubPreview));
            if (exception is GitHubAuthorizationException authorizationFailure)
                HandleGitHubAuthorizationFailure(authorizationFailure);
            GitHubStatus = exception is GitHubAuthorizationException or GitHubApiException or ArgumentException or InvalidDataException
                ? exception.Message
                : "Could not build a GitHub preview. Check the local log.";
            AppLog.Warning($"GitHub preview failed: {exception.GetType().Name}.");
        }
        finally
        {
            IsPushPreviewRunning = false;
        }
    }

    private async Task PushGitHubPreviewAsync(bool requireConfirmation = true)
    {
        var token = await ReadGitHubTokenAsync();
        if (token is null) return;

        var preview = _githubPreview;
        if (preview is null || preview.Changes.Count == 0)
        {
            // The library may have changed since the last preview; build a fresh one before pushing.
            await BuildGitHubPreviewAsync();
            preview = _githubPreview;
            if (preview is null) return;
            if (preview.Changes.Count == 0)
            {
                GitHubStatus = $"{GitHubOwner}/{GitHubRepository} ({GitHubBranch}) is already up to date.";
                PreviewStatus = "No remote changes are needed. Nothing will be uploaded.";
                return;
            }
        }

        if (requireConfirmation)
        {
            var paths = string.Join(Environment.NewLine, preview.Changes.Take(12).Select(change => "• " + change.RelativePath));
            if (preview.Changes.Count > 12) paths += $"{Environment.NewLine}• …and {preview.Changes.Count - 12:N0} more files (all listed in the preview).";
            var confirmation = MessageBox.Show(
                $"Push {preview.Changes.Count:N0} achievement file(s) to {preview.Target.Owner}/{preview.Target.Repository} on branch {preview.Target.Branch}?\n\n{paths}\n\nOnly the reviewed AppID achievement JSON will be sent. Achievement IDs, earned states, and available unlock times will be visible in that repository. Original save files, local paths, and credentials are not included.",
                "Confirm GitHub achievement sync",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirmation != MessageBoxResult.Yes) return;
        }

        IsPushRunning = true;
        GitHubStatus = "Creating a single content-only GitHub commit…";
        try
        {
            var result = await _githubSyncClient.PushAsync(token, preview);
            SaveSettings();
            RefreshAutoSyncStatus();
            _githubPreview = null;
            OnPropertyChanged(nameof(CanPushGitHubPreview));
            GitHubStatus = result.Pushed
                ? $"Pushed {result.UpdatedFiles:N0} file(s) with {result.NewUnlocks:N0} new unlock(s). Commit: {result.CommitUrl}"
                : "No changes were pushed.";
            PreviewStatus = "Push completed. Build a new preview to check for further changes.";
            AppLog.Info($"GitHub sync completed. Files={result.UpdatedFiles}; new unlocks={result.NewUnlocks}.");
        }
        catch (Exception exception)
        {
            _githubPreview = null;
            OnPropertyChanged(nameof(CanPushGitHubPreview));
            if (exception is GitHubAuthorizationException authorizationFailure)
                HandleGitHubAuthorizationFailure(authorizationFailure);
            GitHubStatus = exception is GitHubAuthorizationException or GitHubApiException
                ? exception.Message
                : "GitHub push failed. Rebuild the preview before retrying.";
            AppLog.Warning($"GitHub push failed: {exception.GetType().Name}.");
        }
        finally
        {
            IsPushRunning = false;
        }
    }

    private async Task<string?> ReadGitHubTokenAsync()
    {
        try
        {
            var credential = GitHubStoredCredential.Parse(_githubTokenStore.Read());
            if (credential is null)
            {
                HandleGitHubAuthorizationFailure(new GitHubAuthorizationException("GitHub sign-in has expired or was removed. Connect again."));
                return null;
            }

            if (credential.NeedsRefresh())
            {
                if (string.IsNullOrWhiteSpace(credential.RefreshToken)
                    || credential.RefreshTokenExpiresAtUtc is { } refreshExpiry && refreshExpiry <= DateTimeOffset.UtcNow)
                {
                    HandleGitHubAuthorizationFailure(new GitHubAuthorizationException("GitHub sign-in expired. Connect your account again."));
                    return null;
                }

                try
                {
                    var rotated = await _githubDeviceFlow.RefreshAsync(GitHubOAuthConfiguration.ClientId, credential.RefreshToken);
                    credential = rotated.ToStoredCredential();
                    _githubTokenStore.Write(credential.Serialize());
                    AppLog.Info("GitHub access token refreshed.");
                }
                catch (GitHubAuthorizationException exception)
                {
                    HandleGitHubAuthorizationFailure(exception);
                    return null;
                }
            }

            return credential.AccessToken;
        }
        catch (GitHubAuthorizationException)
        {
            return null;
        }
        catch (Exception exception)
        {
            IsGitHubSignedIn = false;
            GitHubStatus = "Could not read or refresh the secure GitHub credential. Check Windows Credential Manager or connect again.";
            AppLog.Warning($"Could not read GitHub credential: {exception.GetType().Name}.");
            return null;
        }
    }

    private void HandleGitHubAuthorizationFailure(GitHubAuthorizationException exception)
    {
        try
        {
            _githubTokenStore.Delete();
        }
        catch (Exception storageException)
        {
            AppLog.Warning($"Could not remove rejected GitHub credential: {storageException.GetType().Name}.");
        }
        IsGitHubSignedIn = false;
        _settings.GitHubLogin = string.Empty;
        SaveSettings();
        _githubPreview = null;
        ExportFiles.Clear();
        SelectedExport = null;
        OnPropertyChanged(nameof(HasPreviewFiles));
        OnPropertyChanged(nameof(CanPushGitHubPreview));
        PreviewStatus = "GitHub authorization is no longer active. Connect again before previewing changes.";
        GitHubStatus = exception.Message;
    }

    private void InvalidateGitHubPreview()
    {
        if (_githubPreview is null) return;
        _githubPreview = null;
        ExportFiles.Clear();
        SelectedExport = null;
        PreviewStatus = "The repository target changed. Build a new preview before reviewing files.";
        OnPropertyChanged(nameof(HasPreviewFiles));
        OnPropertyChanged(nameof(CanPushGitHubPreview));
    }

    // Automatically pushes the affected AppID only when a genuinely new unlock is
    // observed by the running-game file monitor. Never runs on app start, launches, or
    // ordinary scans; the caller is the achievement-file watcher path only.
    public void RequestAutoSyncAfterUnlock(string appId)
    {
        if (!_settings.AutoSyncAfterUnlock) return;
        if (string.IsNullOrWhiteSpace(appId) || !AppIdValidator.IsValid(appId)) return;
        lock (_autoSyncGate)
        {
            if (!_autoSyncPending.Add(appId)) return; // already queued; coalesce duplicates
        }
        _ = ProcessAutoSyncQueueAsync();
    }

    private async Task ProcessAutoSyncQueueAsync()
    {
        lock (_autoSyncGate)
        {
            if (_autoSyncRunning) return;
            _autoSyncRunning = true;
        }

        try
        {
            while (true)
            {
                string appId;
                lock (_autoSyncGate)
                {
                    if (_autoSyncPending.Count == 0) { _autoSyncRunning = false; return; }
                    appId = _autoSyncPending.First();
                    _autoSyncPending.Remove(appId);
                }
                await SyncUnlockedGameAsync(appId);
            }
        }
        catch (Exception exception)
        {
            AppLog.Warning($"The auto-sync queue stopped unexpectedly: {exception.GetType().Name}.");
            lock (_autoSyncGate) _autoSyncRunning = false;
        }
    }

    private async Task SyncUnlockedGameAsync(string appId)
    {
        var token = await ReadGitHubTokenAsync();
        if (token is null) { AutoSyncStatus = "Auto-sync is paused until you connect GitHub again."; return; }

        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var exportTask = Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    var game = Games.FirstOrDefault(item => string.Equals(item.AppId, appId, StringComparison.Ordinal));
                    return game is null ? null : game.Profile;
                });
                var profile = exportTask is null ? null : await exportTask;
                if (profile is null) { AutoSyncStatus = $"Auto-sync skipped AppID {appId}: it is no longer in the library."; return; }
                WriteMetadataAchievementFile(appId, profile);
                var (exports, failures) = MetadataAchievementExportPlanner.Build(MetadataRoot,
                    new HashSet<string>(StringComparer.Ordinal) { appId });
                if (exports.Count == 0) { AutoSyncStatus = failures.Count == 0
                    ? $"Auto-sync skipped AppID {appId}: no cached achievements.json to push."
                    : $"Auto-sync skipped AppID {appId}: {string.Join("; ", failures.Take(3).Select(failure => failure.Reason))}"; return; }
                var planned = exports.Select(file => new AchievementExportFile(
                    file.RelativePath, file.AppId, file.KnownStates, file.Json)).ToList();
                if (!string.Equals(GitHubOwner, _settings.GitHubLogin, StringComparison.OrdinalIgnoreCase))
                    throw new GitHubApiException("Choose a repository owned by the connected GitHub account.");

                var target = new GitHubSyncTarget(GitHubOwner, GitHubRepository, GitHubBranch);
                var preview = await _githubSyncClient.BuildPreviewAsync(token, target, planned);
                if (preview.Changes.Count == 0) { AutoSyncStatus = $"Auto-sync: AppID {appId} is already up to date."; return; }
                var result = await _githubSyncClient.PushAsync(token, preview);
                AutoSyncStatus = $"Auto-sync: pushed AppID {appId} ({result.NewUnlocks:N0} new unlock(s)).";
                AppLog.Info(AutoSyncStatus);
                InvalidateGitHubPreview();
                return;
            }
            catch (Exception exception)
            {
                var retryable = exception is GitHubApiException or HttpRequestException or TaskCanceledException;
                if (!retryable || attempt == maxAttempts)
                {
                    AutoSyncStatus = exception is GitHubApiException or GitHubAuthorizationException
                        ? $"Auto-sync failed for AppID {appId}: {exception.Message}"
                        : $"Auto-sync failed for AppID {appId}. Check the local log.";
                    AppLog.Warning($"Auto-sync failed for {appId}: {exception.GetType().Name}.");
                    return;
                }
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt));
            }
        }
    }

    private async Task SyncAllMetadataAsync()
    {
        if (_isSyncAllRunning) return;
        IsSyncAllRunning = true;
        BulkSyncStatus = "Preparing the local metadata…";
        try
        {
            var token = await ReadGitHubTokenAsync();
            if (token is null) { BulkSyncStatus = "Connect your GitHub account before syncing."; return; }
            if (!string.Equals(GitHubOwner, _settings.GitHubLogin, StringComparison.OrdinalIgnoreCase))
                throw new GitHubApiException("Choose a repository owned by the connected GitHub account.");

            var target = new GitHubSyncTarget(GitHubOwner, GitHubRepository, GitHubBranch);
            var appIds = new HashSet<string>(Games.Select(game => game.AppId), StringComparer.Ordinal);
            var (exports, failures) = MetadataAchievementExportPlanner.Build(MetadataRoot, appIds);
            var planned = exports.Select(file => new AchievementExportFile(
                file.RelativePath, file.AppId, file.KnownStates, file.Json)).ToList();
            if (planned.Count == 0)
            {
                BulkSyncResults.Clear();
                foreach (var failure in failures)
                    BulkSyncResults.Add($"{failure.AppId} — failed — {failure.Reason}");
                BulkSyncStatus = failures.Count == 0
                    ? "No cached achievement metadata is ready to sync. Load or configure a supported achievement profile first."
                    : $"No files were ready to sync; {failures.Count:N0} file(s) need attention.";
                OnPropertyChanged(nameof(HasBulkSyncReport));
                return;
            }

            var paths = string.Join(Environment.NewLine, planned.Take(12).Select(file => "• " + file.RelativePath));
            if (planned.Count > 12) paths += $"{Environment.NewLine}• …and {planned.Count - 12:N0} more files.";
            var confirmation = MessageBox.Show(
                $"Sync {planned.Count:N0} AppID achievement file(s) to {GitHubOwner}/{GitHubRepository} on branch {GitHubBranch}?\n\n{paths}\n\nOnly achievement JSON will be sent. Continue?",
                "Confirm metadata sync",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirmation != MessageBoxResult.Yes)
            {
                BulkSyncStatus = "Metadata sync cancelled. Nothing was uploaded.";
                return;
            }

            GitHubStatus = "Syncing achievement metadata…";
            var result = await _githubSyncClient.PushBulkAsync(token, target, planned);
            BulkSyncResults.Clear();
            foreach (var failure in failures)
                BulkSyncResults.Add($"{failure.AppId} — failed — {failure.Reason}");
            foreach (var entry in result.Entries)
                BulkSyncResults.Add($"{entry.AppId} — {entry.Action} — {entry.Detail}");
            BulkSyncStatus = result.Pushed ? $"Sync complete: {result.Summary}" : $"Nothing to sync: {result.Summary}";
            if (result.Pushed)
            {
                SaveSettings();
            }
            if (failures.Count > 0 && result.Pushed)
                BulkSyncStatus += $" · {failures.Count:N0} file(s) failed";
            if (result.CommitUrl is not null) GitHubStatus = $"Metadata synced to {target.Owner}/{target.Repository} ({target.Branch}). Commit: {result.CommitUrl}";
            OnPropertyChanged(nameof(HasBulkSyncReport));
            AppLog.Info($"Bulk metadata sync finished. pushed={result.Pushed}; achievementsPushed={result.AchievementFilesPushed}; unchanged={result.AchievementFilesUnchanged}; failed={result.AchievementFilesFailed + failures.Count}.");
        }
        catch (Exception exception)
        {
            if (exception is GitHubAuthorizationException authorizationFailure) HandleGitHubAuthorizationFailure(authorizationFailure);
            BulkSyncStatus = exception is GitHubApiException or GitHubAuthorizationException or ArgumentException or InvalidDataException
                ? "Bulk sync failed: " + exception.Message
                : "Bulk sync failed. Check the local log.";
            GitHubStatus = BulkSyncStatus;
            AppLog.Warning($"Bulk metadata sync failed: {exception}");
        }
        finally
        {
            IsSyncAllRunning = false;
        }
    }

    private static string MetadataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AchievementSync", "metadata");

    /// <summary>
    /// Writes the merged catalog and unlock states to the same viewer-compatible cache used by the library.
    /// Achievement IDs missing from a saved unlock list are represented as locked.
    /// </summary>
    private static void WriteMetadataAchievementFile(string appId, GameProfile profile)
    {
        try
        {
            ViewerAchievementsWriter.WriteAchievementsJson(appId, profile.Achievements);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            AppLog.Warning($"Could not refresh the metadata file for AppID {appId}: {exception.GetType().Name}.");
        }
    }

    private void ClearBulkSyncReport()
    {
        BulkSyncResults.Clear();
        BulkSyncStatus = "Report cleared.";
        OnPropertyChanged(nameof(HasBulkSyncReport));
    }

    public async Task AutoBackupSavesAfterGameSessionAsync(string appId)
    {
        if (!AutoBackupSavesAfterPlaytime || !AppIdValidator.IsValid(appId)) return;

        try
        {
            BackupStatus = $"Waiting for {Games.FirstOrDefault(game => game.AppId == appId)?.Title ?? $"AppID {appId}"} save files to settle…";
            await Task.Delay(TimeSpan.FromSeconds(2));
            await _saveBackupGate.WaitAsync();
            try
            {
                if (!IsGitHubSignedIn)
                {
                    BackupStatus = "Automatic save backup needs a connected GitHub account.";
                    return;
                }

                while (_isRestoreRunning || _isSyncAllRunning || _isPushRunning || _isPushPreviewRunning)
                    await Task.Delay(TimeSpan.FromMilliseconds(500));

                var game = Games.FirstOrDefault(item => string.Equals(item.AppId, appId, StringComparison.Ordinal));
                if (game is null) return;
                var stableSaves = await DiscoverStableSaveFilesAsync(appId, game.Title ?? string.Empty);
                if (stableSaves.Count == 0)
                {
                    BackupStatus = $"No save files were found for {game.Title} after the session.";
                    return;
                }
                await BackupSavesCoreAsync(appId, requireConfirmation: false, discoveredSaves: stableSaves);
            }
            finally
            {
                _saveBackupGate.Release();
            }
        }
        catch (Exception exception)
        {
            BackupStatus = "Automatic save backup failed. Check the local log.";
            AppLog.Warning($"Automatic save backup failed for AppID {appId}: {exception.GetType().Name}.");
        }
    }

    private async Task<IReadOnlyList<DiscoveredSaveFile>> DiscoverStableSaveFilesAsync(string appId, string title)
    {
        IReadOnlyList<DiscoveredSaveFile> previous = [];
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var current = await SaveFileDiscoverer.DiscoverAsync(
                new Dictionary<string, string>(StringComparer.Ordinal) { [appId] = title });
            if (attempt > 0 && HasSameSaveSnapshot(previous, current)) return current;
            previous = current;
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
        return previous;
    }

    private static bool HasSameSaveSnapshot(
        IReadOnlyList<DiscoveredSaveFile> left,
        IReadOnlyList<DiscoveredSaveFile> right)
    {
        if (left.Count != right.Count) return false;
        var leftSnapshot = left.Select(item => $"{item.SourcePath}|{item.Sha256}")
            .Order(StringComparer.OrdinalIgnoreCase);
        var rightSnapshot = right.Select(item => $"{item.SourcePath}|{item.Sha256}")
            .Order(StringComparer.OrdinalIgnoreCase);
        return leftSnapshot.SequenceEqual(rightSnapshot, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Backs up discovered save files and the restoration manifest for every game.</summary>
    private async Task BackupSavesAsync()
    {
        await _saveBackupGate.WaitAsync();
        try { await BackupSavesCoreAsync(onlyAppId: null, requireConfirmation: true, discoveredSaves: null); }
        finally { _saveBackupGate.Release(); }
    }

    private async Task BackupSavesCoreAsync(
        string? onlyAppId,
        bool requireConfirmation,
        IReadOnlyList<DiscoveredSaveFile>? discoveredSaves)
    {
        if (_isBackupRunning) return;
        IsBackupRunning = true;
        _backupFilesProcessed = 0;
        _backupFilesTotal = 0;
        _backupBytesProcessed = 0;
        _backupBytesTotal = 0;
        _backupStage = string.Empty;
        OnPropertyChanged(nameof(BackupProgressPercent));
        OnPropertyChanged(nameof(BackupFilesProcessed));
        OnPropertyChanged(nameof(BackupFilesTotal));
        OnPropertyChanged(nameof(BackupBytesProcessed));
        OnPropertyChanged(nameof(BackupBytesTotal));
        BackupStatus = "Discovering save files…";
        try
        {
            var token = await ReadGitHubTokenAsync();
            if (token is null) { BackupStatus = "Connect your GitHub account before backing up."; return; }
            if (!string.Equals(GitHubOwner, _settings.GitHubLogin, StringComparison.OrdinalIgnoreCase))
                throw new GitHubApiException("Choose a repository owned by the connected GitHub account.");

            var appMeta = Games
                .Where(game => onlyAppId is null || string.Equals(game.AppId, onlyAppId, StringComparison.Ordinal))
                .Select(game => (game.AppId, game.Title))
                .ToArray();
            BackupStatus = "Scanning known save locations…";
            var titles = appMeta.ToDictionary(item => item.AppId, item => item.Title ?? string.Empty, StringComparer.Ordinal);
            var saves = discoveredSaves ?? await SaveFileDiscoverer.DiscoverAsync(titles);
            if (onlyAppId is not null)
                saves = saves.Where(save => string.Equals(save.AppId, onlyAppId, StringComparison.Ordinal)).ToArray();
            var savesByApp = saves.GroupBy(save => save.AppId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => (IReadOnlyList<DiscoveredSaveFile>)group.ToArray(), StringComparer.Ordinal);
            if (saves.Count == 0)
            {
                BackupStatus = "No save files were discovered under the known locations.";
                return;
            }

            var plans = new List<SaveBackupPlan>();
            foreach (var (appId, _) in appMeta)
            {
                if (!savesByApp.TryGetValue(appId, out var appSaves) || appSaves.Count == 0)
                    continue;
                var source = SaveBackupPlanner.ResolveAchievementSource(appId);
                plans.Add(await SaveBackupPlanner.BuildAsync(appId, appSaves, source?.Path, source?.Format ?? "json"));
            }

            _backupFilesTotal = plans.Sum(p => p.Items.Count + 1); // items + 1 manifest per AppID
            _backupBytesTotal = plans.Sum(p => p.Manifest.Saves.Sum(s => s.Size));
            _backupFilesProcessed = 0;
            _backupBytesProcessed = 0;
            BackupStage = "Ready to back up";
            OnPropertyChanged(nameof(BackupFilesTotal));
            OnPropertyChanged(nameof(BackupBytesTotal));
            OnPropertyChanged(nameof(BackupFilesProcessed));
            OnPropertyChanged(nameof(BackupBytesProcessed));
            OnPropertyChanged(nameof(BackupProgressPercent));

            if (plans.Count == 0)
            {
                BackupStatus = "No games were found to back up.";
                return;
            }

            if (requireConfirmation)
            {
                var confirmation = MessageBox.Show(
                    $"Back up {plans.Count:N0} game(s) with {saves.Count:N0} save file(s) to {GitHubOwner}/{GitHubRepository} on branch {GitHubBranch}?\n\n" +
                    "Save files and a restoration manifest will be committed. Achievement data (achievements.json) is backed up separately through the achievements sync. Your original files are not modified.",
                    "Confirm save backup",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (confirmation != MessageBoxResult.Yes) { BackupStatus = "Backup cancelled."; return; }
            }

            var target = new GitHubSyncTarget(GitHubOwner, GitHubRepository, GitHubBranch);
            BackupStatus = $"Uploading backup… 0% ({saves.Count:N0} saves · {FormatBytes(_backupBytesTotal)})";
            var saveBackupProgress = new Progress<AchievementSync.Core.Synchronization.SaveBackupProgress>(update =>
            {
                _backupFilesProcessed = update.FilesProcessed;
                _backupBytesProcessed = update.BytesProcessed;
                _backupStage = update.Stage ?? _backupStage;
                BackupStatus = update.Stage ?? $"Uploading… {FormatBytes(update.BytesProcessed)} / {FormatBytes(_backupBytesTotal)} ({BackupProgressPercent}%)";
                OnPropertyChanged(nameof(BackupFilesProcessed));
                OnPropertyChanged(nameof(BackupBytesProcessed));
                OnPropertyChanged(nameof(BackupProgressPercent));
                OnPropertyChanged(nameof(BackupStage));
            });
            var result = await _githubSyncClient.PushSaveBackupAsync(token, target, plans, saveBackupProgress);
            BackupResults.Clear();
            foreach (var line in result.Report) BackupResults.Add(line);
            var operationName = requireConfirmation ? "Backup" : "Automatic save backup";
            BackupStatus = result.Pushed ? $"{operationName} complete: {result.Summary}" : $"No save changes to upload: {result.Summary}";
            if (result.CommitUrl is not null)
                GitHubStatus = $"Backup pushed to {target.Owner}/{target.Repository} ({target.Branch}). Commit: {result.CommitUrl}";
            OnPropertyChanged(nameof(HasBackupReport));
            AppLog.Info($"Save backup finished. pushed={result.Pushed}; filesPushed={result.FilesPushed}; unchanged={result.FilesUnchanged}; failed={result.FilesFailed}.");
        }
        catch (Exception exception)
        {
            if (exception is GitHubAuthorizationException authorizationFailure) HandleGitHubAuthorizationFailure(authorizationFailure);
            BackupStatus = exception is GitHubApiException or GitHubAuthorizationException or ArgumentException or InvalidDataException
                ? "Backup failed: " + exception.Message
                : "Backup failed. Check the local log.";
            GitHubStatus = BackupStatus;
            AppLog.Warning($"Save backup failed: {exception}");
        }
        finally
        {
            IsBackupRunning = false;
        }
    }

    /// <summary>Restores save files and achievements from the repository, writing only after explicit confirmation.</summary>
    private async Task RestoreSavesAsync()
    {
        if (_isRestoreRunning) return;
        IsRestoreRunning = true;
        RestoreStatus = "Fetching backups from the repository…";
        try
        {
            var token = await ReadGitHubTokenAsync();
            if (token is null) { RestoreStatus = "Connect your GitHub account before restoring."; return; }
            if (!string.Equals(GitHubOwner, _settings.GitHubLogin, StringComparison.OrdinalIgnoreCase))
                throw new GitHubApiException("Choose a repository owned by the connected GitHub account.");

            var target = new GitHubSyncTarget(GitHubOwner, GitHubRepository, GitHubBranch);
            var appIds = new HashSet<string>(Games.Select(game => game.AppId), StringComparer.Ordinal);
            var plans = await _githubSyncClient.PullSaveBackupAsync(token, target, appIds);
            if (plans.Count == 0)
            {
                RestoreStatus = "No backups were found in the repository.";
                return;
            }

            var totalFiles = plans.Sum(plan => plan.Saves.Count);
            var confirmation = MessageBox.Show(
                $"Restore {totalFiles:N0} file(s) for {plans.Count:N0} game(s) from {GitHubOwner}/{GitHubRepository} (branch {GitHubBranch})?\n\n" +
                "Save files and achievement files will be written to their recorded locations. Existing files at those paths will be overwritten.",
                "Confirm restore",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirmation != MessageBoxResult.Yes) { RestoreStatus = "Restore cancelled."; return; }

            RestoreResults.Clear();
            RestoreStatus = "Restoring…";
            var restored = 0;
            var failedRestore = 0;
            var gseRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GSE Saves");
            foreach (var plan in plans)
            {
                foreach (var save in plan.Saves)
                {
                    try
                    {
                        var bytes = await _githubSyncClient.ReadRestoreFileAsync(token, target, plan.HeadSha, save.RepoPath);
                        if (bytes is null) { failedRestore++; RestoreResults.Add($"{plan.AppId} — {save.RepoPath} missing in repository"); continue; }

                        if (save.IsAchievementJson)
                        {
                            continue; // achievements are restored explicitly below (remote or local source) and are not part of the save payload
                        }
                        else
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(save.TargetPath)!);
                            await File.WriteAllBytesAsync(save.TargetPath, bytes);
                            restored++;
                            RestoreResults.Add($"{plan.AppId} — restored {save.RepoPath}");
                        }
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
                    {
                        failedRestore++;
                        RestoreResults.Add($"{plan.AppId} — {save.RepoPath} failed: {exception.Message}");
                    }
                }
            }

            // Achievements are restored explicitly from the achievements pipeline (remote file, or local metadata as fallback).
            foreach (var plan in plans)
            {
                try
                {
                    var achievementsJson = plan.AchievementsJson;
                    if (achievementsJson is null)
                    {
                        var localAchievement = Path.Combine(MetadataRoot, plan.AppId, "achievements.json");
                        if (File.Exists(localAchievement))
                        {
                            achievementsJson = await File.ReadAllTextAsync(localAchievement);
                            RestoreResults.Add($"{plan.AppId} — remote achievements.json not found; using the local cache instead");
                        }
                    }
                    if (achievementsJson is null)
                    {
                        failedRestore++;
                        RestoreResults.Add($"{plan.AppId} — achievements.json is missing remotely and locally; skipping achievement restore");
                        continue;
                    }
                    if (plan.Manifest.AchievementSourcePath is null && string.IsNullOrWhiteSpace(plan.Manifest.AchievementSourceName))
                    {
                        failedRestore++;
                        RestoreResults.Add($"{plan.AppId} — the manifest does not record the original achievement file path; skipping");
                        continue;
                    }

                    var expandedTarget = plan.Manifest.AchievementSourcePath is null ? null : Environment.ExpandEnvironmentVariables(plan.Manifest.AchievementSourcePath);
                    var targetPath = !string.IsNullOrWhiteSpace(expandedTarget)
                        && Directory.Exists(Path.GetDirectoryName(expandedTarget))
                            ? expandedTarget
                            : Path.Combine(
                                Directory.Exists(Path.Combine(gseRoot, plan.AppId))
                                    ? Path.Combine(gseRoot, plan.AppId)
                                    : Path.Combine(gseRoot, plan.AppId),
                                plan.Manifest.AchievementSourceName ?? "achievements.json");
                    Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                    if (string.Equals(plan.Manifest.AchievementFormat, "ini", StringComparison.OrdinalIgnoreCase))
                    {
                        await File.WriteAllBytesAsync(targetPath, IniAchievementWriter.FromViewerJson(achievementsJson));
                    }
                    else
                    {
                        await File.WriteAllTextAsync(targetPath, achievementsJson);
                    }

                    restored++;
                    RestoreResults.Add($"{plan.AppId} — restored {Path.GetFileName(targetPath)} ({(plan.Manifest.AchievementFormat == "ini" ? "ini" : "json")})");
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    failedRestore++;
                    RestoreResults.Add($"{plan.AppId} — achievements restore failed: {exception.Message}");
                }
            }

            RestoreStatus = $"Restore complete: {restored:N0} file(s) restored, {failedRestore:N0} failed.";
            OnPropertyChanged(nameof(HasRestoreReport));
            AppLog.Info(RestoreStatus);
        }
        catch (Exception exception)
        {
            if (exception is GitHubAuthorizationException authorizationFailure) HandleGitHubAuthorizationFailure(authorizationFailure);
            RestoreStatus = exception is GitHubApiException or GitHubAuthorizationException or ArgumentException or InvalidDataException
                ? "Restore failed: " + exception.Message
                : "Restore failed. Check the local log.";
            GitHubStatus = RestoreStatus;
            AppLog.Warning($"Save restore failed: {exception}");
        }
        finally
        {
            IsRestoreRunning = false;
        }
    }

    public void SetManualGameName(GameCardViewModel game, string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            _settings.ManualGameNames.Remove(game.AppId);
            game.ApplyManualTitle(null);
        }
        else
        {
            var cleaned = title.Trim();
            _settings.ManualGameNames[game.AppId] = cleaned;
            game.ApplyManualTitle(cleaned);
        }

        SaveSettings();
        AppLog.Action($"Changed the library name for AppID {game.AppId}.");
        RefreshGamesView();
    }

    private async Task LoadMetadataAsync(GameCardViewModel game)
    {
        await _metadataSlots.WaitAsync();
        try
        {
            var metadata = await _metadataCache.GetAsync(game.AppId);
            if (metadata is null || Application.Current is null) return;

            if (metadata.AchievementsTotal is { } achievementTotal)
                AppLog.Info($"Loaded Steam metadata for AppID {game.AppId}; declaredAchievements={achievementTotal}.");

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                game.ApplyMetadata(metadata);
            });
        }
        catch (Exception exception)
        {
            AppLog.Warning($"Could not load public metadata for AppID {game.AppId}: {exception.GetType().Name}.");
        }
        finally
        {
            _metadataSlots.Release();
        }
    }

    private async Task LoadViewerDataAsync(GameCardViewModel game)
    {
        await _viewerDataSlots.WaitAsync();
        try
        {
            if (NeedsCatalogMetadata(game.Profile))
            {
                var catalogResult = await _viewerGameInfoClient.GetCatalogAsync(
                    _settings.GitHubOwner,
                    _settings.GitHubRepository,
                    _settings.GitHubBranch,
                    game.AppId);

                if (catalogResult.Success && catalogResult.Catalog is not null)
                {
                    var cachedCatalog = await _localCatalogCache.CacheCatalogAsync(
                        game.AppId, catalogResult.Catalog, source: "viewer");
                    var catalog = cachedCatalog ?? catalogResult.Catalog;
                    await Application.Current!.Dispatcher.InvokeAsync(() => game.ApplyCatalog(catalog));
                    AppLog.Info($"Loaded public viewer catalog for AppID {game.AppId}; entries={catalogResult.Catalog.Achievements.Count}.");
                }
                else if (catalogResult.Status is not (ViewerCatalogStatus.NotConfigured or ViewerCatalogStatus.NotFound))
                {
                    AppLog.Warning($"Could not load viewer catalog for AppID {game.AppId}: {catalogResult.Status}.");
                }

                // The community page is a public source independent of GitHub viewer settings.
                // It can expose only part of a game's catalog, so keep the saved completeness flag.
                if (NeedsCatalogMetadata(game.Profile))
                {
                    var communityResult = await _steamCommunityCatalogClient.GetCatalogAsync(game.AppId, game.Title);
                    if (communityResult is not null)
                    {
                        var cachedCatalog = await _localCatalogCache.CacheCatalogAsync(
                            game.AppId,
                            communityResult.Catalog,
                            source: "steam-community",
                            declaredTotal: communityResult.DeclaredTotal);
                        var catalog = cachedCatalog ?? communityResult.Catalog;
                        await Application.Current!.Dispatcher.InvokeAsync(() => game.ApplyCatalog(catalog));
                        var cachedImages = CountCachedAchievementImages(game.AppId);
                        AppLog.Info($"Loaded Steam Community achievement details for AppID {game.AppId}; entries={catalog.Achievements.Count}; cachedImages={cachedImages}; complete={catalog.HasCompleteCatalog}.");
                    }
                    else
                    {
                        AppLog.Info($"No public Steam Community achievement catalog was available for AppID {game.AppId}.");
                    }
                }
            }

            var progressResult = await _viewerProgressClient.GetProgressAsync(
                _settings.GitHubOwner,
                _settings.GitHubRepository,
                _settings.GitHubBranch,
                game.AppId);
            if (progressResult.Success && progressResult.Progress is not null)
            {
                await Application.Current!.Dispatcher.InvokeAsync(() => game.ApplyViewerProgress(progressResult.Progress));
                AppLog.Info($"Loaded public viewer progress for AppID {game.AppId}; entries={progressResult.Progress.Achievements.Count}.");
            }
            else if (progressResult.Status is not (ViewerProgressStatus.NotConfigured or ViewerProgressStatus.NotFound))
            {
                AppLog.Warning($"Could not load public viewer progress for AppID {game.AppId}: {progressResult.Status}.");
            }

            if (game.Profile.HasCompleteCatalog || progressResult.Success)
            {
                // UI refresh is handled by the scan batcher after all per-game loads finish.
            }
        }
        catch (Exception exception)
        {
            AppLog.Error($"Unexpected error while loading public viewer data for AppID {game.AppId}.", exception);
        }
        finally
        {
            _viewerDataSlots.Release();
        }
    }

    private async Task CacheScreenshotsForGamesAsync(IReadOnlyList<string> appIds)
    {
        if (appIds.Count == 0) return;
        AppLog.Action($"Started background SteamDB screenshot caching for {appIds.Count} game(s).");
        using var parallelism = new SemaphoreSlim(3, 3);
        var tasks = appIds.Select(async appId =>
        {
            await parallelism.WaitAsync().ConfigureAwait(false);
            try
            {
                await _screenshotCache.PrecacheAsync(appId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                AppLog.Warning($"Background screenshot caching failed for AppID {appId}: {exception.GetType().Name}.");
            }
            finally
            {
                parallelism.Release();
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
        AppLog.Action($"Finished background SteamDB screenshot caching for {appIds.Count} game(s).");
    }

    private static bool NeedsCatalogMetadata(GameProfile profile) =>
        !profile.HasCompleteCatalog || profile.Achievements.Any(achievement =>
            string.IsNullOrWhiteSpace(achievement.DisplayName)
            || (!achievement.IsHidden && string.IsNullOrWhiteSpace(achievement.Description))
            || !IsCachedMetadataIcon(profile.AppId, achievement.IconUri));

    private static bool IsCachedMetadataIcon(string appId, string? iconUri)
    {
        if (!Uri.TryCreate(iconUri, UriKind.Absolute, out var uri) || !uri.IsFile) return false;
        try
        {
            var imagesDirectory = Path.GetFullPath(Path.Combine(MetadataRoot, appId, "images"));
            var iconPath = Path.GetFullPath(uri.LocalPath);
            var relative = Path.GetRelativePath(imagesDirectory, iconPath);
            return !Path.IsPathRooted(relative)
                && !relative.Equals("..", StringComparison.Ordinal)
                && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && File.Exists(iconPath);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException
                                           or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static int CountCachedAchievementImages(string appId)
    {
        var directory = Path.Combine(MetadataRoot, appId, "images");
        try { return Directory.Exists(directory) ? Directory.EnumerateFiles(directory).Count() : 0; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return 0; }
    }


    private void UpdateLibrarySummary()
    {
        var summary = GameLibrarySummary.Calculate(Games.Select(game => game.Profile));
        TotalGames = summary.GameCount;
        TotalUnlocked = summary.TotalUnlocked;
        TotalKnownAchievements = summary.TotalKnownAchievements;
        CompletedGames = summary.CompletedGameCount;
        CompletionText = summary.CompletionPercent is { } completion ? $"{completion:0.#}%" : "—";
        CompletionCoverageText = summary.MeasurableGameCount == 0
            ? "No games have measurable completion yet"
            : $"Across {summary.MeasurableGameCount:N0} of {summary.GameCount:N0} games with known achievement totals; unlisted achievements count as locked";
        AchievementTotalText = summary.TotalCatalogAchievements is { } catalogTotal
            ? $"{catalogTotal:N0} total achievements · {summary.TotalKnownAchievements:N0} known states"
            : $"{summary.TotalKnownAchievements:N0} known player states · catalog totals unavailable";
        MeasurableGamesText = $"{summary.MeasurableGameCount:N0} games with measurable progress · {summary.GamesMissingCatalog:N0} missing a total · {summary.GamesWithUnknownPlayerState:N0} with saved states missing";
    }

    private void RefreshPlaytimeSnapshots(DateTimeOffset nowUtc)
    {
        var snapshots = _gameplayTracker.GetSnapshots(nowUtc).ToDictionary(snapshot => snapshot.AppId, StringComparer.Ordinal);
        foreach (var game in Games)
        {
            if (snapshots.TryGetValue(game.AppId, out var snapshot))
            {
                var wasRunning = _gameRunningStates.TryGetValue(game.AppId, out var r) && r;
                var isRunning = snapshot.IsRunning;

                game.ApplyPlaytime(snapshot);

                if (_playtimeBaselineInitialized)
                {
                    if (!wasRunning && isRunning)
                    {
                        _gameRunningStates[game.AppId] = true;
                        _gameSessionStartedAt[game.AppId] = snapshot.CurrentSessionStartedAtUtc ?? nowUtc;
                        GameSessionStateChanged?.Invoke(this, new GameSessionEventArgs(game, true, TimeSpan.Zero, snapshot.TotalPlaytime));
                    }
                    else if (wasRunning && !isRunning)
                    {
                        _gameRunningStates[game.AppId] = false;
                        var started = _gameSessionStartedAt.TryGetValue(game.AppId, out var s) ? s : nowUtc;
                        var duration = nowUtc > started ? nowUtc - started : TimeSpan.Zero;
                        _gameSessionStartedAt.Remove(game.AppId);
                        GameSessionStateChanged?.Invoke(this, new GameSessionEventArgs(game, false, duration, snapshot.TotalPlaytime));
                    }
                }
                else
                {
                    _gameRunningStates[game.AppId] = isRunning;
                    if (isRunning && snapshot.CurrentSessionStartedAtUtc.HasValue)
                    {
                        _gameSessionStartedAt[game.AppId] = snapshot.CurrentSessionStartedAtUtc.Value;
                    }
                }
            }
        }
        _playtimeBaselineInitialized = true;
    }

    private bool FilterGame(object item)
    {
        if (item is not GameCardViewModel game) return false;
        var matchesFilter = SelectedGameFilter switch
        {
            "Measurable progress" => game.Profile.CompletionPercent.HasValue,
            "Completed" => game.Profile.CompletionPercent == 100,
            "Needs information" => !game.Profile.TotalCount.HasValue
                || game.Profile.Achievements.Any(achievement => !achievement.IsUnlocked.HasValue),
            _ => true
        };
        if (!matchesFilter) return false;
        return string.IsNullOrWhiteSpace(SearchText)
            || game.Title.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase)
            || game.AppId.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshGamesView()
    {
        if (GamesView is ListCollectionView listView)
        {
            listView.CustomSort = new GameCardComparer(SelectedGameSort);
        }
        GamesView.Refresh();
        IsGameResultsEmpty = GamesView.IsEmpty;
        OnPropertyChanged(nameof(GameResultsEmptyText));
        OnPropertyChanged(nameof(GameResultsEmptyHint));
    }

    public string GameResultsEmptyText => IsLibraryEmpty
        ? IsScanning
            ? "Scanning your enabled sources for achievement profiles…"
            : "No supported profiles have been discovered yet."
        : "No games match this search and filter. Clear or change them to see the library.";

    public string GameResultsEmptyHint => IsLibraryEmpty
        ? IsScanning
            ? "AchievementSync is checking enabled local folders. Source files are read only."
            : "Add or enable a folder in Settings to load achievement profiles. Missing folders are safe to ignore."
        : "Clear or change the search and progress filter to see games.";

    private void OnLocationChanged(LocationOptionViewModel changed)
    {
        if (changed.IsCustom)
        {
            var id = changed.Id.StartsWith("custom-", StringComparison.Ordinal) ? changed.Id[7..] : changed.Id;
            var custom = _settings.CustomLocations.FirstOrDefault(item => item.Id == id);
            if (custom is not null) custom.Enabled = changed.IsEnabled;
        }
        else
        {
            _settings.EnabledLocations[changed.Id] = changed.IsEnabled;
        }

        SaveSettings();
        _ = ScanAsync();
    }

    private void RemoveLocation(LocationOptionViewModel? location)
    {
        if (location is null || !location.IsCustom) return;
        Locations.Remove(location);
        var id = location.Id.StartsWith("custom-", StringComparison.Ordinal) ? location.Id[7..] : location.Id;
        _settings.CustomLocations.RemoveAll(item => item.Id == id);
        SaveSettings();
        _ = ScanAsync();
    }

    private void SaveSettings()
    {
        try
        {
            _settingsStore.Save(_settings);
            AppLog.Action("Saved application settings.");
        }
        catch (Exception exception)
        {
            AppLog.Error("Could not save application settings.", exception);
            ScanStatus = "Settings could not be saved. Check the local log.";
        }
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.0} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):0.0} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):0.0} GB";
    }

    public async Task ScanPlatformAsync(string platform)
    {
        var selected = PlatformStatuses.FirstOrDefault(item =>
            string.Equals(item.Platform, platform, StringComparison.OrdinalIgnoreCase));
        if (selected is null) return;

        await _platformScanGate.WaitAsync();
        try
        {
            IsPlatformScanRunning = true;
            PlatformScanStatus = $"Scanning {selected.DisplayName}...";
            PlatformStatuses = PlatformStatuses.Select(item =>
                string.Equals(item.Platform, selected.Platform, StringComparison.OrdinalIgnoreCase)
                    ? item with { Status = "Scanning..." }
                    : item).ToArray();
            var options = BuildPlatformSourceOptions();
            var report = await _platformScanService.ScanAsync(options, [selected.Platform]);
            if (report.Platforms.Count > 0)
            {
                var updated = report.Platforms.ToDictionary(item => item.Platform, StringComparer.OrdinalIgnoreCase);
                PlatformStatuses = PlatformStatuses.Select(item =>
                    updated.TryGetValue(item.Platform, out var replacement) ? replacement : item).ToArray();
            }
            PlatformScanStatus = report.GamesConverted > 0
                ? $"{selected.DisplayName}: converted {report.GamesConverted} game(s), wrote {report.FilesWritten} file(s)."
                : $"{selected.DisplayName}: no platform achievement data was found.";
            if (report.Messages.Count > 0)
                PlatformScanStatus += " Details: " + string.Join("; ", report.Messages.Take(5));
            await RefreshLibraryProgressFromMetadataAsync();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or HttpRequestException or TaskCanceledException)
        {
            PlatformScanStatus = $"Platform scan failed: {exception.GetType().Name}.";
            if (_enableDiagnostics) AppLog.Warning($"Platform scan failed: {exception.Message}");
        }
        finally
        {
            IsPlatformScanRunning = false;
            _platformScanGate.Release();
        }
    }

    private async Task RefreshLibraryProgressFromMetadataAsync()
    {
        var games = Games.ToArray();
        var updates = await Task.Run(() =>
        {
            var results = new List<(GameCardViewModel Game, GameProfile? Profile, ParsedProfile? Catalog)>();
            foreach (var game in games)
            {
                var path = Path.Combine(MetadataRoot, game.AppId, "achievements.json");
                var parsed = File.Exists(path) ? _metadataProgressParser.Parse(path, game.AppId).Profile : null;
                var catalog = _localCatalogCache.LoadCatalog(game.AppId);
                if (parsed is not null || catalog is not null)
                    results.Add((game, parsed is null ? null : ProfileMerger.Merge([parsed]).Single(), catalog));
            }
            return results.ToArray();
        });

        if (updates.Length == 0 || Application.Current?.Dispatcher is not { } dispatcher) return;
        await dispatcher.InvokeAsync(() =>
        {
            foreach (var update in updates)
            {
                if (update.Profile is not null) update.Game.ApplyLocalProfileUpdate(update.Profile);
                if (update.Catalog is not null) update.Game.ApplyCatalog(update.Catalog);
            }
            UpdateLibrarySummary();
            RefreshGamesView();
        });

        foreach (var update in updates)
        {
            try
            {
                await Task.Run(() => ViewerAchievementsWriter.WriteAchievementsJson(
                    update.Game.AppId, update.Game.Profile.Achievements));
            }
            catch (Exception exception)
            {
                AppLog.Warning($"Could not update viewer progress after the platform scan for AppID {update.Game.AppId}: {exception.GetType().Name}.");
            }
        }
    }

    private PlatformSourceOptions BuildPlatformSourceOptions()
    {
        var options = PlatformSourceOptions.CreateDefault();
        foreach (var kv in _settings.PlatformSourceRoots)
        {
            var key = kv.Key.ToLowerInvariant();
            var value = kv.Value;
            if (string.IsNullOrWhiteSpace(value)) continue;
            var expanded = Environment.ExpandEnvironmentVariables(value);
            switch (key)
            {
                case "steam": options.SteamRoot = expanded; break;
                case "xblen": options.XblenRoot = expanded; break;
                case "xboxpc": options.XboxPcRoot = expanded; break;
                case "xbox360": options.Xbox360Root = expanded; break;
                case "gfwl": options.GfwlRoot = expanded; break;
                case "rpcs3": options.Rpcs3Root = expanded; break;
                case "ps4": options.Ps4Root = expanded; break;
                case "ps5": options.Ps5Root = expanded; break;
                case "xenia": options.XeniaRoot = expanded; break;
                case "epic": options.EpicRoot = expanded; break;
                case "gog": options.GogRoot = expanded; break;
                case "uplay": options.UplayRoot = expanded; break;
                case "uplaycache": options.UplayCacheRoot = expanded; break;
                case "uplayrune": options.UplayRuneRoot = expanded; break;
                case "ea": options.EaRoot = expanded; break;
                case "recomp": options.RecompRoot = expanded; break;
                case "ladxhd": options.LadxhdRoot = expanded; break;
            }
        }
        options.RetroAchievementsUser = _settings.RetroAchievementsUser;
        options.RetroAchievementsApiKey = ReadPlatformCredential(_retroAchievementsKeyStore);

        options.XboxEntraClientId = _settings.XboxClientId;
        var xboxCred = XboxStoredCredential.Parse(ReadPlatformCredential(_xboxCredentialStore));
        if (xboxCred is not null)
        {
            options.XboxXstsToken = xboxCred.XstsToken;
            options.XboxUhs = xboxCred.Uhs;
            options.XboxXuid = xboxCred.Xuid;
        }

        options.EpicClientId = _settings.EpicClientId;
        var epicCred = EpicStoredCredential.Parse(ReadPlatformCredential(_epicCredentialStore));
        if (epicCred is not null)
        {
            options.EpicAccessToken = epicCred.AccessToken;
            options.EpicAccountId = epicCred.AccountId;
        }

        options.LumaPlayWatcherEnabled = _settings.LumaPlayWatcherEnabled;
        return options;
    }

    private static string GetPlatformRoot(PlatformSourceOptions options, string key) => key switch
    {
        "steam" => options.SteamRoot ?? string.Empty,
        "xblen" => options.XblenRoot ?? string.Empty,
        "xboxpc" => options.XboxPcRoot ?? string.Empty,
        "xbox360" => options.Xbox360Root ?? string.Empty,
        "gfwl" => options.GfwlRoot ?? string.Empty,
        "rpcs3" => options.Rpcs3Root ?? string.Empty,
        "ps4" => options.Ps4Root ?? string.Empty,
        "ps5" => options.Ps5Root ?? string.Empty,
        "epic" => options.EpicRoot ?? string.Empty,
        "gog" => options.GogRoot ?? string.Empty,
        "uplay" => options.UplayRoot ?? string.Empty,
        "uplaycache" => options.UplayCacheRoot ?? string.Empty,
        "uplayrune" => options.UplayRuneRoot ?? string.Empty,
        "ea" => options.EaRoot ?? string.Empty,
        "recomp" => options.RecompRoot ?? string.Empty,
        "ladxhd" => options.LadxhdRoot ?? string.Empty,
        _ => string.Empty
    };

    private void UpdatePlatformSourceRoot(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) _settings.PlatformSourceRoots.Remove(key);
        else _settings.PlatformSourceRoots[key] = value;
        SaveSettings();
        _platformSourceOptions = BuildPlatformSourceOptions();
        OnPropertyChanged(nameof(PlatformSourceOptions));
        _platformAdapters.Register(new SteamDataAdapter(_platformSourceOptions));
        PlatformSourceRootsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SavePlatformCredentials(string? retroAchievementsApiKey, string? xboxCredentialJson = null, string? epicCredentialJson = null)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(retroAchievementsApiKey))
                StorePlatformCredential(_retroAchievementsKeyStore, retroAchievementsApiKey);
            if (!string.IsNullOrWhiteSpace(xboxCredentialJson))
                StorePlatformCredential(_xboxCredentialStore, xboxCredentialJson);
            if (!string.IsNullOrWhiteSpace(epicCredentialJson))
                StorePlatformCredential(_epicCredentialStore, epicCredentialJson);

            _platformSourceOptions = BuildPlatformSourceOptions();
            UpdatePlatformCredentialStatus();
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or PlatformNotSupportedException or ArgumentException or InvalidOperationException)
        {
            PlatformCredentialStatus = "Credentials could not be saved. Check Windows Credential Manager access and try again.";
            AppLog.Warning($"Platform API credentials could not be saved: {exception.GetType().Name}.");
        }
    }

    public void ClearPlatformCredentials()
    {
        try
        {
            _retroAchievementsKeyStore.Delete();
            _xboxCredentialStore.Delete();
            _epicCredentialStore.Delete();
            _platformSourceOptions = BuildPlatformSourceOptions();
            UpdatePlatformCredentialStatus();
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or PlatformNotSupportedException or InvalidOperationException)
        {
            PlatformCredentialStatus = "Credentials could not be cleared. Check Windows Credential Manager access and try again.";
            AppLog.Warning($"Platform API credentials could not be cleared: {exception.GetType().Name}.");
        }
    }

    private async Task OpenMappingReviewAsync()
    {
        var options = BuildPlatformSourceOptions();
        var resolutions = new List<SteamAppIdResolution>();
        var resolver = new SteamAppIdResolver(
            SteamDbAppIdLookup.LookupAppIdAsync,
            appId => _localCatalogCache.LoadCatalog(appId)?.Achievements);

        foreach (var adapter in _platformScanService.Adapters)
        {
            try
            {
                if (!adapter.IsConfiguredFor(options)) continue;
                var report = await adapter.CollectAsync(options).ConfigureAwait(false);
                foreach (var game in report.Games)
                {
                    var resolution = await resolver.ResolveAsync(
                        game,
                        _settings.SteamAppIdOverrides,
                        _settings.PlatformBlacklist).ConfigureAwait(false);
                    resolutions.Add(resolution);
                }
            }
            catch (Exception ex)
            {
                AppLog.Warning($"Mapping review collection error ({adapter.Platform}): {ex.Message}");
            }
        }

        foreach (var (key, targetAppId) in _settings.SteamAppIdOverrides)
        {
            var parts = key.Split(':', 2);
            var plat = parts.Length > 1 ? parts[0] : "custom";
            var srcId = parts.Length > 1 ? parts[1] : parts[0];
            if (!resolutions.Any(r => string.Equals(r.Platform, plat, StringComparison.OrdinalIgnoreCase) && string.Equals(r.SourceGameId, srcId, StringComparison.OrdinalIgnoreCase)))
            {
                resolutions.Add(new SteamAppIdResolution(
                    plat, srcId, srcId, targetAppId, SteamMappingConfidence.Override,
                    "Saved manual override", true, _settings.PlatformBlacklist.Contains(key), [], 0));
            }
        }

        await Application.Current!.Dispatcher.InvokeAsync(() =>
        {
            var vm = new MappingReviewViewModel(resolutions);
            var dialog = new Views.MappingReviewDialog(vm)
            {
                Owner = Application.Current.MainWindow
            };
            if (dialog.ShowDialog() == true)
            {
                var changed = false;
                foreach (var item in vm.Items)
                {
                    var key = item.OverrideKey;
                    if (!string.IsNullOrWhiteSpace(item.OverrideAppId))
                    {
                        if (!_settings.SteamAppIdOverrides.TryGetValue(key, out var cur) || !string.Equals(cur, item.OverrideAppId, StringComparison.Ordinal))
                        {
                            _settings.SteamAppIdOverrides[key] = item.OverrideAppId;
                            changed = true;
                        }
                    }
                    else if (_settings.SteamAppIdOverrides.Remove(key))
                    {
                        changed = true;
                    }

                    if (item.IsBlacklisted)
                    {
                        if (_settings.PlatformBlacklist.Add(key)) changed = true;
                    }
                    else
                    {
                        if (_settings.PlatformBlacklist.Remove(key)) changed = true;
                    }
                }

                if (changed)
                {
                    SaveSettings();
                    OnPropertyChanged(nameof(PlatformSourceOptions));
                }
            }
        });
    }

    private void MigrateLegacyPlatformCredentials()
    {
        var changed = false;
        var raKey = _settings.LegacyRetroAchievementsApiKey;
        Migrate(_retroAchievementsKeyStore, ref raKey);
        _settings.LegacyRetroAchievementsApiKey = raKey;
        changed |= !string.IsNullOrWhiteSpace(_settings.LegacyEpicAccessToken)
                   || !string.IsNullOrWhiteSpace(_settings.LegacyGogAccessToken);
        _settings.LegacyEpicAccessToken = string.Empty;
        _settings.LegacyGogAccessToken = string.Empty;
        if (changed) SaveSettings();

        void Migrate(WindowsCredentialTokenStore store, ref string legacy)
        {
            if (string.IsNullOrWhiteSpace(legacy)) return;
            try
            {
                if (string.IsNullOrWhiteSpace(store.Read())) store.Write(legacy);
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or PlatformNotSupportedException or ArgumentException or InvalidOperationException)
            {
                AppLog.Warning($"A legacy platform credential could not be migrated: {exception.GetType().Name}.");
            }
            finally
            {
                legacy = string.Empty;
                changed = true;
            }
        }
    }

    private static string? ReadPlatformCredential(WindowsCredentialTokenStore store)
    {
        try { return store.Read(); }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or PlatformNotSupportedException or InvalidOperationException)
        {
            AppLog.Warning($"A platform credential could not be read: {exception.GetType().Name}.");
            return null;
        }
    }

    private static void StorePlatformCredential(WindowsCredentialTokenStore store, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (value.Length > 5000) throw new ArgumentException("A platform credential is too long.");
        store.Write(value.Trim());
    }

    private void UpdatePlatformCredentialStatus()
    {
        var ra = !string.IsNullOrWhiteSpace(ReadPlatformCredential(_retroAchievementsKeyStore));
        var xbox = XboxStoredCredential.Parse(ReadPlatformCredential(_xboxCredentialStore)) is not null;
        var epic = EpicStoredCredential.Parse(ReadPlatformCredential(_epicCredentialStore)) is not null;
        var parts = new List<string>();
        parts.Add($"RetroAchievements: {(ra ? "saved" : "not set")}");
        parts.Add($"Xbox PC: {(xbox ? "connected" : "not set")}");
        parts.Add($"Epic Games: {(epic ? "connected" : "not set")}");
        PlatformCredentialStatus = string.Join(" | ", parts) + ". Stored in Windows Credential Manager.";
        OnPropertyChanged(nameof(PlatformCredentialStatus));
    }

    private void OpenEnablerWizard()
    {
        CurrentPage = "Enabler";
    }

    public void Dispose()
    {
        _viewerCatalogHttp.Dispose();
        _githubHttpClient.Dispose();
        _metadataSlots.Dispose();
        _viewerDataSlots.Dispose();
        _changedSourceGate.Dispose();
        GC.SuppressFinalize(this);
    }
}

internal sealed class AsyncCommand(Func<Task> execute, Func<bool>? canExecute = null) : ICommand
{
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public async void Execute(object? parameter)
    {
        try
        {
            await execute();
        }
        catch (Exception exception)
        {
            AppLog.Error("An asynchronous UI command failed.", exception);
            MessageBox.Show(
                "The requested operation could not be completed. Check the local log for details.",
                "AchievementSync operation failed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}

public sealed record NotificationSoundOption(string Code, string Label)
{
    public override string ToString() => Label;
}

internal sealed class AsyncParameterCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;

    public async void Execute(object? parameter)
    {
        try
        {
            await execute(parameter);
        }
        catch (Exception exception)
        {
            AppLog.Error("An asynchronous UI command failed.", exception);
            MessageBox.Show(
                "The requested operation could not be completed. Check the local log for details.",
                "AchievementSync operation failed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}

internal sealed class RelayCommand(Action<object?> execute, Func<bool>? canExecute = null) : ICommand
{
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => execute(parameter);
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}

internal sealed class GameCardComparer(string sort) : IComparer
{
    public int Compare(object? left, object? right)
    {
        if (ReferenceEquals(left, right)) return 0;
        if (left is not GameCardViewModel a) return 1;
        if (right is not GameCardViewModel b) return -1;

        var comparison = sort switch
        {
            "Progress" => CompareProgress(a, b),
            "Recent unlock" => (b.Profile.LatestUnlocked?.UnlockTimeUnix ?? 0)
                .CompareTo(a.Profile.LatestUnlocked?.UnlockTimeUnix ?? 0),
            _ => StringComparer.CurrentCultureIgnoreCase.Compare(a.Title, b.Title)
        };
        return comparison != 0 ? comparison : StringComparer.CurrentCultureIgnoreCase.Compare(a.Title, b.Title);
    }

    private static int CompareProgress(GameCardViewModel a, GameCardViewModel b)
    {
        var known = b.Profile.CompletionPercent.HasValue.CompareTo(a.Profile.CompletionPercent.HasValue);
        return known != 0 ? known : (b.Profile.CompletionPercent ?? 0).CompareTo(a.Profile.CompletionPercent ?? 0);
    }
}
