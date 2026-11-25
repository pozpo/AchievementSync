using AchievementSync.Core.Tracking;
using AchievementSync.Desktop.Services;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;

namespace AchievementSync.Desktop;

/// <summary>Controls how a toast is placed and whether it makes a sound. Immutable so it can be safely shared with queued toasts.</summary>
public sealed record ToastPresentation(
    string Position,
    int OffsetX,
    int OffsetY,
    bool SoundEnabled,
    int SoundVolume,
    string SoundKind = "AchievementSync",
    string SoundPath = "",
    bool CompactMode = false,
    bool AutoDetectActiveScreen = true)
{
    public static ToastPresentation Default { get; } = new("TopCenter", 0, 0, SoundEnabled: true, SoundVolume: 70);
}

public sealed record ToastDisplayItem(
    string AppId,
    string KeyId,
    string Header,
    string Title,
    string Description,
    string? Footer,
    string? Glyph,
    string? TrophyType,
    int? ProgressCurrent,
    int? ProgressMax,
    ImageSource? Icon,
    ToastPresentation Presentation);

public partial class AchievementToastWindow : Window
{
    private const double EntranceDurationMs = 520;
    private const double ExitDurationMs = 300;
    private const double EntranceSlideDistance = 44;

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
    private static readonly ConcurrentQueue<ToastDisplayItem> Pending = new();
    private static AchievementToastWindow? ActiveToast;

    private readonly string _appId;
    private readonly string _achievementId;
    private readonly DispatcherTimer _dismissTimer;
    private Rect _activeWorkArea;

    private AchievementToastWindow(ToastDisplayItem item)
    {
        InitializeComponent();
        Tag = item.Presentation;
        _appId = item.AppId;
        _achievementId = item.KeyId;

        HeaderTagText.Text = item.Header;
        AchievementTitleText.Text = item.Title;
        DescriptionText.Text = item.Description;
        GameNameText.Text = item.Footer ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(item.Glyph)) IconGlyph.Text = item.Glyph;

        SetIcon(item.Icon);
        ApplyTheming(item);

