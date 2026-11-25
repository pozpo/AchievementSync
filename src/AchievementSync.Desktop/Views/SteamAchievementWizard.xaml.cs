using System.Windows;
using System.Windows.Controls;
using AchievementSync.Desktop.ViewModels;

namespace AchievementSync.Desktop.Views;

/// <summary>Multi-step wizard for the eternal Steam achievement generator flow.</summary>
public partial class SteamAchievementWizard : Page
{
    internal SteamAchievementWizardViewModel ViewModel { get; }

    internal SteamAchievementWizard(SteamAchievementWizardViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = ViewModel;
        ViewModel.BrowseGameRequested += OnBrowseGameRequested;
        ViewModel.BrowseGeneratorRequested += OnBrowseGeneratorRequested;
        ViewModel.TwoFactorPromptHandler = challenge =>
        {
            return Dispatcher.InvokeAsync(() =>
            {
                var dialog = new SteamTwoFactorDialog(challenge)
                {
                    Owner = Window.GetWindow(this)
                };
                return dialog.ShowDialog() == true ? dialog.EnteredCode : null;
            }).Task;
        };
    }

    private void SteamPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        ViewModel.SteamPassword = SteamPasswordBox.Password;
    }

    private void OnBrowseGameRequested(object? sender, System.EventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select game executable or folder",
            Filter = "Executable (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() == true)
        {
            ViewModel.GameFolder = dialog.FileName;
        }
    }

    private void OnBrowseGeneratorRequested(object? sender, System.EventArgs e)
    {
        var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select the generate_emu_config.exe folder",
            UseDescriptionForTitle = true
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            ViewModel.GeneratorRoot = dialog.SelectedPath;
        }
    }
}
