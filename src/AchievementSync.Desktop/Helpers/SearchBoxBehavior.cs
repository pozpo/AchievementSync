using System.Windows;
using System.Windows.Input;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace AchievementSync.Desktop.Helpers;

/// <summary>
/// Presentation-only helpers for the redesigned search field.
/// <list type="bullet">
/// <item><see cref="ClearCommand"/> powers the "✕" button inside <c>Modern.SearchBox</c>. It only calls
/// <see cref="System.Windows.Controls.TextBox.Clear"/>, so the existing two-way <c>SearchText</c> binding
/// (and the view-model's own filtering) does all of the real work.</item>
/// <item><see cref="FocusSearchCommand"/> is bound to Ctrl+F on the window and moves keyboard focus to the
/// text box marked with <c>helpers:SearchBoxBehavior.IsFocusTarget="True"</c>.</item>
/// </list>
/// Nothing here touches view-models, services, settings or any business logic.
/// </summary>
public static class SearchBoxBehavior
{
    public static readonly RoutedUICommand ClearCommand =
        new("Clear search", nameof(ClearCommand), typeof(SearchBoxBehavior));

    public static readonly RoutedUICommand FocusSearchCommand =
        new("Focus search", nameof(FocusSearchCommand), typeof(SearchBoxBehavior));

    public static readonly DependencyProperty IsFocusTargetProperty =
        DependencyProperty.RegisterAttached(
            "IsFocusTarget",
            typeof(bool),
            typeof(SearchBoxBehavior),
            new PropertyMetadata(false, OnIsFocusTargetChanged));

    private static WeakReference<WpfTextBox>? _focusTarget;

    static SearchBoxBehavior()
    {
        CommandManager.RegisterClassCommandBinding(
            typeof(WpfTextBox),
            new CommandBinding(ClearCommand, OnClearExecuted, OnClearCanExecute));

        CommandManager.RegisterClassCommandBinding(
            typeof(Window),
            new CommandBinding(FocusSearchCommand, OnFocusSearchExecuted, OnFocusSearchCanExecute));
    }

    public static bool GetIsFocusTarget(DependencyObject element) =>
        (bool)element.GetValue(IsFocusTargetProperty);

    public static void SetIsFocusTarget(DependencyObject element, bool value) =>
        element.SetValue(IsFocusTargetProperty, value);

    private static void OnIsFocusTargetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WpfTextBox box && e.NewValue is true)
        {
            _focusTarget = new WeakReference<WpfTextBox>(box);
        }
    }

    private static void OnClearCanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = sender is WpfTextBox box && !box.IsReadOnly && box.Text.Length > 0;
        e.Handled = true;
    }

    private static void OnClearExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (sender is not WpfTextBox box)
        {
            return;
        }

        box.Clear();
        Keyboard.Focus(box);
        e.Handled = true;
    }

    private static bool TryGetVisibleTarget(out WpfTextBox box)
    {
        box = null!;
        if (_focusTarget is { } weak && weak.TryGetTarget(out var target) && target.IsVisible)
        {
            box = target;
            return true;
        }

        return false;
    }

    private static void OnFocusSearchCanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = TryGetVisibleTarget(out _);
        e.Handled = true;
    }

    private static void OnFocusSearchExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (!TryGetVisibleTarget(out var box))
        {
            return;
        }

        Keyboard.Focus(box);
        box.SelectAll();
        e.Handled = true;
    }
}
