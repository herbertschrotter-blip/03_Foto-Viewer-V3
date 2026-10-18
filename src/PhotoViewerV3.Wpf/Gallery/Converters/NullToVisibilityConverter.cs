using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PhotoViewerV3.Gallery.Converters;

/// <summary>
/// null → Visible, sonst Collapsed (Platzhalter, solange kein Thumbnail da ist).
/// Mit <see cref="Invert"/> = true umgekehrt.
/// </summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isNull = value is null;
        return isNull != Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
