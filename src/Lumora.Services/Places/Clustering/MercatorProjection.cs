namespace Lumora.Services.Places.Clustering;

/// <summary>
/// Web-Mercator (EPSG:3857) in Einheitskoordinaten: die ganze Welt ist das Quadrat [0, 1] × [0, 1], x nach Osten,
/// y nach Süden (wie die Kachelnummern von OpenStreetMap). Bei Zoomstufe z hat die Welt 256 · 2^z Pixel Kantenlänge.
/// </summary>
public static class MercatorProjection
{
    /// <summary>Breitengrad, bei dem die Mercator-Karte endet (Quadrat).</summary>
    public const double MaxLatitude = 85.05112878;

    /// <summary>Kantenlänge einer Kachel in Pixeln.</summary>
    public const int TileSize = 256;

    public static double LongitudeToX(double longitude) => Math.Clamp((longitude + 180.0) / 360.0, 0.0, 1.0);

    public static double LatitudeToY(double latitude)
    {
        var lat = Math.Clamp(latitude, -MaxLatitude, MaxLatitude);
        var sin = Math.Sin(lat * Math.PI / 180.0);
        var y = 0.5 - (Math.Log((1 + sin) / (1 - sin)) / (4 * Math.PI));
        return Math.Clamp(y, 0.0, 1.0);
    }

    public static double XToLongitude(double x) => (x * 360.0) - 180.0;

    public static double YToLatitude(double y)
    {
        var n = Math.PI - (2.0 * Math.PI * y);
        return 180.0 / Math.PI * Math.Atan(Math.Sinh(n));
    }

    /// <summary>Pixel-Kantenlänge der Welt bei Zoomstufe <paramref name="zoom"/> (auch gebrochen).</summary>
    public static double WorldSize(double zoom) => TileSize * Math.Pow(2, zoom);
}
