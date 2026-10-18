using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PhotoViewerV3.Gallery.Converters;

/// <summary>Nicht-leerer String → Visible, sonst Collapsed (z. B. Fehlermeldungen).</summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
