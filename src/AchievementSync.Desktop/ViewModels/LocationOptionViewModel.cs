using System.ComponentModel;
using System.Runtime.CompilerServices;
using AchievementSync.Core.Scanning;

namespace AchievementSync.Desktop.ViewModels;

public sealed class LocationOptionViewModel : INotifyPropertyChanged
{
    private bool _isEnabled;

    public LocationOptionViewModel(ScanLocation location, Action<LocationOptionViewModel>? changed = null)
    {
        Id = location.Id;
        Name = location.Name;
        Path = location.Path;
        IsCustom = location.IsCustom;
        IsAvailable = location.IsAvailable;
        FilesFound = location.FilesFound;
        Error = location.Error;
        _isEnabled = location.IsEnabled;
        Changed = changed;
    }

    private Action<LocationOptionViewModel>? Changed { get; }

    public string Id { get; }
    public string Name { get; }
    public string Path { get; }
    public bool IsCustom { get; }
    public bool IsAvailable { get; private set; }
    public int FilesFound { get; private set; }
    public string? Error { get; private set; }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value) return;
            _isEnabled = value;
            OnPropertyChanged();
            Changed?.Invoke(this);
        }
    }

    public string AvailabilityText => IsAvailable ? "Available" : "Unavailable";
    public string DiscoveryText => FilesFound == 0 ? "No supported files seen yet" : $"{FilesFound} possible file(s)";

    public void Update(ScanLocation location)
    {
        IsAvailable = location.IsAvailable;
        FilesFound = location.FilesFound;
        Error = location.Error;
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(AvailabilityText));
        OnPropertyChanged(nameof(FilesFound));
        OnPropertyChanged(nameof(DiscoveryText));
        OnPropertyChanged(nameof(Error));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
