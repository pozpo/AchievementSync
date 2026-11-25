using System.Windows;
using System.Windows.Media;

namespace AchievementSync.Desktop.Views;

public partial class SteamLoginResultDialog : Window
{
    public SteamLoginResultDialog(string title, string message, bool isSuccess)
    {
        InitializeComponent();
        DialogTitle.Text = title;
        DialogMessage.Text = message;
        if (!isSuccess)
        {
            StatusBadge.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(56, 39, 31));
            StatusIcon.Text = "!";
            StatusIcon.Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("WarningBrush");
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
