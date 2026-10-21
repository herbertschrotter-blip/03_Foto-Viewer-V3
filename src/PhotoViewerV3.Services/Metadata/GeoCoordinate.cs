using System.Globalization;
using PhotoViewerV3.Domain.Enums;

namespace PhotoViewerV3.Services.Metadata;

/// <summary>Koordinate WGS84 in Dezimalgrad, optional mit Höhe in Metern (ADR-0050).</summary>
public readonly record struct GeoCoordinate(double Latitude, double Longitude, double? Altitude = null)
{
    /// <summary>
    /// Gültig: endlich, im Wertebereich, und nicht genau (0, 0) — viele Kameras schreiben „0/1“ ohne GPS-Empfang, und
    /// echte Fotos auf „Null Island“ gibt es praktisch nicht.
    /// </summary>
    public bool IsValid
        => double.IsFinite(Latitude) && double.IsFinite(Longitude)
           && Latitude is >= -90 and <= 90 && Longitude is >= -180 and <= 180
           && !(Latitude == 0 && Longitude == 0);

    /// <summary>Anzeige „48,2082° N, 16,3738° O“ (4 Nachkommastellen ≈ 11 m, deutsche Himmelsrichtungen).</summary>
    public string DisplayText => Format(Latitude, Longitude);

    /// <summary>„48,2082° N, 16,3738° O“ in der deutschen Schreibweise (Komma als Dezimaltrenner).</summary>
    public static string Format(double latitude, double longitude)
    {
        var de = CultureInfo.GetCultureInfo("de-DE");
        var lat = Math.Abs(latitude).ToString("0.0000", de) + "° " + (latitude < 0 ? "S" : "N");
        var lon = Math.Abs(longitude).ToString("0.0000", de) + "° " + (longitude < 0 ? "W" : "O");
        return $"{lat}, {lon}";
    }
}

/// <summary>Gelesene Koordinate samt Herkunft (EXIF/XMP bzw. Video).</summary>
public readonly record struct GeoReading(GeoCoordinate Coordinate, GeoSource Source);
