using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using AchievementSync.Core.Metadata;
using AchievementSync.Core.Models;
using AchievementSync.Desktop.Services;
using AchievementSync.Desktop.ViewModels;

namespace AchievementSync.Desktop;

public partial class GameDetailsWindow : Window
{
    private readonly ICollectionView _view;
    private readonly GameCardViewModel _game;
    private readonly ObservableCollection<AchievementRow> _rows;
    private readonly Func<Task>? _refreshCatalogAsync;
    private string _filter = "All";

    public GameDetailsWindow(GameCardViewModel game, Func<Task>? refreshCatalogAsync = null)
    {
        _game = game ?? throw new ArgumentNullException(nameof(game));
        _refreshCatalogAsync = refreshCatalogAsync;
        InitializeComponent();
        SourceInitialized += (_, _) => WindowAppearance.Apply(new WindowInteropHelper(this).Handle);
        GameTitle.Text = game.Title;
        HeroArtwork.Source = game.Artwork;
        _rows = [];
        _view = CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = FilterAchievement;
        AchievementList.ItemsSource = _view;
        RefreshProfileDetails();
        UpdatePlaytimeText();
        _game.PropertyChanged += Game_PropertyChanged;
        Loaded += OnLoaded;
        Closed += (_, _) => _game.PropertyChanged -= Game_PropertyChanged;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var screen = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle);
            var work = screen.WorkingArea;
            var dpi = VisualTreeHelper.GetDpi(this);
            var dpiX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
            var dpiY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;

            var workWidth = work.Width / dpiX;
            var workHeight = work.Height / dpiY;

            if (Height > workHeight * 0.92) Height = workHeight * 0.92;
            if (Width > workWidth * 0.95) Width = workWidth * 0.95;

            var workLeft = work.Left / dpiX;
            var workTop = work.Top / dpiY;

