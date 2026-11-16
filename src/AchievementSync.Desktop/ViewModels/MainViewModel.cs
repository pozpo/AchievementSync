using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows;
using System.Diagnostics;
using System.Net.Http;
using AchievementSync.Core.Scanning;
using AchievementSync.Core.Settings;
using AchievementSync.Core.Synchronization;
using AchievementSync.Desktop.Services;

namespace AchievementSync.Desktop.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly AchievementScanner _scanner = new();
    private readonly SteamMetadataCache _metadataCache = new();
    private readonly SemaphoreSlim _metadataSlots = new(5, 5);
    private readonly SettingsStore _settingsStore = new();
    private readonly HttpClient _githubHttpClient = new() { Timeout = TimeSpan.FromSeconds(45) };
    private readonly GitHubDeviceFlowClient _githubDeviceFlow;
    private readonly GitHubAchievementSyncClient _githubSyncClient;
    private readonly WindowsCredentialTokenStore _githubTokenStore = new();
    private AppSettings _settings;
    private string _searchText = string.Empty;
    private string _scanStatus = "Scanning your local folders when the app opens";
    private string _lastScanText = "Not scanned yet";
    private string _currentPage = "Dashboard";
    private bool _isScanning;
    private bool _isLibraryEmpty = true;
    private int _totalGames;
    private int _totalKnownAchievements;
    private int _totalUnlocked;
    private string _completionText = "—";
    private string _achievementTotalText = "—";
    private string _recentUnlocksText = "No recent unlocks yet";
    private string _issueText = string.Empty;
    private string _previewStatus = "Build a local preview of the viewer-compatible files.";
    private ExportPreviewFileViewModel? _selectedExport;
    private string _githubStatus = string.Empty;
    private bool _isGitHubSignedIn;
    private bool _isGitHubBusy;
    private GitHubSyncPreview? _githubPreview;

    public MainViewModel()
    {
        _settings = _settingsStore.Load();
        _githubDeviceFlow = new GitHubDeviceFlowClient(_githubHttpClient);
        _githubSyncClient = new GitHubAchievementSyncClient(_githubHttpClient);
        Locations = new ObservableCollection<LocationOptionViewModel>(
            LocationCatalog.Build(_settings).Select(location => new LocationOptionViewModel(location, OnLocationChanged)));
        Games = new ObservableCollection<GameCardViewModel>();
        GamesView = CollectionViewSource.GetDefaultView(Games);
        GamesView.Filter = FilterGame;
        ScanCommand = new AsyncCommand(ScanAsync, () => !IsScanning);
        NavigationCommand = new RelayCommand(parameter => CurrentPage = parameter?.ToString() ?? "Dashboard");
        RemoveLocationCommand = new RelayCommand(parameter => RemoveLocation(parameter as LocationOptionViewModel));
        BuildExportPreviewCommand = new RelayCommand(_ => BuildExportPreview());
        SignInToGitHubCommand = new AsyncCommand(SignInToGitHubAsync, () => CanStartGitHubSignIn);
        DisconnectGitHubCommand = new RelayCommand(_ => DisconnectGitHub());
        BuildGitHubPreviewCommand = new AsyncCommand(BuildGitHubPreviewAsync, () => CanBuildGitHubPreview);
        PushGitHubPreviewCommand = new AsyncCommand(() => PushGitHubPreviewAsync(), () => CanPushGitHubPreview);
        OpenGameCommand = new RelayCommand(parameter =>
        {
            if (parameter is GameCardViewModel game) GameRequested?.Invoke(this, game);
        });

        try
        {
            _isGitHubSignedIn = !string.IsNullOrWhiteSpace(_githubTokenStore.Read());
        }
        catch (Exception exception)
        {
            AppLog.Warning($"Could not read GitHub sign-in status from Windows Credential Manager: {exception.GetType().Name}.");
        }
        _githubStatus = _isGitHubSignedIn
            ? $"Connected as {_settings.GitHubLogin}. Preview changes before every push."
            : !HasGitHubOAuthClientId
                ? "GitHub sign-in is waiting for the publisher's OAuth App Client ID."
                : "Connect your GitHub account and select your public achievement-viewer repository.";
    }

    public ObservableCollection<LocationOptionViewModel> Locations { get; }
    public ObservableCollection<GameCardViewModel> Games { get; }
    public ObservableCollection<ExportPreviewFileViewModel> ExportFiles { get; } = [];
    public ICollectionView GamesView { get; }
    public ICommand ScanCommand { get; }
    public ICommand NavigationCommand { get; }
    public ICommand RemoveLocationCommand { get; }
    public ICommand BuildExportPreviewCommand { get; }
    public ICommand SignInToGitHubCommand { get; }
    public ICommand DisconnectGitHubCommand { get; }
    public ICommand BuildGitHubPreviewCommand { get; }
    public ICommand PushGitHubPreviewCommand { get; }
    public ICommand OpenGameCommand { get; }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<object?, GameCardViewModel>? GameRequested;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText == value) return;
            _searchText = value;
            OnPropertyChanged();
            GamesView.Refresh();
        }
    }

    public string ScanStatus { get => _scanStatus; private set => Set(ref _scanStatus, value); }
    public string LastScanText { get => _lastScanText; private set => Set(ref _lastScanText, value); }
    public string CurrentPage { get => _currentPage; private set { if (Set(ref _currentPage, value)) OnPropertyChanged(nameof(IsDashboardVisible)); OnPropertyChanged(nameof(IsSettingsVisible)); OnPropertyChanged(nameof(IsActivityVisible)); } }
    public bool IsScanning { get => _isScanning; private set { if (Set(ref _isScanning, value)) CommandManager.InvalidateRequerySuggested(); } }
    public bool IsDashboardVisible => CurrentPage == "Dashboard" || CurrentPage == "Games";
    public bool IsSettingsVisible => CurrentPage == "Settings";
    public bool IsActivityVisible => CurrentPage == "Activity";
    public bool HasGitHubOAuthClientId => !string.IsNullOrWhiteSpace(GitHubOAuthConfiguration.ClientId);
    public bool IsGitHubSignedIn { get => _isGitHubSignedIn; private set { if (Set(ref _isGitHubSignedIn, value)) { OnPropertyChanged(nameof(CanBuildGitHubPreview)); OnPropertyChanged(nameof(GitHubAccountText)); CommandManager.InvalidateRequerySuggested(); } } }
    public bool IsGitHubBusy { get => _isGitHubBusy; private set { if (Set(ref _isGitHubBusy, value)) { OnPropertyChanged(nameof(CanStartGitHubSignIn)); OnPropertyChanged(nameof(CanBuildGitHubPreview)); OnPropertyChanged(nameof(CanPushGitHubPreview)); CommandManager.InvalidateRequerySuggested(); } } }
    public bool CanStartGitHubSignIn => HasGitHubOAuthClientId && !IsGitHubBusy;
    public bool CanBuildGitHubPreview => IsGitHubSignedIn && !IsGitHubBusy && !string.IsNullOrWhiteSpace(GitHubOwner) && !string.IsNullOrWhiteSpace(GitHubRepository) && !string.IsNullOrWhiteSpace(GitHubBranch);
    public bool CanPushGitHubPreview => _githubPreview is { Changes.Count: > 0 } && IsGitHubSignedIn && !IsGitHubBusy;
    public string GitHubStatus { get => _githubStatus; private set => Set(ref _githubStatus, value); }
    public string GitHubAccountText => IsGitHubSignedIn ? $"Connected as {_settings.GitHubLogin}" : "GitHub account not connected";
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
            if (value)
            {
                if (!IsGitHubSignedIn || string.IsNullOrWhiteSpace(GitHubOwner)
                    || !string.Equals(GitHubOwner, _settings.GitHubLogin, StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(GitHubRepository) || string.IsNullOrWhiteSpace(GitHubBranch))
                {
                    MessageBox.Show("Connect GitHub and configure a repository owned by that account before enabling automatic sync.",
                        "Automatic sync unavailable", MessageBoxButton.OK, MessageBoxImage.Information);
                    OnPropertyChanged();
                    return;
                }

                var choice = MessageBox.Show(
                    $"Enable automatic sync after every successful scan to {GitHubOwner}/{GitHubRepository} on {GitHubBranch}? The first upload will still show the exact preview and ask for confirmation. After that, new or changed achievement JSON may be pushed automatically. Only AppID achievement identifiers, earned states, and available timestamps are sent; original save files and local paths are not.",
                    "Enable automatic GitHub sync",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (choice != MessageBoxResult.Yes)
                {
                    OnPropertyChanged();
                    return;
                }
                _settings.HasAuthorizedFirstUpload = false;
            }
            _settings.AutoSyncAfterScan = value;
            SaveSettings();
            OnPropertyChanged();
        }
    }
    public bool IsLibraryEmpty { get => _isLibraryEmpty; private set => Set(ref _isLibraryEmpty, value); }
    public int TotalGames { get => _totalGames; private set => Set(ref _totalGames, value); }
    public int TotalKnownAchievements { get => _totalKnownAchievements; private set => Set(ref _totalKnownAchievements, value); }
    public int TotalUnlocked { get => _totalUnlocked; private set => Set(ref _totalUnlocked, value); }
    public string CompletionText { get => _completionText; private set => Set(ref _completionText, value); }
    public string AchievementTotalText { get => _achievementTotalText; private set => Set(ref _achievementTotalText, value); }
    public string RecentUnlocksText { get => _recentUnlocksText; private set => Set(ref _recentUnlocksText, value); }
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

    private async Task ScanAsync()
    {
        if (IsScanning) return;
        IsScanning = true;
        ScanStatus = "Scanning enabled locations…";
        IssueText = string.Empty;
        ExportFiles.Clear();
        SelectedExport = null;
        PreviewStatus = "The library changed. Build a new preview before reviewing files.";
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

            Games.Clear();
            foreach (var game in result.Games)
            {
                var card = new GameCardViewModel(game);
                if (_settings.ManualGameNames.TryGetValue(game.AppId, out var manualName))
                {
                    card.ApplyManualTitle(manualName);
                }
                Games.Add(card);
                _ = LoadMetadataAsync(card);
            }
            IsLibraryEmpty = result.Games.Count == 0;

            foreach (var location in result.Locations)
            {
                Locations.FirstOrDefault(item => item.Id == location.Id)?.Update(location);
            }

            TotalGames = result.Games.Count;
            TotalUnlocked = result.Games.Sum(game => game.UnlockedCount);
            TotalKnownAchievements = result.Games.Sum(game => game.TotalCount
                ?? game.Achievements.Count(item => item.IsUnlocked.HasValue));
            var completeGames = result.Games.Where(game => game.TotalCount.HasValue).ToArray();
            var measurableGames = result.Games.Where(game => game.CompletionPercent.HasValue).ToArray();
            var completeTotal = measurableGames.Sum(game => game.TotalCount ?? 0);
            var completeUnlocked = measurableGames.Sum(game => game.UnlockedCount);
            CompletionText = completeTotal > 0 ? $"{completeUnlocked * 100d / completeTotal:0.#}%" : "—";
            AchievementTotalText = completeGames.Length == result.Games.Count && result.Games.Count > 0
                ? completeTotal.ToString("N0")
                : $"{TotalKnownAchievements:N0} known";

            RefreshRecentUnlocks();

            LastScanText = $"Scanned {result.CompletedAt.ToLocalTime():MMM d, h:mm tt}";
            ScanStatus = result.Games.Count == 0
                ? "No supported achievement profiles found yet"
                : $"Found {result.Games.Count} game profile{(result.Games.Count == 1 ? string.Empty : "s")} · local only";
            IssueText = result.Issues.Count == 0
                ? ""
                : $"{result.Issues.Count} file or folder item(s) were skipped. Open the log for details.";
            GamesView.Refresh();
            AppLog.Info($"Local scan finished. Games={result.Games.Count}; files={result.CandidateFiles}; issues={result.Issues.Count}.");
            foreach (var issue in result.Issues.Take(200)) AppLog.Warning(issue);
            if (_settings.AutoSyncAfterScan) await RunAutomaticSyncAsync();
        }
        catch (Exception exception)
        {
            ScanStatus = "The scan could not finish. Check the local log for details.";
            AppLog.Error("Local scan failed.", exception);
        }
        finally
        {
            IsScanning = false;
        }
    }

    private void BuildExportPreview()
    {
        ExportFiles.Clear();
        var plannedFiles = AchievementExportPlanner.Build(Games.Select(game => game.Profile));
        foreach (var file in plannedFiles)
        {
            ExportFiles.Add(new ExportPreviewFileViewModel(file));
        }

        SelectedExport = ExportFiles.FirstOrDefault();
        OnPropertyChanged(nameof(HasPreviewFiles));
        var stateCount = plannedFiles.Sum(file => file.AchievementCount);
        PreviewStatus = plannedFiles.Count == 0
            ? "No files can be previewed. Scan for profiles with known unlock states."
            : $"{plannedFiles.Count:N0} AppID file(s) · {stateCount:N0} known achievement state(s). Unknown states are excluded. Nothing has been uploaded or written.";
    }

    private async Task SignInToGitHubAsync()
    {
        if (!HasGitHubOAuthClientId)
        {
            GitHubStatus = "This build has no publisher-configured GitHub OAuth Client ID. Sign-in cannot start yet.";
            return;
        }

        IsGitHubBusy = true;
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
            _githubTokenStore.Write(token.AccessToken);
            var previousLogin = _settings.GitHubLogin;
            _settings.GitHubLogin = account.Login;
            if (!string.Equals(previousLogin, account.Login, StringComparison.OrdinalIgnoreCase))
            {
                _settings.AutoSyncAfterScan = false;
                _settings.HasAuthorizedFirstUpload = false;
                OnPropertyChanged(nameof(AutoSyncAfterScan));
            }
            SaveSettings();
            IsGitHubSignedIn = true;
            GitHubStatus = $"Connected as {account.Login}. Choose your public repository, then build a preview.";
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
            IsGitHubBusy = false;
        }
    }

    private void DisconnectGitHub()
    {
        try
        {
            _githubTokenStore.Delete();
            _settings.GitHubLogin = string.Empty;
            _settings.AutoSyncAfterScan = false;
            _settings.HasAuthorizedFirstUpload = false;
            OnPropertyChanged(nameof(AutoSyncAfterScan));
            SaveSettings();
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
        var token = ReadGitHubToken();
        if (token is null) return;
        IsGitHubBusy = true;
        GitHubStatus = "Checking your repository and merging achievement progress…";
        try
        {
            if (!string.Equals(GitHubOwner, _settings.GitHubLogin, StringComparison.OrdinalIgnoreCase))
                throw new GitHubApiException("Choose a repository owned by the connected GitHub account.");

            var target = new GitHubSyncTarget(GitHubOwner, GitHubRepository, GitHubBranch);
            var planned = AchievementExportPlanner.Build(Games.Select(game => game.Profile));
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
            GitHubStatus = exception is GitHubApiException or ArgumentException or InvalidDataException
                ? exception.Message
                : "Could not build a GitHub preview. Check the local log.";
            AppLog.Warning($"GitHub preview failed: {exception.GetType().Name}.");
        }
        finally
        {
            IsGitHubBusy = false;
        }
    }

    private async Task PushGitHubPreviewAsync(bool requireConfirmation = true)
    {
        var token = ReadGitHubToken();
        var preview = _githubPreview;
        if (token is null || preview is null || preview.Changes.Count == 0) return;

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

        IsGitHubBusy = true;
        GitHubStatus = "Creating a single content-only GitHub commit…";
        try
        {
            var result = await _githubSyncClient.PushAsync(token, preview);
            _settings.HasAuthorizedFirstUpload = true;
            SaveSettings();
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
            GitHubStatus = exception is GitHubApiException
                ? exception.Message
                : "GitHub push failed. Rebuild the preview before retrying.";
            AppLog.Warning($"GitHub push failed: {exception.GetType().Name}.");
        }
        finally
        {
            IsGitHubBusy = false;
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

        var needsFirstConfirmation = !_settings.HasAuthorizedFirstUpload;
        CurrentPage = "Activity";
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

        if (needsFirstConfirmation)
            PreviewStatus = "First automatic upload requires the exact JSON preview and your confirmation. Review the listed files now.";
        await PushGitHubPreviewAsync(requireConfirmation: needsFirstConfirmation);
        ScanStatus = _settings.HasAuthorizedFirstUpload && _githubPreview is null
            ? "Scan finished · automatic GitHub sync completed."
            : "Scan finished · automatic GitHub sync needs attention.";
    }

    private void ResetFirstUploadAuthorization() => _settings.HasAuthorizedFirstUpload = false;

    private string? ReadGitHubToken()
    {
        try
        {
            var token = _githubTokenStore.Read();
            if (!string.IsNullOrWhiteSpace(token)) return token;
            IsGitHubSignedIn = false;
            GitHubStatus = "GitHub sign-in has expired or was removed. Connect again.";
            return null;
        }
        catch (Exception exception)
        {
            IsGitHubSignedIn = false;
            GitHubStatus = "Could not read the secure GitHub credential. Check Windows Credential Manager.";
            AppLog.Warning($"Could not read GitHub credential: {exception.GetType().Name}.");
            return null;
        }
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
        GamesView.Refresh();
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
                RefreshRecentUnlocks();
                GamesView.Refresh();
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

    private void RefreshRecentUnlocks()
    {
        var latest = Games
            .SelectMany(game => game.Profile.Achievements.Select(item => (Game: game, Achievement: item)))
            .Where(item => item.Achievement.IsUnlocked == true && item.Achievement.UnlockTimeUnix is > 0)
            .OrderByDescending(item => item.Achievement.UnlockTimeUnix)
            .Take(3)
            .Select(item => $"{item.Game.Title} · {item.Achievement.DisplayName ?? item.Achievement.ApiName} · {FormatUnlockDate(item.Achievement.UnlockTimeUnix!.Value)}")
            .ToArray();
        RecentUnlocksText = latest.Length == 0 ? "No recent unlocks with timestamps" : string.Join("  •  ", latest);
    }

    private static string FormatUnlockDate(long unixTime)
    {
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixTime).ToLocalTime().ToString("MMM d, h:mm tt");
        }
        catch (ArgumentOutOfRangeException)
        {
            return "date unavailable";
        }
    }

    private bool FilterGame(object item)
    {
        if (item is not GameCardViewModel game) return false;
        if (string.IsNullOrWhiteSpace(SearchText)) return true;
        return game.Title.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase)
            || game.AppId.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
    }

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
}

internal sealed class AsyncCommand(Func<Task> execute, Func<bool>? canExecute = null) : ICommand
{
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public async void Execute(object? parameter) => await execute();
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}

internal sealed class RelayCommand(Action<object?> execute) : ICommand
{
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute(parameter);
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}
