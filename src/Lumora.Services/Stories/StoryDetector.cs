using System.Globalization;
using Lumora.Domain.Enums;

namespace Lumora.Services.Stories;

/// <summary>
/// Reine Erkennungslogik der Storys (ADR-0058) — ohne Datenbank, testbar mit synthetischen Zeitachsen.
/// <list type="bullet">
/// <item><b>Ereignis</b>: ein Durchlauf über die nach Zeit sortierten Dateien; neues Ereignis bei einer Pause über
/// <see cref="StoryDetectionOptions.Pause"/> oder einem Ortswechsel über <see cref="StoryDetectionOptions.SplitDistanceKm"/>
/// (gegenüber dem zuletzt verorteten Foto). Dateien mit ungefährem Datum (nur Monat/Jahr) bilden eigene Gruppen
/// („Juli 2025“, „1987“) statt sich in die Mitte eines echten Ereignisses zu mischen.</item>
/// <item><b>Reise</b>: aufeinanderfolgende Ereignisse weg von zu Hause (Schwerpunkt weiter als
/// <see cref="StoryDetectionOptions.HomeRadiusKm"/>), höchstens <see cref="StoryDetectionOptions.TripGap"/> auseinander,
/// über mindestens zwei Kalendertage; Ereignisse ohne Ortsangabe dazwischen gehören dazu, eines zu Hause beendet die Reise.
/// Ein einzelner Tag weg bleibt ein Ereignis (<see cref="DetectedStory.IsAway"/> = „Ausflug“).</item>
/// <item><b>Person</b>: Jahre mit mindestens <see cref="StoryDetectionOptions.MinPersonFiles"/> Fotos mit bestätigtem
/// Gesicht; benachbarte schwächere Jahre werden zu einer Spanne zusammengezählt, bis sie die Schwelle erreichen.</item>
/// <item><b>Ort</b>: Orte mit Besuchen in mindestens <see cref="StoryDetectionOptions.MinPlaceYears"/> verschiedenen
/// Jahren, nicht zu Hause.</item>
/// </list>
/// Die Mindestgröße gilt für Ereignisse, Reisen und Orte-Storys. Signaturen: „E:&lt;Start&gt;“, „T:&lt;erster Tag&gt;-&lt;letzter Tag&gt;“,
/// „A:M:yyyy-MM“ / „A:Y:yyyy“, „P:&lt;Person&gt;:&lt;Jahr&gt;-&lt;Jahr&gt;“, „O:&lt;Ort&gt;“.
/// </summary>
public static class StoryDetector
{
    private const double EarthRadiusKm = 6371.0088;

    /// <summary>Visit-Grenze der Orte-Storys: mehr als so viele Tage ohne Foto am Ort = neuer Besuch.</summary>
    private const int VisitGapDays = 2;

    /// <summary>Ereignisse, Reisen und Gruppen ungefährer Daten aus einer nach (Zeit, FileId) sortierten Zeitachse.</summary>
    public static List<DetectedStory> DetectEvents(IReadOnlyList<StoryTimelineItem> sorted, StoryHome? home, StoryDetectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(sorted);
        ArgumentNullException.ThrowIfNull(options);
        var result = new List<DetectedStory>();
        var events = SplitEvents(sorted, options);
        var classified = events.Select(e => (Items: e, Where: Classify(e, home, options))).ToList();

        var i = 0;
        while (i < classified.Count)
        {
            if (classified[i].Where != Where.Away)
            {
                AddEvent(result, classified[i].Items, isAway: false, options);
                i++;
                continue;
            }

            // Reise: weitere Ereignisse weg (oder ohne Ort), solange die Lücken klein bleiben; eines zu Hause beendet sie.
            var lastAway = i;
            for (var k = i + 1; k < classified.Count; k++)
            {
                var gap = classified[k].Items[0].Time - classified[k - 1].Items[^1].Time;
                if (gap > options.TripGap.TotalSeconds || classified[k].Where == Where.Home)
                {
                    break;
                }

                if (classified[k].Where == Where.Away)
                {
                    lastAway = k;
                }
            }

            var run = classified.Skip(i).Take(lastAway - i + 1).SelectMany(c => c.Items).ToList();
            var spansDays = run.Max(x => x.Day) > run.Min(x => x.Day);
            if (spansDays && run.Count >= options.MinFiles)
            {
                var places = SignificantPlaces(run, options);
                result.Add(new DetectedStory
                {
                    Kind = StoryKind.Trip,
                    Signature = "T:" + Inv(run.Min(x => x.Day)) + "-" + Inv(run.Max(x => x.Day)),
                    Items = run,
                    PlaceIds = places,
                    IsAway = true,
                });
            }
            else
            {
                for (var k = i; k <= lastAway; k++)
                {
                    AddEvent(result, classified[k].Items, classified[k].Where == Where.Away, options);
                }
            }

            i = lastAway + 1;
        }

        // Ungefähre Daten: je Monat bzw. Jahr eine Gruppe.
        foreach (var bucket in sorted.Where(x => x.IsApproximate).GroupBy(ApproximateKey))
        {
            var items = bucket.ToList();
            if (items.Count < options.MinFiles)
            {
                continue;
            }

            result.Add(new DetectedStory
            {
                Kind = StoryKind.Event,
                Signature = bucket.Key,
                Items = items,
                Precision = items[0].Precision,
                PlaceIds = SignificantPlaces(items, options),
            });
        }

        return result;
    }

