using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;

namespace AchievementSync.Desktop;

public sealed record PlaytimeNotificationItem(
    string AppId,
    string GameTitle,
    bool IsStart,
    string Message,
    ImageSource? Artwork,
    bool CompactMode,
    Task<ImageSource?>? ScreenshotTask);

public partial class PlaytimeNotificationWindow : Window
{
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFO lpmi);

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private static readonly object QueueGate = new();
    private static readonly ConcurrentQueue<PlaytimeNotificationItem> PendingQueue = new();
    private static PlaytimeNotificationWindow? ActiveWindow;

    private readonly DispatcherTimer _dismissTimer;
    private readonly PlaytimeNotificationItem _item;

    private PlaytimeNotificationWindow(PlaytimeNotificationItem item)
    {
        InitializeComponent();
        _item = item;

        GameTitleText.Text = item.GameTitle;
        MessageText.Text = item.Message;
        BackgroundImage.Source = item.Artwork;

        if (item.IsStart)
        {
            BadgeText.Text = "P L A Y T I M E   S T A R T E D";
            BadgeText.Foreground = new SolidColorBrush(Color.FromRgb(255, 193, 90));
        }
        else
        {
            BadgeText.Text = "P L A Y T I M E   E N D E D";
            BadgeText.Foreground = new SolidColorBrush(Color.FromRgb(255, 137, 75));
        }

        ApplyTheming(item.Artwork, item.IsStart);

        _dismissTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(item.IsStart ? 3.8 : 4.8) };
        _dismissTimer.Tick += (_, _) => Dismiss();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void ApplyTheming(ImageSource? artwork, bool isStart)
    {
        Color accent = isStart ? Color.FromRgb(243, 170, 75) : Color.FromRgb(255, 119, 66);

        if (artwork is System.Windows.Media.Imaging.BitmapSource bitmap)
        {
            try
            {
                var dominant = ExtractDominantColor(bitmap);
                if (dominant != Colors.Transparent)
                {
                    accent = dominant;
                }
            }
            catch { }
        }

        RootBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(180, accent.R, accent.G, accent.B));
        AccentGlow.Background = new RadialGradientBrush(
            Color.FromArgb(70, accent.R, accent.G, accent.B),
            Colors.Transparent);
    }

    private static Color ExtractDominantColor(System.Windows.Media.Imaging.BitmapSource bitmap)
    {
        try
        {
            var scaleX = 16.0 / Math.Max(1, bitmap.PixelWidth);
            var scaleY = 16.0 / Math.Max(1, bitmap.PixelHeight);
            var thumb = new System.Windows.Media.Imaging.TransformedBitmap(bitmap, new ScaleTransform(scaleX, scaleY));
            var pixels = new byte[16 * 16 * 4];
            var stride = 16 * 4;
            var formatted = new System.Windows.Media.Imaging.FormatConvertedBitmap(thumb, PixelFormats.Bgra32, null, 0);
            formatted.CopyPixels(pixels, stride, 0);

            float bestScore = -1f;
            Color bestColor = Color.FromRgb(139, 124, 255);

            for (int i = 0; i < pixels.Length; i += 4)
            {
                byte b = pixels[i];
                byte g = pixels[i + 1];
                byte r = pixels[i + 2];
                byte a = pixels[i + 3];
                if (a < 128) continue;

                float max = Math.Max(r, Math.Max(g, b)) / 255f;
                float min = Math.Min(r, Math.Min(g, b)) / 255f;
                float delta = max - min;
                float sat = max == 0 ? 0 : delta / max;
                float lum = (max + min) / 2f;

                if (lum < 0.15f || lum > 0.90f || sat < 0.20f) continue;

                float score = sat * (1f - Math.Abs(lum - 0.5f));
                if (score > bestScore)
                {
                    bestScore = score;
                    bestColor = Color.FromRgb(r, g, b);
                }
            }
            return bestColor;
        }
        catch
        {
            return Color.FromRgb(139, 124, 255);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var workArea = GetActiveScreenWorkArea();
        var scale = Math.Min(_item.CompactMode ? 0.9 : 1.0,
            Math.Min((workArea.Width - 36) / 1000.0, (workArea.Height - 36) / 563.0));
        scale = Math.Clamp(scale, 0.55, 1.0);
        if (scale < 0.999)
        {
            RootBorder.LayoutTransform = new ScaleTransform(scale, scale);
            Width = 1000 * scale;
            Height = 563 * scale;
            UpdateLayout();
        }

        // This notification is a WPF window, so convert the monitor's physical
        // work-area coordinates to WPF device-independent units before centering.
        var left = workArea.Left + (workArea.Width - ActualWidth) / 2.0;
        var top = workArea.Top + (workArea.Height - ActualHeight) / 2.0;
        Left = left;
        Top = top;

        bool animationsEnabled = SystemParameters.ClientAreaAnimation && !SystemParameters.IsRemoteSession;
        if (animationsEnabled)
        {
            var transformGroup = new TransformGroup();
            var scaleTransform = new ScaleTransform(0.92, 0.92);
            transformGroup.Children.Add(scaleTransform);
            RootBorder.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
            RootBorder.RenderTransform = transformGroup;

            Opacity = 0;

            var scaleAnim = new DoubleAnimation(0.92, 1.0, new Duration(TimeSpan.FromMilliseconds(280)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            var fadeAnim = new DoubleAnimation(0, 1.0, new Duration(TimeSpan.FromMilliseconds(240)));

            scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
            scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
            BeginAnimation(OpacityProperty, fadeAnim);
        }
        else
        {
            Opacity = 1;
        }

        if (_item.ScreenshotTask is { } screenshotTask)
        {
            _ = ApplyFetchedScreenshotAsync(screenshotTask);
            _ = StartDismissTimerAfterScreenshotLookupAsync(screenshotTask);
        }
        else
        {
            _dismissTimer.Start();
        }
    }

    private async Task StartDismissTimerAfterScreenshotLookupAsync(Task<ImageSource?> screenshotTask)
    {
        // On a clean install, SteamDB has to be queried and the first screenshot
        // downloaded before this task can return. Give that cold-cache lookup a
        // short window before starting the visible notification's dismissal timer.
        await Task.WhenAny(screenshotTask, Task.Delay(TimeSpan.FromSeconds(6)));
        if (IsLoaded && !_dismissTimer.IsEnabled)
            _dismissTimer.Start();
    }

    private async Task ApplyFetchedScreenshotAsync(Task<ImageSource?> screenshotTask)
    {
        try
        {
            var screenshot = await screenshotTask.ConfigureAwait(false);
            if (screenshot is null || Dispatcher.HasShutdownStarted) return;
            await Dispatcher.InvokeAsync(() =>
            {
                if (!IsLoaded) return;
                BackgroundImage.Source = screenshot;
                ApplyTheming(screenshot, _item.IsStart);
            });
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or TaskCanceledException)
        {
            AppLog.Warning($"A playtime screenshot could not be displayed: {exception.GetType().Name}.");
        }
    }

    private void Dismiss()
    {
        _dismissTimer.Stop();

        bool animationsEnabled = SystemParameters.ClientAreaAnimation && !SystemParameters.IsRemoteSession;
        if (animationsEnabled)
        {
            var fadeOut = new DoubleAnimation(Opacity, 0, new Duration(TimeSpan.FromMilliseconds(220)))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            fadeOut.Completed += (_, _) => Close();
            BeginAnimation(OpacityProperty, fadeOut);
        }
        else
        {
            Close();
        }
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => Dismiss();

    private void OnClosed(object? sender, EventArgs e)
    {
        _dismissTimer.Stop();
        lock (QueueGate)
        {
            if (ReferenceEquals(ActiveWindow, this)) ActiveWindow = null;
            if (PendingQueue.IsEmpty) return;
        }

        var nextTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        nextTimer.Tick += (_, _) =>
        {
            nextTimer.Stop();
            lock (QueueGate) PresentNext();
        };
        nextTimer.Start();
    }

    public static void ShowPlaytimeNotification(
        string appId,
        string gameTitle,
        bool isStart,
        string message,
        ImageSource? artwork,
        bool compactMode = false,
        Task<ImageSource?>? screenshotTask = null)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted) return;
        if (!dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(new Action(() =>
                ShowPlaytimeNotification(appId, gameTitle, isStart, message, artwork, compactMode, screenshotTask)));
            return;
        }

        var item = new PlaytimeNotificationItem(appId, gameTitle, isStart, message, artwork, compactMode, screenshotTask);

        lock (QueueGate)
        {
            if (PendingQueue.Count >= 6) PendingQueue.TryDequeue(out _);
            PendingQueue.Enqueue(item);
            if (ActiveWindow is null) PresentNext();
        }
    }

    private static void PresentNext()
    {
        if (ActiveWindow is not null) return;
        if (!PendingQueue.TryDequeue(out var item)) return;
        ActiveWindow = new PlaytimeNotificationWindow(item);
        ActiveWindow.Show();
    }

    private Rect GetActiveScreenWorkArea()
    {
        try
        {
            var fgHwnd = GetForegroundWindow();
            if (fgHwnd != nint.Zero)
            {
                var hMonitor = MonitorFromWindow(fgHwnd, MONITOR_DEFAULTTONEAREST);
                if (hMonitor != nint.Zero)
                {
                    var mi = new MONITORINFO();
                    mi.cbSize = Marshal.SizeOf<MONITORINFO>();
                    if (GetMonitorInfo(hMonitor, ref mi))
                    {
                            var dpi = VisualTreeHelper.GetDpi(this);
                            var dpiX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
                            var dpiY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;
                            var left = mi.rcWork.Left / dpiX;
                            var top = mi.rcWork.Top / dpiY;
                            var width = (mi.rcWork.Right - mi.rcWork.Left) / dpiX;
                            var height = (mi.rcWork.Bottom - mi.rcWork.Top) / dpiY;
                            if (width > 200 && height > 200) return new Rect(left, top, width, height);
                    }
                }
            }
        }
        catch { }

        return SystemParameters.WorkArea;
    }
}
