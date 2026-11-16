using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AchievementSync.Core.Models;
using Microsoft.VisualBasic;

namespace AchievementSync.Desktop;

public partial class GameDetailsWindow : Window
{
    private readonly ICollectionView _view;
    private string _filter = "All";

    public GameDetailsWindow(GameProfile profile, string title, BitmapImage? artwork)
    {
        InitializeComponent();
        GameTitle.Text = title;
        HeroArtwork.Source = artwork;
        AppIdText.Text = $"Steam AppID {profile.AppId}  ·  {profile.SourceFiles.Count} local profile file(s)";
        var known = profile.Achievements.Where(item => item.IsUnlocked.HasValue).ToArray();
        var unlocked = known.Count(item => item.IsUnlocked == true);
        var locked = known.Length - unlocked;
        UnlockedText.Text = unlocked.ToString("N0");
        LockedText.Text = locked.ToString("N0");
        CompletionText.Text = profile.CompletionPercent is { } percentage ? $"{percentage:0.#}%" : "—";
        var unknown = profile.Achievements.Count(item => item.IsUnlocked is null);
        CatalogNote.Text = profile.HasCompleteCatalog
            ? $"{profile.Achievements.Count:N0} achievements in the catalog · {unknown:N0} unlock state(s) unknown"
            : $"{known.Length:N0} known entries · this source does not prove the full game catalog";

        var rows = new ObservableCollection<AchievementRow>(profile.Achievements.Select(item => new AchievementRow(item)));
        _view = CollectionViewSource.GetDefaultView(rows);
        _view.Filter = FilterAchievement;
        AchievementList.ItemsSource = _view;
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
        }
    }

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _view?.Refresh();
    }

    public event Action<string?>? ManualNameSubmitted;

    private void IdentifyGame_Click(object sender, RoutedEventArgs e)
    {
        var title = Interaction.InputBox(
            "Enter a display name for this AppID. The name is saved on this PC.",
            "Identify game",
            GameTitle.Text);
        if (string.IsNullOrWhiteSpace(title)) return;
        GameTitle.Text = title.Trim();
        ManualNameSubmitted?.Invoke(title.Trim());
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
        if (!Uri.TryCreate(iconUri, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !(uri.Host.EndsWith(".steamstatic.com", StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Host, "steamcdn-a.akamaihd.net", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnDemand;
            image.UriSource = uri;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception exception) when (exception is IOException or UriFormatException or NotSupportedException)
        {
            return null;
        }
    }
}
