using AchievementSync.Core.Tracking;
using AchievementSync.Desktop.Services;
using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Application = System.Windows.Application;

namespace AchievementSync.Desktop;

/// <summary>Controls how a toast is placed and whether it makes a sound. Immutable so it can be safely shared with queued toasts.</summary>
public sealed record ToastPresentation(string Position, int OffsetX, int OffsetY, bool SoundEnabled, int SoundVolume, string SoundKind = "AchievementSync", string SoundPath = "")
{
    public static ToastPresentation Default { get; } = new("TopCenter", 0, 0, SoundEnabled: true, SoundVolume: 70);
}

public partial class AchievementToastWindow : Window
{
    private const double EntranceDurationMs = 520;
    private const double ExitDurationMs = 300;
    private const double EntranceSlideDistance = 44;

    private static readonly object QueueGate = new();
    private static readonly ConcurrentQueue<(AchievementUnlockNotification Notification, ImageSource? Icon, ToastPresentation Presentation)> Pending = new();
    private static AchievementToastWindow? ActiveToast;

    private readonly string _appId;
    private readonly string _achievementId;
    private readonly DispatcherTimer _dismissTimer;

    private AchievementToastWindow(AchievementUnlockNotification notification, ImageSource? icon, ToastPresentation presentation)
    {
        InitializeComponent();
        Tag = presentation;
        _appId = notification.AppId;
        _achievementId = notification.AchievementId;

        AchievementTitleText.Text = notification.AchievementName;
        GameNameText.Text = notification.GameTitle;
        var description = notification.Description?.Trim();
        DescriptionText.Text = string.IsNullOrWhiteSpace(description) ? "Achievement progress updated." : description;
        SetIcon(icon);

        _dismissTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _dismissTimer.Tick += (_, _) => Dismiss();
        Loaded += OnLoaded;
        Closed += OnClosed;
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
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted) return;
        if (!dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(new Action(() => Show(notification, icon, presentation)));
            return;
        }

        lock (QueueGate)
        {
            var alreadyQueued = Pending.Any(item =>
                string.Equals(item.Notification.AppId, notification.AppId, StringComparison.Ordinal)
                && string.Equals(item.Notification.AchievementId, notification.AchievementId, StringComparison.Ordinal));
            var alreadyShown = ActiveToast is not null
                && string.Equals(ActiveToast._appId, notification.AppId, StringComparison.Ordinal)
                && string.Equals(ActiveToast._achievementId, notification.AchievementId, StringComparison.Ordinal);
            if (alreadyQueued || alreadyShown) return;

            if (Pending.Count >= 8) Pending.TryDequeue(out _); // drop the oldest when the queue is full
            Pending.Enqueue((notification, icon, presentation));
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
        ActiveToast = new AchievementToastWindow(request.Notification, request.Icon, request.Presentation);
        ActiveToast.Show();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var presentation = (ToastPresentation?)Tag ?? ToastPresentation.Default;
        var work = SystemParameters.WorkArea;
        var (anchorLeft, anchorTop) = NotificationPlacement.Compute(
            presentation.Position, ActualWidth, ActualHeight, presentation.OffsetX, presentation.OffsetY,
            work.Left, work.Top, work.Width, work.Height);
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

        UnlockSoundPlayer.TryPlay(presentation.SoundEnabled, presentation.SoundVolume, presentation.SoundKind, presentation.SoundPath);
        AppLog.Info("A local achievement unlock notification was displayed.");
        _dismissTimer.Start();
    }

    private void Dismiss()
    {
        _dismissTimer.Stop();
        var slideUp = new DoubleAnimation(Top, Math.Max(SystemParameters.WorkArea.Top - ActualHeight, Top - 36), new Duration(TimeSpan.FromMilliseconds(ExitDurationMs)))
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
