using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace AchievementSync.Desktop.Converters;

/// <summary>Converts a boolean to a green brush when true, muted gray when false.</summary>
public sealed class BoolToGreenBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b)
            return b ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x69, 0xD9, 0xB3)) : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x77, 0x84, 0x9A));
        return new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x77, 0x84, 0x9A));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
