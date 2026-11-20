using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media.Imaging;
using AchievementSync.Core.Models;
using AchievementSync.Core.Metadata;
using AchievementSync.Core.Scanning;
using AchievementSync.Core.Tracking;
using AchievementSync.Desktop.Services;

namespace AchievementSync.Desktop.ViewModels;

public sealed class GameCardViewModel(GameProfile profile) : INotifyPropertyChanged
{
    private GameProfile _profile = profile;
    private string? _metadataTitle;
    private string? _manualTitle;
    private BitmapImage? _artwork;
    private bool _includesViewerProgress;
    private GamePlaytimeSnapshot _playtime = new(profile.AppId, TimeSpan.Zero, null, null, [], 0);

    public GameProfile Profile => _profile;
    public string AppId => Profile.AppId;
    public string Title => _manualTitle ?? _metadataTitle ?? Profile.Title ?? $"AppID {Profile.AppId}";
    public string CoverMark => Profile.AppId.Length >= 3 ? Profile.AppId[^3..] : Profile.AppId;
    public BitmapImage? Artwork => _artwork;
    public Visibility FallbackVisibility => _artwork is null ? Visibility.Visible : Visibility.Collapsed;
    public int UnlockedCount => Profile.UnlockedCount;
    public int? TotalCount => Profile.TotalCount;
    public double Progress => Profile.CompletionPercent ?? 0;
    public bool HasMeasurableProgress => Profile.CompletionPercent.HasValue;
    public string ProgressText => Profile.CompletionPercent is { } percentage
        ? $"{percentage:0.#}%"
        : Profile.TotalCount.HasValue
            ? Profile.KnownStateCount == 0
                ? "Player progress unavailable"
                : "Some player progress unavailable"
            : Profile.UnlockedCount > 0
                ? $"{Profile.UnlockedCount} unlocked"
                : Profile.UnlistedAchievementsAreLocked
                    ? "No unlocks recorded · total unknown"
                    : "Player progress unavailable · total unknown";
    public string AchievementCountText => Profile.TotalCount is { } total
        ? Profile.KnownStateCount == total
            ? $"{Profile.UnlockedCount} / {total} achievements unlocked"
            : $"{Profile.UnlockedCount} unlocked · {Profile.KnownStateCount} of {total} states known"
        : Profile.KnownStateCount > 0
            ? $"{Profile.UnlockedCount} unlocked · total unknown"
            : Profile.UnlistedAchievementsAreLocked
                ? "No confirmed unlocks · total unknown"
                : "Player progress unavailable · total unknown";
    public string RecentAchievementText => Profile.LatestUnlocked is { } latest
        ? $"Last unlocked: {latest.DisplayName ?? latest.ApiName}"
        : "No unlock time recorded";
    public string SourceCountText => Profile.SourceFiles.Count > 1
        ? _includesViewerProgress
            ? "Local profiles merged with public viewer progress"
            : $"Merged from {Profile.SourceFiles.Count} profiles"
        : _includesViewerProgress
            ? "Public viewer progress"
            : "Local achievement profile";
    public string PlaytimeText => _playtime.IsRunning
        ? $"Playing · {PlaytimeFormatter.Format(_playtime.TotalPlaytime)}"
        : $"Played {PlaytimeFormatter.Format(_playtime.TotalPlaytime)}";
    public Visibility PlaytimeVisibility => _playtime.ExecutablePaths.Count > 0 || _playtime.TotalPlaytime > TimeSpan.Zero || _playtime.IsRunning
        ? Visibility.Visible
        : Visibility.Collapsed;
    public string LastPlayedText => _playtime.LastPlayedUtc is { } lastPlayed
        ? $"Last played {lastPlayed.ToLocalTime():MMM d, h:mm tt}"
        : "Not played yet";
    public string CurrentSessionText => _playtime.IsRunning
        ? $"Current session {PlaytimeFormatter.Format(_playtime.CurrentSessionDuration(DateTimeOffset.UtcNow))}"
        : "Not currently running";
    public string ExecutableAssociationText => _playtime.ExecutablePaths.Count switch
    {
        0 => "No game executable linked",
        1 => Path.GetFileName(_playtime.ExecutablePaths[0]),
        _ => $"{_playtime.ExecutablePaths.Count} game executables linked"
    };
    public IReadOnlyList<string> ExecutablePaths => _playtime.ExecutablePaths;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void ApplyCatalog(ParsedProfile catalog)
    {
        _profile = ProfileMerger.EnrichWithCatalog(Profile, catalog);
        if (string.IsNullOrWhiteSpace(_metadataTitle) && !string.IsNullOrWhiteSpace(catalog.Title))
        {
            _metadataTitle = catalog.Title;
            OnPropertyChanged(nameof(Title));
        }
        OnPropertyChanged(nameof(Profile));
        OnPropertyChanged(nameof(UnlockedCount));
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(HasMeasurableProgress));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(AchievementCountText));
        OnPropertyChanged(nameof(RecentAchievementText));
        OnPropertyChanged(nameof(SourceCountText));
    }

    public void ApplyViewerProgress(ParsedProfile progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (!string.Equals(progress.AppId, AppId, StringComparison.Ordinal)) return;

        var local = new ParsedProfile(
            Profile.AppId,
            "Merged local profiles",
            Profile.SourceFiles.FirstOrDefault() ?? $"local://AppID/{Profile.AppId}",
            DateTimeOffset.UtcNow,
            Profile.Achievements,
            Profile.HasCompleteCatalog,
            Profile.Title,
            Profile.UnlistedAchievementsAreLocked);
        var viewerProgress = progress with { HasCompleteCatalog = false, UnlistedAchievementsAreLocked = true };
        var merged = ProfileMerger.Merge([local, viewerProgress]).Single();
        _profile = merged with
        {
            SourceFiles = Profile.SourceFiles
                .Append(progress.FilePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
        _includesViewerProgress = true;
        OnPropertyChanged(nameof(Profile));
        OnPropertyChanged(nameof(UnlockedCount));
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(HasMeasurableProgress));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(AchievementCountText));
        OnPropertyChanged(nameof(RecentAchievementText));
        OnPropertyChanged(nameof(SourceCountText));
    }

    public void ApplyLocalProfileUpdate(GameProfile localProfile)
    {
        if (!string.Equals(localProfile.AppId, AppId, StringComparison.Ordinal)) return;
        var existing = new ParsedProfile(
            Profile.AppId,
            "Current library profile",
            Profile.SourceFiles.FirstOrDefault() ?? $"local://AppID/{Profile.AppId}",
            DateTimeOffset.UtcNow,
            Profile.Achievements,
            Profile.HasCompleteCatalog,
            Profile.Title,
            Profile.UnlistedAchievementsAreLocked);
        var local = new ParsedProfile(
            localProfile.AppId,
            "Updated local profiles",
            string.Join(";", localProfile.SourceFiles),
            DateTimeOffset.UtcNow,
            localProfile.Achievements,
            localProfile.HasCompleteCatalog,
            localProfile.Title,
            localProfile.UnlistedAchievementsAreLocked);
        _profile = ProfileMerger.Merge([existing, local]).Single() with
        {
            SourceFiles = Profile.SourceFiles
                .Union(localProfile.SourceFiles, StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
        OnPropertyChanged(nameof(Profile));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(UnlockedCount));
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(HasMeasurableProgress));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(AchievementCountText));
        OnPropertyChanged(nameof(RecentAchievementText));
        OnPropertyChanged(nameof(SourceCountText));
    }

    public void ApplyIdentifiedMetadata(SteamGameMetadata metadata)
    {
        if (!string.Equals(metadata.AppId, AppId, StringComparison.Ordinal)) return;
        if (string.IsNullOrWhiteSpace(_metadataTitle) && !string.IsNullOrWhiteSpace(metadata.Name))
        {
            _metadataTitle = metadata.Name;
            OnPropertyChanged(nameof(Title));
        }
    }

    public void ApplyManualTitle(string? title)
    {
        _manualTitle = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        OnPropertyChanged(nameof(Title));
    }

    public void ApplyPlaytime(GamePlaytimeSnapshot playtime)
    {
        if (!string.Equals(playtime.AppId, AppId, StringComparison.Ordinal)) return;
        _playtime = playtime;
        OnPropertyChanged(nameof(PlaytimeText));
        OnPropertyChanged(nameof(PlaytimeVisibility));
        OnPropertyChanged(nameof(LastPlayedText));
        OnPropertyChanged(nameof(CurrentSessionText));
        OnPropertyChanged(nameof(ExecutableAssociationText));
        OnPropertyChanged(nameof(ExecutablePaths));
    }

    public void ApplyMetadata(CachedSteamMetadata metadata)
    {
        if (string.IsNullOrWhiteSpace(_metadataTitle) && !string.IsNullOrWhiteSpace(metadata.Name))
        {
            _metadataTitle = metadata.Name;
            OnPropertyChanged(nameof(Title));
        }

        // The public Steam Store listing states how many achievements the app defines, hidden ones
        // included. When the local profile already carries an explicit state for every listed
        // achievement, the listing completes the catalog without inventing states or guessing names.
        if (!Profile.HasCompleteCatalog
            && metadata.AchievementsTotal is { } steamTotal
            && steamTotal > 0
            && Profile.Achievements.Count == steamTotal)
        {
            _profile = Profile with { HasCompleteCatalog = true };
            OnPropertyChanged(nameof(Profile));
            OnPropertyChanged(nameof(UnlockedCount));
            OnPropertyChanged(nameof(TotalCount));
            OnPropertyChanged(nameof(Progress));
            OnPropertyChanged(nameof(HasMeasurableProgress));
            OnPropertyChanged(nameof(ProgressText));
            OnPropertyChanged(nameof(AchievementCountText));
        }

        if (!string.IsNullOrWhiteSpace(metadata.ArtworkPath) && File.Exists(metadata.ArtworkPath))
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 460;
                image.UriSource = new Uri(metadata.ArtworkPath, UriKind.Absolute);
                image.EndInit();
                image.Freeze();
                _artwork = image;
                OnPropertyChanged(nameof(Artwork));
                OnPropertyChanged(nameof(FallbackVisibility));
            }
            catch (Exception exception) when (exception is IOException or UriFormatException or NotSupportedException)
            {
                // The title can still be used when a cached image is incomplete or unsupported.
            }
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
