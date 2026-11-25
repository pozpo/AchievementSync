using System.Windows;
using AchievementSync.Core.Enabler;
using MessageBox = System.Windows.MessageBox;

namespace AchievementSync.Desktop.Views;

public partial class SteamTwoFactorDialog : Window
{
    public string? EnteredCode { get; private set; }

    public SteamTwoFactorDialog(SteamTwoFactorChallenge challenge)
    {
        InitializeComponent();
        PromptDescription.Text = $"Verification needed for Steam user: {challenge.Username}\nCheck your Steam Mobile App or Email for the security code.";
        if (challenge.IsRetry)
        {
            ErrorMessage.Visibility = Visibility.Visible;
        }

        Loaded += (_, _) =>
        {
            CodeBox.Focus();
            CodeBox.SelectAll();
        };
    }

    private void Submit_Click(object sender, RoutedEventArgs e)
    {
        var code = CodeBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(code))
        {
            MessageBox.Show("Please enter the Steam Guard code, or click 'Approved on Steam App' if you approved it on your phone.",
                "Code Required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        EnteredCode = code.ToUpperInvariant();
        DialogResult = true;
        Close();
    }

    private void AppApproved_Click(object sender, RoutedEventArgs e)
    {
        // When approved via mobile app, Steam expects an empty line (pressing Enter)
        EnteredCode = string.Empty;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        EnteredCode = null;
        DialogResult = false;
        Close();
    }
}