        _dismissTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _dismissTimer.Tick += (_, _) => Dismiss();
        Loaded += OnLoaded;
        Closed += OnClosed;
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
            Color bestColor = Color.FromRgb(161, 139, 255);

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
            return Color.FromRgb(161, 139, 255);
        }
    }

    private void ApplyTheming(ToastDisplayItem item)
    {
        Color accentColor = Color.FromRgb(161, 139, 255);
        if (item.Icon is System.Windows.Media.Imaging.BitmapSource bitmap)
        {
            accentColor = ExtractDominantColor(bitmap);
        }

        var trophy = (item.TrophyType ?? string.Empty).ToLowerInvariant();
        if (trophy == "platinum")
        {
            HeaderTagText.Text = "PLATINUM TROPHY UNLOCKED";
            accentColor = Color.FromRgb(140, 232, 255); // Platinum shine
            IconGlyph.Text = "🏆";
        }
        else if (trophy == "gold")
        {
            HeaderTagText.Text = "GOLD TROPHY UNLOCKED";
            accentColor = Color.FromRgb(255, 215, 0);
            IconGlyph.Text = "🏆";
        }
        else if (trophy == "silver")
        {
            HeaderTagText.Text = "SILVER TROPHY UNLOCKED";
            accentColor = Color.FromRgb(208, 213, 221);
            IconGlyph.Text = "🏆";
        }
        else if (trophy == "bronze")
        {
            HeaderTagText.Text = "BRONZE TROPHY UNLOCKED";
            accentColor = Color.FromRgb(205, 127, 50);
            IconGlyph.Text = "🏆";
        }
        else if (!string.IsNullOrWhiteSpace(item.Header))
        {
            HeaderTagText.Text = item.Header;
        }

        var brush = new SolidColorBrush(accentColor);
        HeaderTagText.Foreground = brush;
        IconGlyph.Foreground = brush;
        RootBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(190, accentColor.R, accentColor.G, accentColor.B));
        AccentGlow.Background = new LinearGradientBrush(
            Color.FromArgb(100, accentColor.R, accentColor.G, accentColor.B),
            Colors.Transparent,
            90);

        if (item.ProgressCurrent.HasValue && item.ProgressMax.HasValue && item.ProgressMax.Value > 0)
        {
            ProgressContainer.Visibility = Visibility.Visible;
            double pct = Math.Clamp((double)item.ProgressCurrent.Value / item.ProgressMax.Value * 100.0, 0, 100);
            AchievementProgressBar.Value = pct;
            AchievementProgressBar.Foreground = brush;
            ProgressText.Text = $"{item.ProgressCurrent.Value} / {item.ProgressMax.Value}";
        }
        else
        {
            ProgressContainer.Visibility = Visibility.Collapsed;
        }
    }

    public void UpdateProgress(ToastDisplayItem item)
    {
        var description = item.Description?.Trim();
        DescriptionText.Text = string.IsNullOrWhiteSpace(description) ? "Progress updated." : description;

        if (item.ProgressCurrent.HasValue && item.ProgressMax.HasValue && item.ProgressMax.Value > 0)
        {
            ProgressContainer.Visibility = Visibility.Visible;
            double newPct = Math.Clamp((double)item.ProgressCurrent.Value / item.ProgressMax.Value * 100.0, 0, 100);
            var anim = new DoubleAnimation(AchievementProgressBar.Value, newPct, new Duration(TimeSpan.FromMilliseconds(250)))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            AchievementProgressBar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, anim);
            ProgressText.Text = $"{item.ProgressCurrent.Value} / {item.ProgressMax.Value}";
        }

        // Reset the 6-second dismiss timer smoothly so the user has time to view the progress update
        _dismissTimer.Stop();
        _dismissTimer.Start();
    }

    private void SetIcon(ImageSource? icon)
    {
        if (icon is not null)
        {
            IconImage.Source = icon;
            IconImage.Visibility = Visibility.Visible;
            IconGlyph.Visibility = Visibility.Collapsed;
        }
        else
        {
            IconImage.Source = null;
            IconImage.Visibility = Visibility.Collapsed;
            IconGlyph.Visibility = Visibility.Visible;
        }
    }

    public static void Show(AchievementUnlockNotification notification, ImageSource? icon, ToastPresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(notification);
        var item = new ToastDisplayItem(
            notification.AppId,
            notification.AchievementId,
            Header: "ACHIEVEMENT UNLOCKED",
            Title: notification.AchievementName,
            Description: string.IsNullOrWhiteSpace(notification.Description) ? "Achievement progress updated." : notification.Description.Trim(),
            Footer: notification.GameTitle,
            Glyph: "✦",
            TrophyType: notification.TrophyType,
            ProgressCurrent: notification.ProgressCurrent,
            ProgressMax: notification.ProgressMax,
            Icon: icon,
            Presentation: presentation);

        EnqueueToast(item);
    }

    public static void ShowPlaytimeToast(
        string appId,
        string header,
        string gameTitle,
        string description,
        string? footer,
        ImageSource? artwork,
        ToastPresentation presentation)
    {
        var glyph = header.Contains("SAVED", StringComparison.OrdinalIgnoreCase) ? "⏱" : "▶";
        var item = new ToastDisplayItem(
            appId,
            header + "_" + appId,
            Header: header,
            Title: gameTitle,
            Description: description,
            Footer: footer ?? "AchievementSync",
            Glyph: glyph,
            TrophyType: null,
            ProgressCurrent: null,
            ProgressMax: null,
            Icon: artwork,
            Presentation: presentation);

        EnqueueToast(item);
    }

    private static void EnqueueToast(ToastDisplayItem item)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted) return;
        if (!dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(new Action(() => EnqueueToast(item)));
            return;
        }

        lock (QueueGate)
        {
            if (ActiveToast is not null
                && string.Equals(ActiveToast._appId, item.AppId, StringComparison.Ordinal)
                && string.Equals(ActiveToast._achievementId, item.KeyId, StringComparison.Ordinal))
            {
                ActiveToast.UpdateProgress(item);
                return;
            }

            var alreadyQueued = Pending.Any(p =>
                string.Equals(p.AppId, item.AppId, StringComparison.Ordinal)
                && string.Equals(p.KeyId, item.KeyId, StringComparison.Ordinal));
            var alreadyShown = ActiveToast is not null
                && string.Equals(ActiveToast._appId, item.AppId, StringComparison.Ordinal)
                && string.Equals(ActiveToast._achievementId, item.KeyId, StringComparison.Ordinal);
            if (alreadyQueued || alreadyShown) return;

            if (Pending.Count >= 8) Pending.TryDequeue(out _); // drop the oldest when the queue is full
            Pending.Enqueue(item);
            if (ActiveToast is null) PresentNext();
        }
    }

    public static int PendingCount
    {
        get { lock (QueueGate) return Pending.Count; }
    }

    private static void PresentNext()
    {
        if (ActiveToast is not null) return;
        if (!Pending.TryDequeue(out var request)) return;
        ActiveToast = new AchievementToastWindow(request);
        ActiveToast.Show();
    }

    private Rect GetActiveScreenWorkArea(bool autoDetect)
    {
        if (autoDetect)
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

                            if (width > 200 && height > 200)
                            {
                                return new Rect(left, top, width, height);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Warning($"Active game screen detection failed, falling back to primary screen: {ex.Message}");
            }
        }
        return SystemParameters.WorkArea;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var presentation = (ToastPresentation?)Tag ?? ToastPresentation.Default;
        _activeWorkArea = GetActiveScreenWorkArea(presentation.AutoDetectActiveScreen);

        if (presentation.CompactMode || _activeWorkArea.Height <= 800)
        {
            RootBorder.LayoutTransform = new ScaleTransform(0.84, 0.84);
            Width = 400;
            UpdateLayout();
        }

        var (anchorLeft, anchorTop) = NotificationPlacement.Compute(
            presentation.Position, ActualWidth, ActualHeight, presentation.OffsetX, presentation.OffsetY,
            _activeWorkArea.Left, _activeWorkArea.Top, _activeWorkArea.Width, _activeWorkArea.Height);

        bool animationsEnabled = SystemParameters.ClientAreaAnimation && !SystemParameters.IsRemoteSession;
        if (animationsEnabled)
        {
            Top = anchorTop - ActualHeight - EntranceSlideDistance;
            Left = anchorLeft;
            Opacity = 0;

            var slide = new DoubleAnimation(Top, anchorTop, new Duration(TimeSpan.FromMilliseconds(EntranceDurationMs)))
            {
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.28 },
                FillBehavior = FillBehavior.HoldEnd
            };
            var fade = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(350)));
            BeginAnimation(TopProperty, slide);
            BeginAnimation(OpacityProperty, fade);
        }
        else
        {
            Top = anchorTop;
            Left = anchorLeft;
            Opacity = 1;
        }

        UnlockSoundPlayer.TryPlay(presentation.SoundEnabled, presentation.SoundVolume, presentation.SoundKind, presentation.SoundPath);
        AppLog.Info("A local achievement unlock notification was displayed.");
        _dismissTimer.Interval = TimeSpan.FromSeconds(6);
        _dismissTimer.Start();
    }

    private void Dismiss()
    {
        _dismissTimer.Stop();
        bool animationsEnabled = SystemParameters.ClientAreaAnimation && !SystemParameters.IsRemoteSession;
        if (animationsEnabled)
        {
            var targetTop = _activeWorkArea.Width > 0 ? _activeWorkArea.Top : SystemParameters.WorkArea.Top;
            var slideUp = new DoubleAnimation(Top, Math.Max(targetTop - ActualHeight, Top - 36), new Duration(TimeSpan.FromMilliseconds(ExitDurationMs)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
                FillBehavior = FillBehavior.HoldEnd
            };
            var fadeOut = new DoubleAnimation(Opacity, 0, new Duration(TimeSpan.FromMilliseconds(ExitDurationMs)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
                FillBehavior = FillBehavior.HoldEnd
            };
            fadeOut.Completed += (_, _) => Close();
            BeginAnimation(TopProperty, slideUp);
            BeginAnimation(OpacityProperty, fadeOut);
        }
        else
        {
            Close();
        }
    }

    private void Toast_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => Dismiss();

    private void OnClosed(object? sender, EventArgs e)
    {
        _dismissTimer.Stop();
        lock (QueueGate)
        {
            if (ReferenceEquals(ActiveToast, this)) ActiveToast = null;
            if (Pending.IsEmpty) return;
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            lock (QueueGate) PresentNext();
        };
        timer.Start();
    }
}
