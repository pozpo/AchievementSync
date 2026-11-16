using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows;
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

    public MainViewModel()
    {
        _settings = _settingsStore.Load();
        Locations = new ObservableCollection<LocationOptionViewModel>(
            LocationCatalog.Build(_settings).Select(location => new LocationOptionViewModel(location, OnLocationChanged)));
        Games = new ObservableCollection<GameCardViewModel>();
        GamesView = CollectionViewSource.GetDefaultView(Games);
        GamesView.Filter = FilterGame;
        ScanCommand = new AsyncCommand(ScanAsync, () => !IsScanning);
        NavigationCommand = new RelayCommand(parameter => CurrentPage = parameter?.ToString() ?? "Dashboard");
        RemoveLocationCommand = new RelayCommand(parameter => RemoveLocation(parameter as LocationOptionViewModel));
        BuildExportPreviewCommand = new RelayCommand(_ => BuildExportPreview());
        OpenGameCommand = new RelayCommand(parameter =>
        {
            if (parameter is GameCardViewModel game) GameRequested?.Invoke(this, game);
        });
    }

    public ObservableCollection<LocationOptionViewModel> Locations { get; }
    public ObservableCollection<GameCardViewModel> Games { get; }
    public ObservableCollection<ExportPreviewFileViewModel> ExportFiles { get; } = [];
    public ICollectionView GamesView { get; }
    public ICommand ScanCommand { get; }
    public ICommand NavigationCommand { get; }
    public ICommand RemoveLocationCommand { get; }
    public ICommand BuildExportPreviewCommand { get; }
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
