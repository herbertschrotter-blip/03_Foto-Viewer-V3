using System;
using System.Globalization;
using System.Windows.Data;

namespace Lumora.Gallery.Converters;

/// <summary>Negiert einen bool (z. B. IsEnabled = !IsOperationRunning).</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;
}
