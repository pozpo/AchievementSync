using System.IO;
using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using AchievementSync.Core.Models;
using AchievementSync.Core.Metadata;
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

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly AchievementScanner _scanner = new();
    private readonly LocalAchievementMetadataCache _localMetadataCache;
    private readonly LocalAchievementCatalogCache _localCatalogCache;
    private readonly PlatformAdapterRegistry _platformAdapters = new();
    private readonly SteamMetadataCache _metadataCache;
    private readonly SemaphoreSlim _metadataSlots = new(5, 5);
    private readonly SemaphoreSlim _viewerDataSlots = new(4, 4);
    private readonly HttpClient _viewerCatalogHttp = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly ViewerGameInfoClient _viewerGameInfoClient;
    private readonly ViewerAchievementProgressClient _viewerProgressClient;
    private readonly SettingsStore _settingsStore = new();
    private readonly HttpClient _githubHttpClient = new() { Timeout = TimeSpan.FromSeconds(45) };
    private readonly GitHubDeviceFlowClient _githubDeviceFlow;
    private readonly GitHubAchievementSyncClient _githubSyncClient;
    private readonly WindowsCredentialTokenStore _githubTokenStore = new();
    private readonly WindowsCredentialTokenStore _retroAchievementsKeyStore = new("AchievementSync/Platforms/RetroAchievementsApiKey");
    private readonly GameplaySessionTracker _gameplayTracker;
    private readonly AchievementUnlockTracker _unlockTracker;
    private readonly IStartupRegistration _startupRegistration;
    private readonly SemaphoreSlim _changedSourceGate = new(1, 1);
    private IReadOnlyList<DiscoveredAchievementFile> _sourceFiles = [];
    private readonly bool _enableDiagnostics;
    private AppSettings _settings;
    private string _searchText = string.Empty;
    private string _scanStatus = "Scanning your local folders when the app opens";
    private string _lastScanText = "Not scanned yet";
    private string _currentPage = "Games";
    private bool _isScanning;
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
    private string _backupStatus = "Back up save files and achievements to your repository. Nothing is uploaded automatically.";
    private string _restoreStatus = "Restore save files and achievements from your repository. This overwrites local files only after you confirm.";
    private string _selectedGameFilter = "All games";
    private string _selectedGameSort = "Name";
    private string _startupStatus = "Windows startup is off.";
    private RelayCommand? _browseNotificationSoundCommand;
    private System.Windows.Media.Imaging.BitmapImage? _githubAvatarImage;

    // Platforms
    private readonly PlatformScanService _platformScanService = new();
    private readonly AchievementEnablerService _enablerService;
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
        ("gog", "GOG Galaxy"), ("uplay", "Ubisoft Connect")
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
        _platformScanService.Register(new GfwlDataAdapter());
        _platformScanService.Register(new Xbox360DataAdapter());
        _platformScanService.Register(new PlayStationDataAdapter());
        _platformScanService.Register(new GogDataAdapter(_platformSourceOptions));
        _platformScanService.Register(new EpicDataAdapter(_platformSourceOptions, _viewerCatalogHttp));
        _platformScanService.Register(new UplayDataAdapter(_platformSourceOptions));
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

        _viewerGameInfoClient = new ViewerGameInfoClient(_viewerCatalogHttp);
        _viewerProgressClient = new ViewerAchievementProgressClient(_viewerCatalogHttp);
        _githubDeviceFlow = new GitHubDeviceFlowClient(_githubHttpClient);
        _githubSyncClient = new GitHubAchievementSyncClient(_githubHttpClient);
        Locations = new ObservableCollection<LocationOptionViewModel>(
            LocationCatalog.Build(_settings).Select(location => new LocationOptionViewModel(location, OnLocationChanged)));
        Games = new ObservableCollection<GameCardViewModel>();
        GamesView = CollectionViewSource.GetDefaultView(Games);
        GamesView.Filter = FilterGame;
        ScanCommand = new AsyncCommand(ScanAsync);
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
        ClearBulkSyncReportCommand = new RelayCommand(_ => ClearBulkSyncReport());
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
    public ICommand ScanCommand { get; }
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
    public ICommand ClearBulkSyncReportCommand { get; }
    public ObservableCollection<string> BulkSyncResults { get; } = [];
    public ObservableCollection<string> BackupResults { get; } = [];
    public ObservableCollection<string> RestoreResults { get; } = [];
    public string BulkSyncStatus { get => _bulkSyncStatus; private set => Set(ref _bulkSyncStatus, value); }
    public string AutoSyncStatus { get => _autoSyncStatus; private set => Set(ref _autoSyncStatus, value); }
    public string BackupStatus { get => _backupStatus; private set => Set(ref _backupStatus, value); }
    public string RestoreStatus { get => _restoreStatus; private set => Set(ref _restoreStatus, value); }
    public bool HasBackupReport => BackupResults.Count > 0;
    public bool HasRestoreReport => RestoreResults.Count > 0;
    public bool HasBulkSyncReport => BulkSyncResults.Count > 0;

    private void RefreshAutoSyncStatus()
    {
        if (!AutoSyncAfterScan && !AutoSyncAfterUnlock)
            AutoSyncStatus = "Automatic sync is off.";
        else if (!IsGitHubSignedIn)
            AutoSyncStatus = "Automatic sync is saved, but GitHub must be connected before it can run.";
        else if (AutoSyncAfterScan && !_settings.HasAuthorizedFirstUpload)
            AutoSyncStatus = "After-scan sync is on. The first upload will ask you to review and confirm it.";
        else if (AutoSyncAfterScan)
            AutoSyncStatus = "After-scan sync is on. Later uploads run after each completed scan.";
        else
            AutoSyncStatus = "New-unlock sync is on. It pushes AppID files after a genuine unlock.";
    }

    public bool CanSyncAll => IsGitHubSignedIn && !_isSyncAllRunning && !_isBackupRunning && !_isRestoreRunning && !_isPushRunning && !_isPushPreviewRunning;
    public bool CanBackup => IsGitHubSignedIn && !_isBackupRunning && !_isRestoreRunning && !_isSyncAllRunning && !_isPushRunning && !_isPushPreviewRunning;
    public bool CanRestore => IsGitHubSignedIn && !_isRestoreRunning && !_isBackupRunning && !_isSyncAllRunning && !_isPushRunning && !_isPushPreviewRunning;
    public GameplaySessionTracker GameplayTracker => _gameplayTracker;
    public IReadOnlyList<DiscoveredAchievementFile> WatchableSourceFiles => _sourceFiles;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<object?, GameCardViewModel>? GameRequested;
    public event EventHandler? ProcessTrackingConfigurationChanged;
    public event Action<object?, AchievementUnlockNotification>? AchievementUnlocked;
    public event Action<object?, IReadOnlyList<DiscoveredAchievementFile>>? WatchSourcesChanged;
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
    public string CurrentPage { get => _currentPage; private set { if (Set(ref _currentPage, value)) { OnPropertyChanged(nameof(IsGamesVisible)); OnPropertyChanged(nameof(IsSettingsVisible)); OnPropertyChanged(nameof(IsActivityVisible)); OnPropertyChanged(nameof(IsPlatformsVisible)); } } }
    public bool IsScanning { get => _isScanning; private set { if (Set(ref _isScanning, value)) CommandManager.InvalidateRequerySuggested(); } }
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
    public Visibility AutoSyncStatusVisibility => AutoSyncAfterScan || AutoSyncAfterUnlock ? Visibility.Visible : Visibility.Collapsed;

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
    public bool IsPlatformScanRunning { get => _isPlatformScanRunning; private set { if (Set(ref _isPlatformScanRunning, value)) CommandManager.InvalidateRequerySuggested(); } }
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
        set { var cleaned = value?.Trim() ?? string.Empty; if (_settings.GitHubOwner == cleaned) return; _settings.GitHubOwner = cleaned; ResetFirstUploadAuthorization(); SaveSettings(); InvalidateGitHubPreview(); OnPropertyChanged(nameof(CanBuildGitHubPreview)); }
    }
    public string GitHubRepository
    {
        get => _settings.GitHubRepository;
        set { var cleaned = value?.Trim() ?? string.Empty; if (_settings.GitHubRepository == cleaned) return; _settings.GitHubRepository = cleaned; ResetFirstUploadAuthorization(); SaveSettings(); InvalidateGitHubPreview(); OnPropertyChanged(nameof(CanBuildGitHubPreview)); }
    }
    public string GitHubBranch
    {
        get => _settings.GitHubBranch;
        set { var cleaned = value?.Trim() ?? string.Empty; if (_settings.GitHubBranch == cleaned) return; _settings.GitHubBranch = cleaned; ResetFirstUploadAuthorization(); SaveSettings(); InvalidateGitHubPreview(); OnPropertyChanged(nameof(CanBuildGitHubPreview)); }
    }
    public bool AutoSyncAfterScan
    {
        get => _settings.AutoSyncAfterScan;
        set
        {
            if (_settings.AutoSyncAfterScan == value) return;
            _settings.AutoSyncAfterScan = value;
            SaveSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(AutoSyncStatusVisibility));
            RefreshAutoSyncStatus();
        }
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
    }

    public IReadOnlyList<GameExecutableAssociation> GetGameExecutables(GameCardViewModel game) =>
        Games.Contains(game) ? _gameplayTracker.GetAssociations(game.AppId) : [];

    public bool AddGameExecutable(GameCardViewModel game, string executablePath, out string? error)
    {
        error = null;
        if (!Games.Contains(game))
        {
            error = "This game is no longer in the current library. Scan again and retry.";
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
                await dispatcher.InvokeAsync(() =>
                {
                    var game = Games.FirstOrDefault(item => string.Equals(item.AppId, appId, StringComparison.Ordinal));
                    var title = game?.Title ?? localProfile.Title ?? $"AppID {appId}";
                    foreach (var notification in _unlockTracker.Observe(localProfile, title))
                    {
                        if (_settings.AchievementNotificationsEnabled)
                            AchievementUnlocked?.Invoke(this, notification);
                        RequestAutoSyncAfterUnlock(notification.AppId);
                    }

                    if (game is null) return;
                    game.ApplyLocalProfileUpdate(localProfile);
                    UpdateLibrarySummary();
                    RefreshGamesView();
                }, System.Windows.Threading.DispatcherPriority.DataBind);
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

    private async Task ScanAsync()
    {
        if (IsScanning) return;
        IsScanning = true;
        ScanStatus = "Scanning local profiles and configured platforms…";
        IssueText = string.Empty;
        ExportFiles.Clear();
        SelectedExport = null;
        PreviewStatus = IsGitHubSignedIn
            ? "Refreshing the pending GitHub changes…"
            : "Connect GitHub, then build a preview. Nothing has been uploaded.";
        OnPropertyChanged(nameof(HasPreviewFiles));
        AppLog.Info("Local scan started.");

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

            ScanStatus = "Scanning configured platforms…";
            await ScanPlatformsAsync();
            var catalogUpdate = await Task.Run(() => _localCatalogCache.UpdateAsync(result.SourceFiles));
            if (catalogUpdate.GamesUpdated > 0 || catalogUpdate.Failed > 0)
                AppLog.Info($"Local achievement catalog cache update finished. Games={catalogUpdate.GamesUpdated}; images={catalogUpdate.ImagesStored}; failed={catalogUpdate.Failed}.");

            var libraryGames = await Task.Run(() => result.Games.Select(game =>
            {
                var catalog = _localCatalogCache.LoadCatalog(game.AppId);
                return catalog is null ? game : ProfileMerger.EnrichWithCatalog(game, catalog);
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
            // Load public metadata/viewer data per game, but only refresh the UI once
            // after each batch settles instead of per-game.
            _ = Task.Run(async () =>
            {
                foreach (var card in gamesToLoadMetadata) await LoadMetadataAsync(card);
                foreach (var card in gamesToLoadViewerData) await LoadViewerDataAsync(card);
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
            _gameplayTracker.SetLibraryGames(libraryGames.Select(game => game.AppId), DateTimeOffset.UtcNow);
            RefreshPlaytimeSnapshots(DateTimeOffset.UtcNow);
            ProcessTrackingConfigurationChanged?.Invoke(this, EventArgs.Empty);
            IsLibraryEmpty = libraryGames.Length == 0;

            foreach (var location in result.Locations)
            {
                Locations.FirstOrDefault(item => item.Id == location.Id)?.Update(location);
            }

            UpdateLibrarySummary();

            LastScanText = $"Scanned {result.CompletedAt.ToLocalTime():MMM d, h:mm tt}";
            ScanStatus = libraryGames.Length == 0
                ? "No supported achievement profiles found yet"
                : $"Found {libraryGames.Length} game profile{(libraryGames.Length == 1 ? string.Empty : "s")} · local + platform scan";
            IssueText = result.Issues.Count == 0
                ? ""
                : $"{result.Issues.Count} file or folder item(s) were skipped. Open the log for details.";
            RefreshGamesView();
            AppLog.Info($"Local scan finished. Games={libraryGames.Length}; files={result.CandidateFiles}; issues={result.Issues.Count}.");
            foreach (var issue in result.Issues.Take(200)) AppLog.Warning(issue);
            if (_settings.AutoSyncAfterScan && newUnlockNotifications.Count > 0)
                await RunAutomaticSyncAsync();

            // Keep the FILES IN PREVIEW list live: rebuild the GitHub preview in the background so it always reflects the library.
            if (CanBuildGitHubPreview)
                _ = BuildGitHubPreviewAsync();
        }
        catch (Exception exception)
        {
            ScanStatus = "The scan could not finish. Check the local log for details.";
            AppLog.Error("Local scan failed.", exception);
        }
        finally
        {
            IsScanning = false;
            GC.Collect(2, GCCollectionMode.Optimized, false);
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
                _settings.AutoSyncAfterScan = false;
                _settings.AutoSyncAfterUnlock = false;
                _settings.HasAuthorizedFirstUpload = false;
                OnPropertyChanged(nameof(AutoSyncAfterScan));
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
                _settings.AutoSyncAfterScan = false;
                _settings.AutoSyncAfterUnlock = false;
                _settings.HasAuthorizedFirstUpload = false;
                OnPropertyChanged(nameof(AutoSyncAfterScan));
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
            _settings.AutoSyncAfterScan = false;
            _settings.HasAuthorizedFirstUpload = false;
            OnPropertyChanged(nameof(AutoSyncAfterScan));
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
            _settings.HasAuthorizedFirstUpload = true;
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

    private async Task RunAutomaticSyncAsync()
    {
        if (!_settings.AutoSyncAfterScan) return;
        if (!IsGitHubSignedIn || string.IsNullOrWhiteSpace(GitHubOwner)
            || string.IsNullOrWhiteSpace(GitHubRepository) || string.IsNullOrWhiteSpace(GitHubBranch))
        {
            ScanStatus = "Scan finished · automatic GitHub sync needs account and repository settings.";
            return;
        }

        GitHubStatus = "Automatic sync is building a fresh remote preview…";
        await BuildGitHubPreviewAsync();
        if (_githubPreview is null)
        {
            ScanStatus = "Scan finished · automatic GitHub sync needs attention.";
            return;
        }
        if (_githubPreview.Changes.Count == 0)
        {
            GitHubStatus = $"{GitHubOwner}/{GitHubRepository} ({GitHubBranch}) is up to date.";
            ScanStatus = "Scan finished · GitHub repository is up to date.";
            return;
        }

        await PushGitHubPreviewAsync(requireConfirmation: false);
        ScanStatus = _githubPreview is null
            ? "Scan finished · automatic GitHub sync completed."
            : "Scan finished · automatic GitHub sync needs attention.";
    }

    private void ResetFirstUploadAuthorization() => _settings.HasAuthorizedFirstUpload = false;

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
        _settings.AutoSyncAfterScan = false;
        _settings.HasAuthorizedFirstUpload = false;
        OnPropertyChanged(nameof(AutoSyncAfterScan));
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
                    ? "No cached achievement metadata is ready to sync. Scan the library first."
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
                _settings.HasAuthorizedFirstUpload = true;
                SaveSettings();
                RefreshAutoSyncStatus();
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
    /// Writes the merged local states back to the file at metadata/&lt;AppID&gt;/achievements.json so the file
    /// uploaded to the viewer is exactly the file on disk. Only games in the library with known states are written.
    /// Unknown states are excluded (they carry no information for the viewer).
    /// </summary>
    private static void WriteMetadataAchievementFile(string appId, GameProfile profile)
    {
        try
        {
            var states = new SortedDictionary<string, AchievementSync.Core.Synchronization.ViewerAchievementState>(StringComparer.OrdinalIgnoreCase);
            foreach (var achievement in profile.Achievements.OrderBy(item => item.ApiName, StringComparer.Ordinal))
            {
                if (!achievement.IsUnlocked.HasValue || string.IsNullOrWhiteSpace(achievement.ApiName)) continue;
                states[achievement.ApiName.Trim()] = new AchievementSync.Core.Synchronization.ViewerAchievementState(
                    achievement.IsUnlocked.Value,
                    achievement.IsUnlocked.Value ? achievement.UnlockTimeUnix ?? 0 : 0);
            }

            if (states.Count == 0) return;

            var directory = Path.Combine(MetadataRoot, appId);
            Directory.CreateDirectory(directory);
            var destination = Path.Combine(directory, "achievements.json");
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = File.Create(temporary, 16 * 1024, FileOptions.WriteThrough))
                {
                    JsonSerializer.Serialize(stream, states);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temporary, destination, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
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

    /// <summary>Backs up discovered save files and the restoration manifest for every game. Achievement data is synced through the achievements pipeline and is not copied into the save backup.</summary>
    private async Task BackupSavesAsync()
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

            var appMeta = Games.Select(game => (game.AppId, game.Title)).ToArray();
            BackupStatus = "Scanning known save locations…";
            var appIds = appMeta.Select(item => item.AppId).ToHashSet(StringComparer.Ordinal);
            var titles = appMeta.ToDictionary(item => item.AppId, item => item.Title ?? string.Empty, StringComparer.Ordinal);
            var saves = await SaveFileDiscoverer.DiscoverAsync(titles);
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

            var confirmation = MessageBox.Show(
                $"Back up {plans.Count:N0} game(s) with {saves.Count:N0} save file(s) to {GitHubOwner}/{GitHubRepository} on branch {GitHubBranch}?\n\n" +
                "Save files and a restoration manifest will be committed. Achievement data (achievements.json) is backed up separately through the achievements sync. Your original files are not modified.",
                "Confirm save backup",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirmation != MessageBoxResult.Yes) { BackupStatus = "Backup cancelled."; return; }

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
            BackupStatus = result.Pushed ? $"Backup complete: {result.Summary}" : $"Nothing to back up: {result.Summary}";
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
            GC.Collect(2, GCCollectionMode.Optimized, false);
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
        RefreshGamesView();
    }

    private async Task LoadMetadataAsync(GameCardViewModel game)
    {
        await _metadataSlots.WaitAsync();
        try
        {
            var metadata = await _metadataCache.GetAsync(game.AppId);
            if (metadata is null || Application.Current is null) return;

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
                    var cachedCatalog = await _localCatalogCache.CacheCatalogAsync(game.AppId, catalogResult.Catalog);
                    var catalog = cachedCatalog ?? StripRemoteCatalogIcons(catalogResult.Catalog);
                    await Application.Current!.Dispatcher.InvokeAsync(() => game.ApplyCatalog(catalog));
                    AppLog.Info($"Loaded public viewer catalog for AppID {game.AppId}; entries={catalogResult.Catalog.Achievements.Count}.");
                }
                else if (catalogResult.Status is not (ViewerCatalogStatus.NotConfigured or ViewerCatalogStatus.NotFound))
                {
                    AppLog.Warning($"Could not load viewer catalog for AppID {game.AppId}: {catalogResult.Status}.");
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

    private static bool NeedsCatalogMetadata(GameProfile profile) =>
        !profile.HasCompleteCatalog || profile.Achievements.Any(achievement =>
            string.IsNullOrWhiteSpace(achievement.DisplayName)
            || string.IsNullOrWhiteSpace(achievement.Description)
            || string.IsNullOrWhiteSpace(achievement.IconUri));

    private static ParsedProfile StripRemoteCatalogIcons(ParsedProfile catalog) => catalog with
    {
        Achievements = catalog.Achievements.Select(achievement => achievement with
        {
            IconUri = Uri.TryCreate(achievement.IconUri, UriKind.Absolute, out var uri) && uri.IsFile
                ? achievement.IconUri
                : null
        }).ToArray()
    };

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
            : $"Across {summary.MeasurableGameCount:N0} of {summary.GameCount:N0} games with known catalog and player states";
        AchievementTotalText = summary.TotalCatalogAchievements is { } catalogTotal
            ? $"{catalogTotal:N0} catalog entries · {summary.TotalKnownAchievements:N0} known states"
            : $"{summary.TotalKnownAchievements:N0} known player states · catalog totals unavailable";
        MeasurableGamesText = $"{summary.MeasurableGameCount:N0} games with measurable progress · {summary.GamesMissingCatalog:N0} missing a total · {summary.GamesWithUnknownPlayerState:N0} with unknown player states";
    }

    private void RefreshPlaytimeSnapshots(DateTimeOffset nowUtc)
    {
        var snapshots = _gameplayTracker.GetSnapshots(nowUtc).ToDictionary(snapshot => snapshot.AppId, StringComparer.Ordinal);
        foreach (var game in Games)
        {
            if (snapshots.TryGetValue(game.AppId, out var snapshot)) game.ApplyPlaytime(snapshot);
        }
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
    }

    public string GameResultsEmptyText => IsLibraryEmpty
        ? "No supported profiles have been discovered yet. Scan now or add a folder in Settings."
        : "No games match this search and filter. Clear or change them to see the library.";

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
    }

    private void RemoveLocation(LocationOptionViewModel? location)
    {
        if (location is null || !location.IsCustom) return;
        Locations.Remove(location);
        var id = location.Id.StartsWith("custom-", StringComparison.Ordinal) ? location.Id[7..] : location.Id;
        _settings.CustomLocations.RemoveAll(item => item.Id == id);
        SaveSettings();
    }

    private void SaveSettings()
    {
        try
        {
            _settingsStore.Save(_settings);
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

    private async Task ScanPlatformsAsync()
    {
        if (IsPlatformScanRunning) return;
        IsPlatformScanRunning = true;
        PlatformScanStatus = "Scanning configured platform sources...";
        try
        {
            var options = BuildPlatformSourceOptions();
            var report = await _platformScanService.ScanAsync(options);
            PlatformStatuses = report.Platforms;
            PlatformScanStatus = report.GamesConverted > 0
                ? $"Converted {report.GamesConverted} game(s), wrote {report.FilesWritten} file(s)."
                : "No platform achievement data was found.";
            if (report.Messages.Count > 0)
                PlatformScanStatus += " Details: " + string.Join("; ", report.Messages.Take(5));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or HttpRequestException or TaskCanceledException)
        {
            PlatformScanStatus = $"Platform scan failed: {exception.GetType().Name}.";
            if (_enableDiagnostics) AppLog.Warning($"Platform scan failed: {exception.Message}");
        }
        finally
        {
            IsPlatformScanRunning = false;
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
            }
        }
        options.RetroAchievementsUser = _settings.RetroAchievementsUser;
        options.RetroAchievementsApiKey = ReadPlatformCredential(_retroAchievementsKeyStore);
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
    }

    public void SavePlatformCredentials(string retroAchievementsApiKey)
    {
        try
        {
            StorePlatformCredential(_retroAchievementsKeyStore, retroAchievementsApiKey);
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
            _platformSourceOptions = BuildPlatformSourceOptions();
            UpdatePlatformCredentialStatus();
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or PlatformNotSupportedException or InvalidOperationException)
        {
            PlatformCredentialStatus = "Credentials could not be cleared. Check Windows Credential Manager access and try again.";
            AppLog.Warning($"Platform API credentials could not be cleared: {exception.GetType().Name}.");
        }
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
        PlatformCredentialStatus = $"RetroAchievements API key: {(ra ? "saved" : "not set")}. Stored in Windows Credential Manager.";
    }

    private void OpenEnablerWizard()
    {
        var window = new AchievementEnablerWindow(_enablerService)
        {
            Owner = Application.Current?.MainWindow
        };
        window.ShowDialog();
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
                "The requested operation could not be completed. Your local achievement files were not changed. Check the local log for details.",
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
