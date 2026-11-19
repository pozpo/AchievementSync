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

        // Slow the mouse wheel down: WPF scrolls three lines per notch by default, which feels too fast for the
        // dense lists here. Scroll proportionally to the wheel delta instead — smooth for trackpads too.
        const double pixelsPerDeltaUnit = 48.0 / 120; // about 48 px per wheel notch
        var max = Math.Max(0, scrollViewer.ExtentHeight - scrollViewer.ViewportHeight);
        var offset = scrollViewer.VerticalOffset - e.Delta * pixelsPerDeltaUnit;
        scrollViewer.ScrollToVerticalOffset(Math.Clamp(offset, 0, max));
        e.Handled = true;
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
