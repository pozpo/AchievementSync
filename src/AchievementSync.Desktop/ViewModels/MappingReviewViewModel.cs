using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using AchievementSync.Core.Platforms;

namespace AchievementSync.Desktop.ViewModels;

public sealed class MappingReviewItemViewModel : INotifyPropertyChanged
{
    private string _overrideAppId;
    private bool _isBlacklisted;

    public MappingReviewItemViewModel(SteamAppIdResolution resolution)
    {
        Platform = resolution.Platform;
        SourceGameId = resolution.SourceGameId;
        SourceTitle = !string.IsNullOrWhiteSpace(resolution.SourceTitle) ? resolution.SourceTitle : resolution.SourceGameId;
        ResolvedSteamAppId = resolution.ResolvedSteamAppId ?? string.Empty;
        Confidence = resolution.Confidence;
        MatchReason = resolution.MatchReason;
        IsOverride = resolution.IsOverride;
        _isBlacklisted = resolution.IsBlacklisted;
        _overrideAppId = resolution.IsOverride ? (resolution.ResolvedSteamAppId ?? string.Empty) : string.Empty;
        NormalizedAchievementCount = resolution.NormalizedAchievements.Count;
        DiscardedCount = resolution.DiscardedCount;
    }

    public string Platform { get; }
    public string SourceGameId { get; }
    public string SourceTitle { get; }
    public string ResolvedSteamAppId { get; }
    public SteamMappingConfidence Confidence { get; }
    public string MatchReason { get; }
    public bool IsOverride { get; }
    public int NormalizedAchievementCount { get; }
    public int DiscardedCount { get; }

    public string OverrideKey => SteamAppIdResolver.BuildOverrideKey(Platform, SourceGameId);

    public string OverrideAppId
    {
        get => _overrideAppId;
        set
        {
            if (_overrideAppId == value) return;
            _overrideAppId = value?.Trim() ?? string.Empty;
            OnPropertyChanged(nameof(OverrideAppId));
            OnPropertyChanged(nameof(EffectiveAppId));
        }
    }

    public bool IsBlacklisted
    {
        get => _isBlacklisted;
        set
        {
            if (_isBlacklisted == value) return;
            _isBlacklisted = value;
            OnPropertyChanged(nameof(IsBlacklisted));
        }
    }

    public string EffectiveAppId => !string.IsNullOrWhiteSpace(_overrideAppId) ? _overrideAppId : ResolvedSteamAppId;
    public string ConfidenceDisplay => Confidence.ToString();

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class MappingReviewViewModel : INotifyPropertyChanged
{
    private string _filterText = string.Empty;
    private readonly ICollectionView _itemsView;

    public MappingReviewViewModel(IEnumerable<SteamAppIdResolution> resolutions)
    {
        var list = resolutions
            .Select(r => new MappingReviewItemViewModel(r))
            .OrderBy(item => item.Platform, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.SourceTitle, StringComparer.OrdinalIgnoreCase);

        Items = new ObservableCollection<MappingReviewItemViewModel>(list);
        _itemsView = CollectionViewSource.GetDefaultView(Items);
        _itemsView.Filter = FilterItem;
    }

    public ObservableCollection<MappingReviewItemViewModel> Items { get; }
    public ICollectionView ItemsView => _itemsView;

    public string FilterText
    {
        get => _filterText;
        set
        {
            if (_filterText == value) return;
            _filterText = value;
            OnPropertyChanged(nameof(FilterText));
            _itemsView.Refresh();
        }
    }

    public int TotalCount => Items.Count;

    private bool FilterItem(object obj)
    {
        if (obj is not MappingReviewItemViewModel item) return false;
        if (string.IsNullOrWhiteSpace(_filterText)) return true;

        var term = _filterText.Trim();
        return item.SourceTitle.Contains(term, StringComparison.OrdinalIgnoreCase)
            || item.Platform.Contains(term, StringComparison.OrdinalIgnoreCase)
            || item.SourceGameId.Contains(term, StringComparison.OrdinalIgnoreCase)
            || item.EffectiveAppId.Contains(term, StringComparison.OrdinalIgnoreCase)
            || item.ConfidenceDisplay.Contains(term, StringComparison.OrdinalIgnoreCase);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
