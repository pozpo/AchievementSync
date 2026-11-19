using System.Windows;
using MessageBox = System.Windows.MessageBox;
using System.Windows.Input;
using System.Windows.Interop;
using AchievementSync.Desktop.Services;

namespace AchievementSync.Desktop;

public partial class GameNameDialog : Window
{
    public GameNameDialog(string currentName)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowAppearance.Apply(new WindowInteropHelper(this).Handle);
        GameNameTextBox.Text = currentName;
        GameNameTextBox.SelectAll();
        Loaded += (_, _) =>
        {
            GameNameTextBox.Focus();
            Keyboard.Focus(GameNameTextBox);
        };
    }

    public string GameName => GameNameTextBox.Text.Trim();

    private void SaveName_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(GameName))
        {
            MessageBox.Show(this, "Enter a name or cancel to keep the current display name.",
                "Game name required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
    }
}
