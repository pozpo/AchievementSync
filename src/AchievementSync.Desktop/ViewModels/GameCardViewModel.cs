using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media.Imaging;
using AchievementSync.Core.Models;
using AchievementSync.Desktop.Services;

namespace AchievementSync.Desktop.ViewModels;

public sealed class GameCardViewModel(GameProfile profile) : INotifyPropertyChanged
{
    private string? _metadataTitle;
    private string? _manualTitle;
    private BitmapImage? _artwork;

    public GameProfile Profile { get; } = profile;
    public string AppId => Profile.AppId;
    public string Title => _manualTitle ?? _metadataTitle ?? Profile.Title ?? $"AppID {Profile.AppId}";
    public string CoverMark => Profile.AppId.Length >= 3 ? Profile.AppId[^3..] : Profile.AppId;
    public BitmapImage? Artwork => _artwork;
    public Visibility FallbackVisibility => _artwork is null ? Visibility.Visible : Visibility.Collapsed;
    public int UnlockedCount => Profile.UnlockedCount;
    public int? TotalCount => Profile.TotalCount;
    public double Progress => Profile.CompletionPercent ?? 0;
    public string ProgressText => Profile.CompletionPercent is { } percentage
        ? $"{percentage:0.#}%"
        : "Progress unavailable";
    public string AchievementCountText => Profile.TotalCount is { } total
        ? $"{Profile.UnlockedCount} / {total} achievements unlocked"
        : $"{Profile.UnlockedCount} unlocked · total unknown";
    public string RecentAchievementText => Profile.LatestUnlocked is { } latest
        ? $"Last unlocked: {latest.DisplayName ?? latest.ApiName}"
        : "No unlock time recorded";
    public string SourceCountText => Profile.SourceFiles.Count > 1
        ? $"Merged from {Profile.SourceFiles.Count} profiles"
        : "Local achievement profile";

    public event PropertyChangedEventHandler? PropertyChanged;

    public void ApplyManualTitle(string? title)
    {
        _manualTitle = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        OnPropertyChanged(nameof(Title));
    }

    public void ApplyMetadata(CachedSteamMetadata metadata)
    {
        if (string.IsNullOrWhiteSpace(_metadataTitle) && !string.IsNullOrWhiteSpace(metadata.Name))
        {
            _metadataTitle = metadata.Name;
            OnPropertyChanged(nameof(Title));
        }

        if (!string.IsNullOrWhiteSpace(metadata.ArtworkPath) && File.Exists(metadata.ArtworkPath))
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
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
