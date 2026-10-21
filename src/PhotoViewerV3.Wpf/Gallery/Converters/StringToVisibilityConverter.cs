using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PhotoViewerV3.Gallery.Converters;

/// <summary>Nicht-leerer String → Visible, sonst Collapsed (z. B. Fehlermeldungen); Parameter „Invert“ kehrt um (0.2.43).</summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.IsNullOrWhiteSpace(value as string) != IsInvert(parameter) ? Visibility.Collapsed : Visibility.Visible;

    private static bool IsInvert(object? parameter) => string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
