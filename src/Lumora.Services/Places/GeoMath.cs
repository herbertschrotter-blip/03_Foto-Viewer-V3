namespace Lumora.Services.Places;

/// <summary>Kleine Geo-Rechnungen auf der Kugel (WGS84 genähert, Erdradius 6 371 km) für ADR-0062.</summary>
public static class GeoMath
{
    public const double EarthRadiusMeters = 6_371_000;

    /// <summary>Großkreis-Abstand in Metern (Haversine).</summary>
    public static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        var p1 = lat1 * Math.PI / 180;
        var p2 = lat2 * Math.PI / 180;
        var dp = (lat2 - lat1) * Math.PI / 180;
        var dl = (lon2 - lon1) * Math.PI / 180;
        var a = (Math.Sin(dp / 2) * Math.Sin(dp / 2)) + (Math.Cos(p1) * Math.Cos(p2) * Math.Sin(dl / 2) * Math.Sin(dl / 2));
        return 2 * EarthRadiusMeters * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    /// <summary>Linear zwischen zwei Punkten (für kurze Abstände ausreichend; Längen über die Datumsgrenze richtig).</summary>
    public static (double Latitude, double Longitude) Interpolate(double lat1, double lon1, double lat2, double lon2, double fraction)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        var dl = lon2 - lon1;
        if (dl > 180)
        {
            dl -= 360;
        }
        else if (dl < -180)
        {
            dl += 360;
        }

        var lon = lon1 + (dl * fraction);
        if (lon > 180)
        {
            lon -= 360;
        }
        else if (lon < -180)
        {
            lon += 360;
        }

        return (lat1 + ((lat2 - lat1) * fraction), lon);
    }

    /// <summary>Geschwindigkeit in km/h für einen Abstand in Metern über eine Zeit in Sekunden (mindestens 1 s).</summary>
    public static double SpeedKmh(double meters, double seconds) => meters / Math.Max(1, Math.Abs(seconds)) * 3.6;

    /// <summary>Himmelsrichtung von Punkt 1 nach Punkt 2 auf Deutsch („westlich“, „nordöstlich“ …).</summary>
    public static string DirectionText(double lat1, double lon1, double lat2, double lon2)
    {
        var p1 = lat1 * Math.PI / 180;
        var p2 = lat2 * Math.PI / 180;
        var dl = (lon2 - lon1) * Math.PI / 180;
        var y = Math.Sin(dl) * Math.Cos(p2);
        var x = (Math.Cos(p1) * Math.Sin(p2)) - (Math.Sin(p1) * Math.Cos(p2) * Math.Cos(dl));
        var bearing = (Math.Atan2(y, x) * 180 / Math.PI + 360) % 360;
        string[] names = ["nördlich", "nordöstlich", "östlich", "südöstlich", "südlich", "südwestlich", "westlich", "nordwestlich"];
        return names[(int)Math.Round(bearing / 45) % 8];
    }

    /// <summary>„850 m“, „4,8 km“, „12 km“ (deutsche Schreibweise).</summary>
    public static string FormatDistance(double meters)
    {
        var de = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
        if (meters < 1000)
        {
            return $"{Math.Round(meters / 10) * 10:0} m";
        }

        return meters < 10_000 ? (meters / 1000).ToString("0.0", de) + " km" : (meters / 1000).ToString("#,##0", de) + " km";
    }
}
