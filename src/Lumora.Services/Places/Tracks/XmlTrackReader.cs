using System.Globalization;
using System.Xml;
using Lumora.Domain.Enums;

namespace Lumora.Services.Places.Tracks;

/// <summary>
/// GPX- und TCX-Leser (ADR-0062) als streamender <see cref="XmlReader"/> ohne DTD und ohne externe Auflösung: GPX
/// <c>trk</c>/<c>trkseg</c>/<c>trkpt</c> (lat/lon, <c>ele</c>, <c>time</c>; je <c>trk</c> eine Aktivität, Name und
/// <c>type</c> als Sportart), TCX <c>Activity</c> (Sport-Attribut, <c>Creator/Name</c> als Gerät) mit
/// <c>Trackpoint</c>/<c>Time</c>/<c>Position</c>/<c>AltitudeMeters</c>. Punkte ohne Zeit zählen nicht (Routen, Wegpunkte).
/// </summary>
public static class XmlTrackReader
{
    private static XmlReaderSettings Settings => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreWhitespace = true,
        CloseInput = false,
    };

    /// <summary>Aktivitäten aus einer GPX-Datei (nur solche mit zeitgestempelten Punkten).</summary>
    public static IReadOnlyList<ParsedTrack> ReadGpx(Stream stream)
    {
        var result = new List<ParsedTrack>();
        using var reader = XmlReader.Create(stream, Settings);
        string? creator = null;
        string? name = null;
        string? type = null;
        List<TrackPoint>? points = null;
        TrackPoint? pending = null;
        string? field = null;
        var depthInTrk = -1;
        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    switch (reader.LocalName)
                    {
                        case "gpx":
                            creator = reader.GetAttribute("creator");
                            break;
                        case "trk":
                            points = new List<TrackPoint>();
                            name = null;
                            type = null;
                            depthInTrk = reader.Depth;
                            break;
                        case "trkpt" when points is not null:
                            pending = ParseLatLon(reader.GetAttribute("lat"), reader.GetAttribute("lon")) is { } ll
                                ? new TrackPoint(long.MinValue, ll.Lat, ll.Lon)
                                : null;
                            if (reader.IsEmptyElement)
                            {
                                pending = null; // ohne Zeit: zählt nicht
                            }

                            break;
                        case "ele" or "time" when pending is not null:
                            field = reader.LocalName;
                            break;
                        case "name" when points is not null && pending is null && reader.Depth == depthInTrk + 1:
                            field = "trkname";
                            break;
                        case "type" when points is not null && pending is null && reader.Depth == depthInTrk + 1:
                            field = "trktype";
                            break;
                    }

                    break;
                case XmlNodeType.Text or XmlNodeType.CDATA when field is not null:
                    var text = reader.Value.Trim();
                    switch (field)
                    {
                        case "ele" when pending is { } p && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var ele):
                            pending = p with { Altitude = ele };
                            break;
                        case "time" when pending is { } p2 && ParseTime(text) is { } t:
                            pending = p2 with { Utc = t };
                            break;
                        case "trkname":
                            name = text.Length > 80 ? text[..80] : text;
                            break;
                        case "trktype":
                            type = text;
                            break;
                    }

                    break;
                case XmlNodeType.EndElement:
                    field = null;
                    if (reader.LocalName == "trkpt" && pending is { } done && points is not null)
                    {
                        if (done.Utc != long.MinValue)
                        {
                            points.Add(done);
                        }

                        pending = null;
                    }
                    else if (reader.LocalName == "trk" && points is not null)
                    {
                        var normalized = ParsedTrack.Normalize(points);
                        if (normalized.Count > 0)
                        {
                            result.Add(new ParsedTrack(normalized, TrackSportNames.FromText(type), CreatorDevice(creator), string.IsNullOrWhiteSpace(name) ? null : name, GpsTrackSource.Gpx));
                        }

                        points = null;
                    }

                    break;
            }
        }

        return result;
    }

    /// <summary>Aktivitäten aus einer TCX-Datei.</summary>
    public static IReadOnlyList<ParsedTrack> ReadTcx(Stream stream)
    {
        var result = new List<ParsedTrack>();
        using var reader = XmlReader.Create(stream, Settings);
        string? sport = null;
        string? device = null;
        List<TrackPoint>? points = null;
        long? time = null;
        double? lat = null, lon = null, alt = null;
        var inPoint = false;
        var inCreator = false;
        string? field = null;
        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    switch (reader.LocalName)
                    {
                        case "Activity":
                            sport = reader.GetAttribute("Sport");
                            device = null;
                            points = new List<TrackPoint>();
                            break;
                        case "Creator" when points is not null:
                            inCreator = true;
                            break;
                        case "Name" when inCreator:
                            field = "creator";
                            break;
                        case "Trackpoint" when points is not null:
                            inPoint = true;
                            time = null;
                            lat = lon = alt = null;
                            break;
                        case "Time" or "LatitudeDegrees" or "LongitudeDegrees" or "AltitudeMeters" when inPoint:
                            field = reader.LocalName;
                            break;
                    }

                    break;
                case XmlNodeType.Text when field is not null:
                    var text = reader.Value.Trim();
                    var number = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : (double?)null;
                    switch (field)
                    {
                        case "creator":
                            device = text.Length > 64 ? text[..64] : text;
                            break;
                        case "Time":
                            time = ParseTime(text);
                            break;
                        case "LatitudeDegrees":
                            lat = number;
                            break;
                        case "LongitudeDegrees":
                            lon = number;
                            break;
                        case "AltitudeMeters":
                            alt = number;
                            break;
                    }

                    break;
                case XmlNodeType.EndElement:
                    field = null;
                    switch (reader.LocalName)
                    {
                        case "Creator":
                            inCreator = false;
                            break;
                        case "Trackpoint" when points is not null:
                            if (time is { } t && lat is { } la && lon is { } lo)
                            {
                                points.Add(new TrackPoint(t, la, lo, alt));
                            }

                            inPoint = false;
                            break;
                        case "Activity" when points is not null:
                            var normalized = ParsedTrack.Normalize(points);
                            if (normalized.Count > 0)
                            {
                                result.Add(new ParsedTrack(normalized, TrackSportNames.FromText(sport), device, null, GpsTrackSource.Tcx));
                            }

                            points = null;
                            break;
                    }

                    break;
            }
        }

        return result;
    }

    /// <summary>ISO 8601 mit Zone („2020-03-15T13:56:10Z“, „…+01:00“); ohne Zone als UTC.</summary>
    public static long? ParseTime(string text)
    {
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value))
        {
            return value.ToUnixTimeSeconds();
        }

        return null;
    }

    private static (double Lat, double Lon)? ParseLatLon(string? lat, string? lon)
        => double.TryParse(lat, NumberStyles.Float, CultureInfo.InvariantCulture, out var la)
           && double.TryParse(lon, NumberStyles.Float, CultureInfo.InvariantCulture, out var lo)
            ? (la, lo)
            : null;

    /// <summary>Gerät aus <c>gpx/@creator</c>, wenn es eine Garmin-Angabe ist („Garmin Connect“ zählt nicht).</summary>
    private static string? CreatorDevice(string? creator)
    {
        if (string.IsNullOrWhiteSpace(creator))
        {
            return null;
        }

        var c = creator.Trim();
        return c.Contains("Connect", StringComparison.OrdinalIgnoreCase) || c.Length > 64 ? null : c;
    }
}
