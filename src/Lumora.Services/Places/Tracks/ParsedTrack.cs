using Lumora.Domain.Enums;

namespace Lumora.Services.Places.Tracks;

/// <summary>Eine gelesene Aktivität: Punkte (nach Zeit sortiert, nur mit Position), Sportart, Gerät, Name, Format.</summary>
public sealed record ParsedTrack(IReadOnlyList<TrackPoint> Points, string? Sport, string? Device, string? Name, GpsTrackSource Source)
{
    /// <summary>Punkte ordnen, Ungültiges (0/0, außerhalb des Wertebereichs) und doppelte Zeitpunkte entfernen.</summary>
    public static IReadOnlyList<TrackPoint> Normalize(IEnumerable<TrackPoint> points)
    {
        var list = points
            .Where(p => double.IsFinite(p.Latitude) && double.IsFinite(p.Longitude)
                        && p.Latitude is >= -90 and <= 90 && p.Longitude is >= -180 and <= 180
                        && !(p.Latitude == 0 && p.Longitude == 0))
            .OrderBy(p => p.Utc)
            .ToList();
        var result = new List<TrackPoint>(list.Count);
        foreach (var p in list)
        {
            if (result.Count > 0 && result[^1].Utc == p.Utc)
            {
                continue; // gleicher Zeitpunkt: der erste gilt
            }

            result.Add(p);
        }

        return result.Count > TrackCodec.MaxPoints ? result.Take(TrackCodec.MaxPoints).ToList() : result;
    }
}

/// <summary>Sportarten in Worten (FIT-Profil <c>sport</c>, GPX/TCX-Texte).</summary>
public static class TrackSportNames
{
    private static readonly Dictionary<int, string> FitSports = new()
    {
        [0] = "Aktivität",
        [1] = "Laufen",
        [2] = "Radfahren",
        [4] = "Fitnessgerät",
        [5] = "Schwimmen",
        [10] = "Training",
        [11] = "Gehen",
        [12] = "Skilanglauf",
        [13] = "Ski alpin",
        [14] = "Snowboard",
        [15] = "Rudern",
        [16] = "Bergsteigen",
        [17] = "Wandern",
        [18] = "Multisport",
        [19] = "Paddeln",
        [21] = "E-Bike",
        [22] = "Motorrad",
        [23] = "Boot",
        [24] = "Autofahrt",
        [25] = "Golf",
        [27] = "Reiten",
        [30] = "Inline-Skaten",
        [31] = "Klettern",
        [32] = "Segeln",
        [33] = "Eislaufen",
        [35] = "Schneeschuhwandern",
        [37] = "Stand-up-Paddling",
        [38] = "Surfen",
        [41] = "Kajak",
    };

    private static readonly Dictionary<string, string> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        ["running"] = "Laufen",
        ["run"] = "Laufen",
        ["biking"] = "Radfahren",
        ["cycling"] = "Radfahren",
        ["ride"] = "Radfahren",
        ["walking"] = "Gehen",
        ["walk"] = "Gehen",
        ["hiking"] = "Wandern",
        ["hike"] = "Wandern",
        ["swimming"] = "Schwimmen",
        ["mountaineering"] = "Bergsteigen",
        ["alpine_skiing"] = "Ski alpin",
        ["cross_country_skiing"] = "Skilanglauf",
        ["other"] = "Aktivität",
    };

    /// <summary>Name zur FIT-Sportart; null bei 255 (ungültig).</summary>
    public static string? FromFit(int sport) => sport is < 0 or >= 255 ? null : FitSports.TryGetValue(sport, out var name) ? name : "Aktivität";

    /// <summary>Text aus GPX <c>type</c> bzw. TCX <c>Sport</c> („Running“, „hiking“).</summary>
    public static string? FromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var t = text.Trim();
        return Words.TryGetValue(t, out var name) ? name : t.Length > 40 ? t[..40] : t;
    }
}
