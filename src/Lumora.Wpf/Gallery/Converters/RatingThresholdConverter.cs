using System;
using System.Globalization;
using System.Windows.Data;

namespace Lumora.Gallery.Converters;

/// <summary>true, wenn die Bewertung (byte?) mindestens den Schwellwert aus ConverterParameter erreicht (Stern „gefüllt“).</summary>
public sealed class RatingThresholdConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (!int.TryParse(parameter?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var threshold))
        {
            return false;
        }

        return value switch
        {
            byte b => b >= threshold,
            int i => i >= threshold,
            _ => false,
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