            if (Top + Height > workTop + workHeight) Top = Math.Max(workTop, workTop + workHeight - Height - 10);
            if (Left + Width > workLeft + workWidth) Left = Math.Max(workLeft, workLeft + workWidth - Width - 10);
        }
        catch { }

        await CacheAchievementIconsAsync();
    }

    private async Task CacheAchievementIconsAsync()
    {
        var profile = _game.Profile;
        if (profile.Achievements.Count == 0) return;
        try
        {
            var missingCachedArtwork = HasMissingCachedArtwork(profile);
            if (missingCachedArtwork && _refreshCatalogAsync is not null)
            {
                // Prefer the configured full viewer catalog before falling back to Steam's partial public page.
                await _refreshCatalogAsync();
                profile = _game.Profile;
                missingCachedArtwork = HasMissingCachedArtwork(profile);
            }

            var catalog = new ParsedProfile(
                profile.AppId,
                "Game details achievement catalog",
                $"local://AppID/{profile.AppId}/game-details",
                DateTimeOffset.UtcNow,
                profile.Achievements.Select(item => item with { IsUnlocked = null, UnlockTimeUnix = null }).ToArray(),
                profile.HasCompleteCatalog,
                _game.Title);
            var cached = await new LocalAchievementCatalogCache().CacheCatalogAsync(profile.AppId, catalog);
            if (cached is not null) _game.ApplyCatalog(cached);
            var imageDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AchievementSync", "Metadata", profile.AppId, "images");
            var imageCount = Directory.Exists(imageDirectory) ? Directory.EnumerateFiles(imageDirectory).Count() : 0;

            if (missingCachedArtwork && AppIdValidator.IsValid(profile.AppId))
            {
                // A profile can still be in memory after Metadata/<AppID>/images was removed.
                // Its old file:// URIs cannot be copied back, so fetch the public Steam catalog again.
                var community = await new SteamCommunityCatalogClient().GetCatalogAsync(profile.AppId, _game.Title);
                if (community is not null)
                {
                    var refreshed = await new LocalAchievementCatalogCache().CacheCatalogAsync(
                        profile.AppId,
                        community.Catalog,
                        source: "steam-community",
                        declaredTotal: community.DeclaredTotal);
                    if (refreshed is not null) _game.ApplyCatalog(refreshed);
                    imageCount = Directory.Exists(imageDirectory) ? Directory.EnumerateFiles(imageDirectory).Count() : 0;
                }
            }

            AppLog.Info($"Achievement icons checked for AppID {profile.AppId}; images={imageCount}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or HttpRequestException
                                           or JsonException or TaskCanceledException or InvalidDataException or NotSupportedException)
        {
            AppLog.Warning($"Could not cache achievement icons for AppID {profile.AppId}: {exception.GetType().Name}.");
        }
    }

    private static bool HasMissingCachedArtwork(GameProfile profile) => profile.Achievements.Any(achievement =>
        (string.IsNullOrWhiteSpace(achievement.IconUri) && string.IsNullOrWhiteSpace(achievement.LockedIconUri))
        || IsMissingMetadataArtwork(achievement.IconUri)
        || IsMissingMetadataArtwork(achievement.LockedIconUri));

    private static bool IsMissingMetadataArtwork(string? iconUri)
    {
        if (!AchievementIdentity.IsMetadataImageUri(iconUri)
            || !Uri.TryCreate(iconUri, UriKind.Absolute, out var uri)) return false;
        try { return !File.Exists(uri.LocalPath); }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return true;
        }
    }

    private bool FilterAchievement(object item)
    {
        if (item is not AchievementRow row) return false;
        if (_filter == "Unlocked" && row.Record.IsUnlocked != true) return false;
        if (_filter == "Locked" && row.Record.IsUnlocked == true) return false;
        return string.IsNullOrWhiteSpace(SearchBox?.Text)
            || row.DisplayName.Contains(SearchBox.Text, StringComparison.CurrentCultureIgnoreCase)
            || row.Record.ApiName.Contains(SearchBox.Text, StringComparison.OrdinalIgnoreCase);
    }

    private void FilterButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string filter)
        {
            _filter = filter;
            _view.Refresh();
            UpdateAchievementEmptyState();
        }
    }

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _view?.Refresh();
        UpdateAchievementEmptyState();
    }

    private void UpdateAchievementEmptyState() =>
        AchievementEmptyState.Visibility = GetAchievementEmptyStateVisibility(AchievementList.Items.Count);

    internal static Visibility GetAchievementEmptyStateVisibility(int visibleAchievementCount) =>
        visibleAchievementCount == 0 ? Visibility.Visible : Visibility.Collapsed;

    public event Action<string?>? ManualNameSubmitted;
    public event Action<GameCardViewModel>? ExecutableAssociationRequested;

    private void Game_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GameCardViewModel.Title)) GameTitle.Text = _game.Title;
        if (e.PropertyName == nameof(GameCardViewModel.Profile)) RefreshProfileDetails();
        if (e.PropertyName is nameof(GameCardViewModel.PlaytimeText)
            or nameof(GameCardViewModel.LastPlayedText)
            or nameof(GameCardViewModel.CurrentSessionText)
            or nameof(GameCardViewModel.ExecutableAssociationText))
        {
            UpdatePlaytimeText();
        }
    }

    private void RefreshProfileDetails()
    {
        var profile = _game.Profile;
        var viewerProgressIncluded = profile.SourceFiles.Any(source => source.StartsWith("viewer://", StringComparison.OrdinalIgnoreCase));
        var localSourceCount = profile.SourceFiles.Count(source => !source.StartsWith("viewer://", StringComparison.OrdinalIgnoreCase));
        AppIdText.Text = viewerProgressIncluded
            ? $"Steam AppID {profile.AppId}  ·  {localSourceCount} local profile file(s) + public viewer data"
            : $"Steam AppID {profile.AppId}  ·  {localSourceCount} local profile file(s)";
        var unlocked = profile.Achievements.Count(item => item.IsUnlocked == true);
        var locked = Math.Max(0, (profile.TotalCount ?? profile.Achievements.Count) - unlocked);
        UnlockedText.Text = unlocked.ToString("N0");
        LockedText.Text = locked.ToString("N0");
        CompletionText.Text = profile.CompletionPercent is { } percentage ? $"{percentage:0.#}%" : "—";
        CatalogNote.Text = profile.HasCompleteCatalog
            ? $"{profile.Achievements.Count:N0} achievements in the catalog · unlisted achievements are shown as locked"
            : profile.TotalCount is { } declaredTotal
                ? $"{declaredTotal:N0} achievements listed online · {profile.Achievements.Count:N0} definitions available locally"
                : $"{unlocked:N0} unlocked · {profile.KnownStateCount:N0} saved states · full achievement catalog unavailable";
        if (viewerProgressIncluded)
            CatalogNote.Text += " · includes saved progress from the configured public Achievement Viewer";

        _rows.Clear();
        foreach (var achievement in profile.Achievements) _rows.Add(new AchievementRow(achievement));
        _view.Refresh();
        UpdateAchievementEmptyState();
        _ = PreloadGameIconsAsync(profile.Achievements);
    }

    private static async Task PreloadGameIconsAsync(IReadOnlyList<AchievementRecord> achievements)
    {
        var uris = achievements
            .Select(a => a.IconUri)
            .Where(AchievementIdentity.IsMetadataImageUri)
            .Where(uri => !string.IsNullOrWhiteSpace(uri))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        using var throttle = new SemaphoreSlim(6, 6);
        var tasks = uris.Select(async uri =>
        {
            await throttle.WaitAsync().ConfigureAwait(false);
            try
            {
                await AchievementArtworkCache.GetAsync(uri).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                AppLog.Warning($"Achievement artwork download failed: {exception.GetType().Name}.");
            }
            finally
            {
                throttle.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private void UpdatePlaytimeText()
    {
        PlaytimeText.Text = _game.PlaytimeText;
        LastPlayedText.Text = _game.LastPlayedText;
        CurrentSessionText.Text = _game.CurrentSessionText;
        ExecutableInfo.Text = _game.ExecutableAssociationText;
    }

    private void LinkExecutable_Click(object sender, RoutedEventArgs e) => ExecutableAssociationRequested?.Invoke(_game);

    private void EditGameName_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new GameNameDialog(GameTitle.Text) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        GameTitle.Text = dialog.GameName;
        ManualNameSubmitted?.Invoke(dialog.GameName);
    }
}

internal sealed class AchievementRow : INotifyPropertyChanged
{
    private BitmapImage? _iconImage;

    public event PropertyChangedEventHandler? PropertyChanged;

    public AchievementRow(AchievementRecord record)
    {
        Record = record;
        var iconUri = record.IsUnlocked != true ? record.LockedIconUri ?? record.IconUri : record.IconUri ?? record.LockedIconUri;
        if (!AchievementIdentity.IsMetadataImageUri(iconUri)) iconUri = null;
        _iconImage = AchievementArtworkCache.TryGetCached(iconUri);
        if (_iconImage is null && !string.IsNullOrWhiteSpace(iconUri))
        {
            _iconImage = CreateIconImage(iconUri);
            _ = LoadIconAsync(iconUri);
        }
    }

    private async Task LoadIconAsync(string uri)
    {
        try
        {
            var image = await AchievementArtworkCache.GetAsync(uri).ConfigureAwait(false);
            if (image is not null)
            {
                _iconImage = image;
                if (System.Windows.Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
                {
                    _ = dispatcher.InvokeAsync(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconImage))));
                }
                else
                {
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconImage)));
                }
            }
        }
        catch (Exception exception)
        {
            AppLog.Warning($"Achievement icon could not be loaded from the local artwork cache: {exception.GetType().Name}.");
        }
    }

    private static BitmapImage? CreateIconImage(string? iconUri)
    {
        if (!AchievementIdentity.IsMetadataImageUri(iconUri)) return null;

        var cached = AchievementArtworkCache.TryGetCached(iconUri);
        if (cached is not null) return cached;

        if (!Uri.TryCreate(iconUri, UriKind.Absolute, out var uri)) return null;

        var isLocal = uri.Scheme == Uri.UriSchemeFile;
        if (!isLocal || !File.Exists(uri.LocalPath)) return null;

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.DecodePixelWidth = 96;
            if (isLocal)
            {
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = new MemoryStream(File.ReadAllBytes(uri.LocalPath), writable: false);
            }
            else return null;
            image.EndInit();
            if (image.CanFreeze) image.Freeze();
            return image;
        }
        catch (Exception exception) when (exception is IOException or UriFormatException or NotSupportedException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    public AchievementRecord Record { get; }
    public BitmapImage? IconImage => _iconImage;
    public string DisplayName => AchievementIdentity.IsIdentifierLikeDisplayName(Record.ApiName, Record.DisplayName)
        ? AchievementIdentity.HumanizeApiName(Record.ApiName)
        : Record.DisplayName!;
    public string IdentifierText => Record.ApiName;
    public string DescriptionText => Record.IsHidden
        ? "Hidden achievement"
        : Record.Description ?? "Description unavailable.";
    public string StateText
    {
        get
        {
            if (Record.IsUnlocked != true) return "LOCKED";
            var trophy = (Record.TrophyType ?? string.Empty).ToLowerInvariant();
            if (trophy == "platinum") return "PLATINUM";
            if (trophy == "gold") return "GOLD";
            if (trophy == "silver") return "SILVER";
            if (trophy == "bronze") return "BRONZE";
            return "UNLOCKED";
        }
    }

    public Brush StateBrush
    {
        get
        {
            if (Record.IsUnlocked != true) return new SolidColorBrush(Color.FromRgb(153, 165, 184));
            var trophy = (Record.TrophyType ?? string.Empty).ToLowerInvariant();
            if (trophy == "platinum") return new SolidColorBrush(Color.FromRgb(140, 232, 255));
            if (trophy == "gold") return new SolidColorBrush(Color.FromRgb(255, 215, 0));
            if (trophy == "silver") return new SolidColorBrush(Color.FromRgb(208, 213, 221));
            if (trophy == "bronze") return new SolidColorBrush(Color.FromRgb(205, 127, 50));
            return new SolidColorBrush(Color.FromRgb(114, 224, 187));
        }
    }
    public string UnlockDateText => Record.IsUnlocked == true && Record.UnlockTimeUnix is > 0
        ? FormatUnlockDate(Record.UnlockTimeUnix.Value)
        : string.Empty;

    private static string FormatUnlockDate(long unixTime)
    {
        try
        {
            return $"Unlocked {DateTimeOffset.FromUnixTimeSeconds(unixTime).ToLocalTime().ToString("MMM d, yyyy · h:mm tt", CultureInfo.CurrentCulture)}";
        }
        catch (ArgumentOutOfRangeException)
        {
            return "Unlock date unavailable";
        }
    }
}
