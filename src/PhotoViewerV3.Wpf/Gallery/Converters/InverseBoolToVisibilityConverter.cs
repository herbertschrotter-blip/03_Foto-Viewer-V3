using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PhotoViewerV3.Gallery.Converters;

/// <summary>false → Visible, true → Collapsed.</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Collapsed;
}
