using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Microsoft.Win32;

namespace AchievementSync.Desktop.ViewModels;

public sealed class PlatformSourceRootViewModel : INotifyPropertyChanged
{
    private readonly Action<string, string> _pathChanged;
    private string _path;

    public PlatformSourceRootViewModel(string key, string label, string path, Action<string, string> pathChanged)
    {
        Key = key;
        Label = label;
        _path = path;
        _pathChanged = pathChanged;
        BrowseCommand = new FolderBrowseCommand(this);
    }

    public string Key { get; }
    public string Label { get; }

    public string Path
    {
        get => _path;
        set
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (string.Equals(_path, normalized, StringComparison.Ordinal)) return;
            _path = normalized;
            OnPropertyChanged();
            _pathChanged(Key, normalized);
        }
    }

    public ICommand BrowseCommand { get; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private sealed class FolderBrowseCommand(PlatformSourceRootViewModel owner) : ICommand
    {
        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter)
        {
            var dialog = new OpenFolderDialog
            {
                Title = $"Choose {owner.Label} folder",
                InitialDirectory = Directory.Exists(owner.Path) ? owner.Path : null,
                Multiselect = false
            };
            if (dialog.ShowDialog() == true) owner.Path = dialog.FolderName;
        }

        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }
    }
}
