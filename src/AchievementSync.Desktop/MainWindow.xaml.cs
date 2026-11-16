using System.Windows;
using AchievementSync.Desktop.ViewModels;
using Microsoft.Win32;

namespace AchievementSync.Desktop;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    private bool _loaded;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.GameRequested += (_, game) =>
        {
            var details = new GameDetailsWindow(game.Profile, game.Title, game.Artwork) { Owner = this };
            details.ManualNameSubmitted += title => _viewModel.SetManualGameName(game, title);
            details.ShowDialog();
        };
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        await _viewModel.LoadAsync();
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose a folder containing achievement profiles",
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.AddCustomLocation(dialog.FolderName);
        }
    }
}
