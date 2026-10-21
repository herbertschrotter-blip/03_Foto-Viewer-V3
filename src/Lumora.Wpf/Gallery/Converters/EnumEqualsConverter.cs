using System;
using System.Globalization;
using System.Windows.Data;

namespace Lumora.Gallery.Converters;

/// <summary>
/// Bindet RadioButtons an eine Enum-Eigenschaft: IsChecked = (Wert == ConverterParameter).
/// ConvertBack setzt beim Anhaken den Enum-Wert, beim Abhaken passiert nichts.
/// </summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is not string name)
        {
            return false;
        }

        return string.Equals(value.ToString(), name, StringComparison.OrdinalIgnoreCase);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not true || parameter is not string name)
        {
            return Binding.DoNothing;
        }

        var enumType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        return Enum.TryParse(enumType, name, ignoreCase: true, out var result) && result is not null
            ? result
            : Binding.DoNothing;
    }
}
