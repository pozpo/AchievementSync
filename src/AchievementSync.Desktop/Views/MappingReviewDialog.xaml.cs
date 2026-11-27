using System.Windows;
using System.Windows.Interop;
using AchievementSync.Desktop.Services;
using AchievementSync.Desktop.ViewModels;

namespace AchievementSync.Desktop.Views;

public partial class MappingReviewDialog : Window
{
    public MappingReviewDialog(MappingReviewViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        SourceInitialized += (_, _) => WindowAppearance.Apply(new WindowInteropHelper(this).Handle);
    }

    public MappingReviewViewModel ViewModel => (MappingReviewViewModel)DataContext;

    private void SaveMappings_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
