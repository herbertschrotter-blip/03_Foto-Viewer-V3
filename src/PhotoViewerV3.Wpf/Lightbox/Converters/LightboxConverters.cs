using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PhotoViewerV3.Lightbox.Converters;

/// <summary>Bool → Visibility; ConverterParameter „Invert“ kehrt die Logik um.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (IsInvert(parameter))
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value is Visibility.Visible;
        return IsInvert(parameter) ? !visible : visible;
    }

    private static bool IsInvert(object? parameter)
        => parameter is string s && s.Equals("Invert", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Nicht-leerer String → Visible, sonst Collapsed.</summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Bewertung (byte?) → bool: true, wenn die Bewertung mindestens dem Stern aus dem
/// ConverterParameter (1–5) entspricht. Für die „gefüllt“-Darstellung der Sterne.
/// </summary>
public sealed class RatingAtLeastConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var rating = value switch
        {
            byte b => b,
            int i => i,
            _ => 0,
        };
        var threshold = parameter switch
        {
            string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
            int i => i,
            _ => 1,
        };
        return rating >= threshold;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