    /// <summary>Teilt die Zeitachse (ohne ungefähre Daten) bei Pause bzw. Ortswechsel.</summary>
    public static List<List<StoryTimelineItem>> SplitEvents(IReadOnlyList<StoryTimelineItem> sorted, StoryDetectionOptions options)
    {
        var events = new List<List<StoryTimelineItem>>();
        List<StoryTimelineItem>? current = null;
        StoryTimelineItem? lastGeo = null;
        var pause = (long)options.Pause.TotalSeconds;
        foreach (var item in sorted)
        {
            if (item.IsApproximate)
            {
                continue;
            }

            var split = current is null
                        || item.Time - current[^1].Time > pause
                        || (item.HasGeo && lastGeo is { } g && DistanceKm(g.Latitude!.Value, g.Longitude!.Value, item.Latitude!.Value, item.Longitude!.Value) > options.SplitDistanceKm);
            if (split)
            {
                current = new List<StoryTimelineItem>();
                events.Add(current);
                lastGeo = null;
            }

            current!.Add(item);
            if (item.HasGeo)
            {
                lastGeo = item;
            }
        }

        return events;
    }

    /// <summary>Orte-Storys: je Ort alle Dateien, Besuche in mindestens N verschiedenen Jahren, nicht zu Hause.</summary>
    /// <param name="onlyPlaces">Nur diese Orte auswerten (inkrementell); null = alle.</param>
    public static List<DetectedStory> DetectPlaces(IEnumerable<StoryTimelineItem> items, StoryHome? home, StoryDetectionOptions options, IReadOnlySet<int>? onlyPlaces = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        var result = new List<DetectedStory>();
        foreach (var group in items.Where(x => x.PlaceId is { } p && (onlyPlaces is null || onlyPlaces.Contains(p))).GroupBy(x => x.PlaceId!.Value))
        {
            if (home?.PlaceId == group.Key)
            {
                continue;
            }

            var list = group.OrderBy(x => x.Time).ThenBy(x => x.FileId).ToList();
            if (list.Count < options.MinFiles)
            {
                continue;
            }

            var years = list.Select(x => YearOf(x.Day)).Distinct().Count();
            if (years < options.MinPlaceYears)
            {
                continue;
            }

            var geo = list.Where(x => x.HasGeo).ToList();
            if (home is not null && geo.Count > 0
                && DistanceKm(home.Latitude, home.Longitude, geo.Average(x => x.Latitude!.Value), geo.Average(x => x.Longitude!.Value)) <= options.HomeRadiusKm)
            {
                continue;
            }

            result.Add(new DetectedStory
            {
                Kind = StoryKind.Place,
                Signature = "O:" + Inv(group.Key),
                Items = list,
                PlaceId = group.Key,
                PlaceIds = [group.Key],
                VisitCount = CountVisits(list),
                IsAway = true,
            });
        }

        return result;
    }

    /// <summary>
    /// Personen-Storys: je Person Jahre mit mindestens <see cref="StoryDetectionOptions.MinPersonFiles"/> Fotos; benachbarte
    /// Jahre darunter werden zusammengezählt, bis die Schwelle erreicht ist („Max · 2021–2023“).
    /// </summary>
    public static List<DetectedStory> DetectPersons(IReadOnlyDictionary<int, List<StoryTimelineItem>> byPerson, StoryDetectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(byPerson);
        var result = new List<DetectedStory>();
        foreach (var (personId, files) in byPerson)
        {
            var years = files.GroupBy(x => YearOf(x.Day)).OrderBy(g => g.Key).Select(g => (Year: g.Key, Items: g.ToList())).ToList();
            var i = 0;
            while (i < years.Count)
            {
                if (years[i].Items.Count >= options.MinPersonFiles)
                {
                    result.Add(PersonStory(personId, years[i].Year, years[i].Year, years[i].Items));
                    i++;
                    continue;
                }

                var j = i;
                var sum = years[i].Items.Count;
                while (sum < options.MinPersonFiles && j + 1 < years.Count && years[j + 1].Year == years[j].Year + 1 && years[j + 1].Items.Count < options.MinPersonFiles)
                {
                    j++;
                    sum += years[j].Items.Count;
                }

                if (sum >= options.MinPersonFiles)
                {
                    result.Add(PersonStory(personId, years[i].Year, years[j].Year, years.Skip(i).Take(j - i + 1).SelectMany(y => y.Items).ToList()));
                }

                i = j + 1;
            }
        }

        return result;
    }

