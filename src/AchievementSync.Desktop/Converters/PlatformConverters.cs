using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AchievementSync.Desktop.Converters;

public sealed class PlatformIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var platform = value as string ?? string.Empty;
        return platform.ToLowerInvariant() switch
        {
            "steam" => "⛭",
            "xbox" => "✘",
            "playstation" => "⧆",
            "xbox360" => "⬢",
            "gfwl" => "⟲",
            "epic" => "⬡",
            "gog" => "◆",
            "uplay" => "⬟",
            "retroachievements" => "⬠",
            _ => "◇"
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return !string.IsNullOrWhiteSpace(value as string) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class BoolToConfiguredTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is true ? "Configured" : "Not configured";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
