using System;
using System.Globalization;
using System.Windows.Data;

namespace PhotoViewerV3.Gallery.ListView;

/// <summary>UTC-Zeitstempel → lokale Anzeige „dd.MM.yyyy HH:mm“ (Spalte „Geändert“).</summary>
public sealed class UtcToLocalTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is DateTime utc ? utc.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Spalte „Typ“: Video/Bild aus <c>IsVideo</c>.</summary>
public sealed class MediaTypeTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "Video" : "Bild";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Spalte „Status“ — Platzhalter: zeigt bislang nur „fehlt“ (Missing) oder nichts.
/// TODO (Agent A / Integration): Eingangs-/Archiv-Status der Datei aus dem Bibliotheksmodul anzeigen.
/// </summary>
public sealed class MissingToStatusTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "fehlt" : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
