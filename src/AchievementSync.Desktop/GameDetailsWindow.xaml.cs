using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using AchievementSync.Core.Models;
using AchievementSync.Desktop.Services;
using AchievementSync.Desktop.ViewModels;

namespace AchievementSync.Desktop;

public partial class GameDetailsWindow : Window
{
    private readonly ICollectionView _view;
    private readonly GameCardViewModel _game;
    private readonly ObservableCollection<AchievementRow> _rows;
    private string _filter = "All";

    public GameDetailsWindow(GameCardViewModel game)
    {
        _game = game ?? throw new ArgumentNullException(nameof(game));
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
        Closed += (_, _) => _game.PropertyChanged -= Game_PropertyChanged;
    }

    private bool FilterAchievement(object item)
    {
        if (item is not AchievementRow row) return false;
        if (_filter == "Unlocked" && row.Record.IsUnlocked != true) return false;
        if (_filter == "Locked" && row.Record.IsUnlocked != false) return false;
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
        var known = profile.Achievements.Where(item => item.IsUnlocked.HasValue).ToArray();
        var unlocked = known.Count(item => item.IsUnlocked == true);
        var locked = known.Length - unlocked;
        UnlockedText.Text = unlocked.ToString("N0");
        LockedText.Text = locked.ToString("N0");
        CompletionText.Text = profile.CompletionPercent is { } percentage ? $"{percentage:0.#}%" : "—";
        var unknown = profile.Achievements.Count(item => item.IsUnlocked is null);
        CatalogNote.Text = profile.HasCompleteCatalog
            ? $"{profile.Achievements.Count:N0} achievements in the catalog · {unknown:N0} unlock state(s) unknown"
            : $"{unlocked:N0} unlocked · {known.Length:N0} known states · total achievement catalog unavailable";
        if (viewerProgressIncluded)
            CatalogNote.Text += " · includes saved progress from the configured public Achievement Viewer";

        _rows.Clear();
        foreach (var achievement in profile.Achievements) _rows.Add(new AchievementRow(achievement));
        _view.Refresh();
        UpdateAchievementEmptyState();
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

internal sealed class AchievementRow
{
    public AchievementRow(AchievementRecord record)
    {
        Record = record;
        IconImage = CreateIconImage(record.IconUri);
    }

    public AchievementRecord Record { get; }
    public System.Windows.Media.Imaging.BitmapImage? IconImage { get; }
    public string DisplayName => string.IsNullOrWhiteSpace(Record.DisplayName)
        ? Record.ApiName
        : Record.DisplayName;
    public string IdentifierText => string.IsNullOrWhiteSpace(Record.DisplayName)
        ? "Achievement identifier from local profile"
        : Record.ApiName;
    public string DescriptionText => Record.IsHidden
        ? "Hidden achievement"
        : Record.Description ?? "Description is not included in this local profile.";
    public string StateText => Record.IsUnlocked switch { true => "UNLOCKED", false => "LOCKED", _ => "UNKNOWN" };
    public Brush StateBrush => Record.IsUnlocked switch
    {
        true => new SolidColorBrush(Color.FromRgb(114, 224, 187)),
        false => new SolidColorBrush(Color.FromRgb(153, 165, 184)),
        _ => new SolidColorBrush(Color.FromRgb(229, 184, 112))
    };
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

    private static BitmapImage? CreateIconImage(string? iconUri)
    {
        if (string.IsNullOrWhiteSpace(iconUri)) return null;
        if (!Uri.TryCreate(iconUri, UriKind.Absolute, out var uri)) return null;

        var isLocal = uri.Scheme == Uri.UriSchemeFile;
        var isSteamHost = uri.Scheme == Uri.UriSchemeHttps
            && (uri.Host.EndsWith(".steamstatic.com", StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Host, "steamcdn-a.akamaihd.net", StringComparison.OrdinalIgnoreCase));
        if (!isLocal && !isSteamHost) return null;
        if (isLocal && !File.Exists(uri.LocalPath)) return null;

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
            else
            {
                image.CacheOption = BitmapCacheOption.OnDemand;
                image.UriSource = uri;
            }
            image.EndInit();
            if (image.CanFreeze) image.Freeze();
            return image;
        }
        catch (Exception exception) when (exception is IOException or UriFormatException or NotSupportedException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }
}