    /// <summary>Entfernung zweier Koordinaten (Haversine) in km.</summary>
    public static double DistanceKm(double lat1, double lon1, double lat2, double lon2)
    {
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLon = (lon2 - lon1) * Math.PI / 180;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                + Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * EarthRadiusKm * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    /// <summary>Kalenderdatum einer Tageszahl (seit 1970-01-01).</summary>
    public static DateOnly DateOf(int day) => DateOnly.FromDayNumber(UnixEpochDayNumber + day);

    /// <summary>Tageszahl (seit 1970-01-01) eines Kalenderdatums.</summary>
    public static int DayOf(DateOnly date) => date.DayNumber - UnixEpochDayNumber;

    public static int YearOf(int day) => DateOf(day).Year;

    private static readonly int UnixEpochDayNumber = new DateOnly(1970, 1, 1).DayNumber;

    private enum Where
    {
        Unknown,
        Home,
        Away,
    }

    private static Where Classify(List<StoryTimelineItem> items, StoryHome? home, StoryDetectionOptions options)
    {
        if (home is null)
        {
            return Where.Unknown;
        }

        double lat = 0, lon = 0;
        var n = 0;
        foreach (var item in items)
        {
            if (item.HasGeo)
            {
                lat += item.Latitude!.Value;
                lon += item.Longitude!.Value;
                n++;
            }
        }

        if (n == 0)
        {
            return Where.Unknown;
        }

        return DistanceKm(home.Latitude, home.Longitude, lat / n, lon / n) > options.HomeRadiusKm ? Where.Away : Where.Home;
    }

    private static void AddEvent(List<DetectedStory> result, List<StoryTimelineItem> items, bool isAway, StoryDetectionOptions options)
    {
        if (items.Count < options.MinFiles)
        {
            return;
        }

        result.Add(new DetectedStory
        {
            Kind = StoryKind.Event,
            Signature = "E:" + Inv(items[0].Time),
            Items = items,
            PlaceIds = SignificantPlaces(items, options),
            IsAway = isAway,
        });
    }

    private static DetectedStory PersonStory(int personId, int fromYear, int toYear, List<StoryTimelineItem> items)
    {
        var ordered = items.OrderBy(x => x.Time).ThenBy(x => x.FileId).ToList();
        return new DetectedStory
        {
            Kind = StoryKind.Person,
            Signature = "P:" + Inv(personId) + ":" + Inv(fromYear) + "-" + Inv(toYear),
            Items = ordered,
            PersonId = personId,
            PlaceIds = [],
        };
    }

    /// <summary>Orte mit nennenswertem Anteil, in Reihenfolge ihres ersten Auftretens.</summary>
    internal static List<int> SignificantPlaces(IReadOnlyList<StoryTimelineItem> items, StoryDetectionOptions options)
    {
        var counts = new Dictionary<int, int>();
        var order = new List<int>();
        var located = 0;
        foreach (var item in items)
        {
            if (item.PlaceId is not { } p)
            {
                continue;
            }

            located++;
            if (counts.TryGetValue(p, out var c))
            {
                counts[p] = c + 1;
            }
            else
            {
                counts[p] = 1;
                order.Add(p);
            }
        }

        var min = Math.Max(2, (int)Math.Ceiling(located * options.MinPlaceShare));
        var significant = order.Where(p => counts[p] >= min).ToList();
        if (significant.Count == 0 && order.Count > 0)
        {
            significant.Add(counts.OrderByDescending(kv => kv.Value).ThenBy(kv => order.IndexOf(kv.Key)).First().Key);
        }

        return significant;
    }

    private static int CountVisits(List<StoryTimelineItem> sortedByTime)
    {
        var days = sortedByTime.Select(x => x.Day).Distinct().OrderBy(d => d).ToList();
        var visits = 0;
        var previous = int.MinValue;
        foreach (var day in days)
        {
            if (previous == int.MinValue || day - previous > VisitGapDays)
            {
                visits++;
            }

            previous = day;
        }

        return visits;
    }

    private static string ApproximateKey(StoryTimelineItem item)
    {
        var date = DateOf(item.Day);
        return item.Precision == CaptureDatePrecision.Year
            ? "A:Y:" + date.Year.ToString("D4", CultureInfo.InvariantCulture)
            : "A:M:" + date.Year.ToString("D4", CultureInfo.InvariantCulture) + "-" + date.Month.ToString("D2", CultureInfo.InvariantCulture);
    }

    private static string Inv(long value) => value.ToString(CultureInfo.InvariantCulture);
}
