using System.Windows;
using System.Windows.Threading;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace AchievementSync.Desktop;

public partial class App : Application
{
    public static bool StartInTray { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        try { System.IO.Directory.SetCurrentDirectory(AppContext.BaseDirectory); } catch { }
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        base.OnStartup(e);
        StartInTray = e.Args.Any(argument => string.Equals(argument, "--tray", StringComparison.OrdinalIgnoreCase));

        try
        {
            var mainWindow = new MainWindow();
            MainWindow = mainWindow;
            if (!StartInTray)
            {
                mainWindow.Show();
            }
            _ = mainWindow.InitializeAppAsync();
        }
        catch (Exception exception)
        {
            AppLog.Error("The desktop window could not be created.", exception);
            MessageBox.Show(
                "AchievementSync could not open its window. A local error log was written under AppData\\Local\\AchievementSync\\Logs.",
                "AchievementSync startup error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (MainWindow is MainWindow mainWindow && mainWindow.ViewModel is IDisposable disposable)
        {
            disposable.Dispose();
        }
        base.OnExit(e);
    }

    private void ScrollViewer_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (sender is not System.Windows.Controls.ScrollViewer scrollViewer) return;

        // Smooth, responsive mouse wheel scroll speed that matches modern desktop expectations
        // and respects the user's Windows mouse wheel scroll lines setting:
        var lines = SystemParameters.WheelScrollLines;
        var pixelsPerNotch = Math.Max(3, lines) * 40.0; // ~120px per standard 120 delta unit (2.5x faster than 48px)
        var pixelsPerDeltaUnit = pixelsPerNotch / 120.0;

        if (scrollViewer.ScrollableHeight > 0)
        {
            var offset = scrollViewer.VerticalOffset - (e.Delta * pixelsPerDeltaUnit);
            scrollViewer.ScrollToVerticalOffset(Math.Clamp(offset, 0, scrollViewer.ScrollableHeight));
            e.Handled = true;
        }
        else if (scrollViewer.ScrollableWidth > 0)
        {
            var offset = scrollViewer.HorizontalOffset - (e.Delta * pixelsPerDeltaUnit);
            scrollViewer.ScrollToHorizontalOffset(Math.Clamp(offset, 0, scrollViewer.ScrollableWidth));
            e.Handled = true;
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("An unhandled UI error occurred.", e.Exception);
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            AppLog.Error("An unhandled application error occurred.", exception);
        }
    }
}
