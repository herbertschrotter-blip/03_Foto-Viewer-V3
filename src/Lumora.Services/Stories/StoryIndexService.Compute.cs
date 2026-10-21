using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Lumora.Services.Library;

namespace Lumora.Services.Stories;

public sealed partial class StoryIndexService
{
    /// <summary>Spanne je Abfrage beim Suchen einer Fenstergrenze (größer als jede überbrückbare Lücke).</summary>
    private const long GapSearchSpanSeconds = 60L * 86400;

    /// <summary>Mehr getrennte Fenster als das werden zu einem zusammengefasst.</summary>
    private const int MaxWindows = 100;

    /// <summary>
    /// Ein Durchgang für eine Gruppe: lesen, erkennen, abgleichen, schreiben (eine Transaktion). Ohne
    /// <paramref name="fileIds"/> ganz, sonst nur die Fenster/Orte/Personen der Dateien.
    /// </summary>
    private StoryIndexReport Compute(int groupId, StoryFamilies families, IReadOnlyCollection<int>? fileIds, CancellationToken token)
    {
        var total = Stopwatch.StartNew();
        using var connection = _factory.OpenSqliteConnection();
        var libraries = LibraryGroupStore.ReadGroupLibraries(connection, null, groupId);
        if (libraries.Count == 0)
        {
            return StoryIndexReport.Empty;
        }

        var settings = StorySettingsStore.Load(connection);
        var options = StoryDetectionOptions.From(settings);
        var zone = _clock.Zone;
        // Zuhause: bei jeder vollen Berechnung neu (zählt die Orte der ganzen Gruppe), inkrementell aus dem letzten Durchgang.
        StoryHome? home;
        if (fileIds is not null && _homes is not null && _homes.TryGet(groupId, out var cachedHome))
        {
            home = cachedHome;
        }
        else
        {
            home = StorySql.ReadHome(connection, libraries, settings.HomeOrAutomatic);
            _homes?.Set(groupId, home);
        }
        var doEvents = families.HasFlag(StoryFamilies.Events) && settings.EventsEnabled;
        var doPlaces = families.HasFlag(StoryFamilies.Places) && settings.PlacesEnabled;
        var doPersons = families.HasFlag(StoryFamilies.Persons) && settings.PersonsEnabled;

        var read = Stopwatch.StartNew();
        var work = new List<(string Where, List<DetectedStory>? Detected)>();
        List<StoryTimelineItem> timeline;
        List<StoryTimelineItem> placeItems;
        Dictionary<int, List<StoryTimelineItem>>? personItems = null;
        List<(long From, long To)>? windows = null;
        HashSet<int>? placeIds = null;
        HashSet<int>? personIds = null;
        if (fileIds is null)
        {
            timeline = doEvents || doPlaces || doPersons ? StorySql.ReadTimeline(connection, libraries, zone) : [];
            placeItems = timeline;
            personItems = doPersons ? StorySql.PersonItemsFromTimeline(connection, groupId, timeline) : null;
        }
        else
        {
            var dirty = ReadDirty(connection, groupId, libraries, fileIds);
            timeline = [];
            if (doEvents)
            {
                windows = ExpandWindows(connection, groupId, libraries, dirty.Intervals, options.MaxBridgeSeconds);
                foreach (var (from, to) in windows)
                {
                    timeline.AddRange(StorySql.ReadTimeline(connection, libraries, zone, from, to));
                }
            }

            placeIds = dirty.PlaceIds;
            if (home?.PlaceId is { } homePlace)
            {
                placeIds.Remove(homePlace); // zu Hause wird nie eine Orte-Story — und hat die meisten Fotos
            }

            placeItems = doPlaces ? StorySql.ReadByPlaces(connection, libraries, placeIds, zone) : [];
            personIds = dirty.PersonIds;
            personItems = doPersons && personIds.Count > 0 ? StorySql.ReadPersonItems(connection, groupId, libraries, zone, personIds) : null;
        }

        read.Stop();
        token.ThrowIfCancellationRequested();

        var detect = Stopwatch.StartNew();
        if (doEvents && (windows is null || windows.Count > 0))
        {
            var events = new List<DetectedStory>();
            if (windows is null)
            {
                events.AddRange(StoryDetector.DetectEvents(timeline, home, options));
            }
            else
            {
                // Je Fenster getrennt (die Zeitachse ist je Fenster sortiert, Fenster überschneiden sich nicht).
                foreach (var (from, to) in windows)
                {
                    events.AddRange(StoryDetector.DetectEvents(timeline.Where(x => x.Time >= from && x.Time <= to).ToList(), home, options));
                }
            }

            var where = "s.Kind IN (0, 1)";
            if (windows is not null)
            {
                where += " AND (" + string.Join(" OR ", windows.Select(w => $"(s.EndAt >= {Inv(w.From)} AND s.StartAt <= {Inv(w.To)})")) + ")";
            }

            work.Add((where, events));
        }

        if (doPlaces && (placeIds is null || placeIds.Count > 0))
        {
            var where = placeIds is null ? "s.Kind = 4" : $"s.Kind = 4 AND s.PlaceId IN ({StorySql.IdList(placeIds)})";
            work.Add((where, StoryDetector.DetectPlaces(placeItems, home, options, placeIds)));
        }

        if (doPersons && (personIds is null || personIds.Count > 0))
        {
            var where = personIds is null ? "s.Kind = 3" : $"s.Kind = 3 AND s.PersonId IN ({StorySql.IdList(personIds)})";
            work.Add((where, StoryDetector.DetectPersons(personItems ?? new Dictionary<int, List<StoryTimelineItem>>(), options)));
        }

        detect.Stop();
        if (work.Count == 0)
        {
            return new StoryIndexReport(1, timeline.Count, 0, 0, 0, 0, read.Elapsed, detect.Elapsed, TimeSpan.Zero, TimeSpan.Zero, total.Elapsed);
        }

        token.ThrowIfCancellationRequested();
        var write = Stopwatch.StartNew();
        int inserted = 0, changed = 0, deleted = 0, detected = 0;
        using (var tx = _factory.BusyRetry.BeginTransactionAsync(connection, token).GetAwaiter().GetResult())
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            // Allererste Erkennung einer Gruppe: nichts ist „neu seit dem letzten Besuch“ — es gab keinen.
            var initial = StorySql.Scalar(connection, tx, "SELECT COUNT(*) FROM Stories WHERE GroupId = $g AND Kind IN (0, 1, 3, 4);", ("$g", groupId)) == 0;
            foreach (var (where, found) in work)
            {
                var existing = StorySql.ReadExisting(connection, tx, groupId, where);
                var plan = StoryReconciler.Reconcile(existing, found ?? []);
                var result = StoryPlanWriter.Apply(connection, tx, groupId, plan, now);
                inserted += result.Inserted;
                changed += result.Changed.Count - result.Inserted;
                deleted += result.Deleted.Count;
                detected += found?.Count ?? 0;
            }

            if (initial)
            {
                StorySql.Execute(connection, tx, "UPDATE Stories SET IsSeen = 1, SeenAtUtc = $now WHERE GroupId = $g AND Kind IN (0, 1, 3, 4) AND IsSeen = 0;", ("$now", now), ("$g", groupId));
            }

            tx.Commit();
        }

        write.Stop();
        return new StoryIndexReport(1, Math.Max(timeline.Count, placeItems.Count), detected, inserted, changed, deleted, read.Elapsed, detect.Elapsed, write.Elapsed, TimeSpan.Zero, total.Elapsed);
    }

    /// <summary>Geänderte Dateien: ihre Zeitpunkte und die Zeiträume, Orte und Personen der Storys, in denen sie stehen.</summary>
    private static (List<(long From, long To)> Intervals, HashSet<int> PlaceIds, HashSet<int> PersonIds) ReadDirty(
        SqliteConnection connection, int groupId, IReadOnlyCollection<int> libraries, IReadOnlyCollection<int> fileIds)
    {
        var intervals = new List<(long, long)>();
        var places = new HashSet<int>();
        var persons = new HashSet<int>();
        var libs = StorySql.IdList(libraries);
        foreach (var chunk in fileIds.Distinct().Chunk(500))
        {
            var ids = StorySql.IdList(chunk);
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = $"SELECT {StorySql.TimeKey}, f.PlaceId FROM Files f WHERE f.Id IN ({ids}) AND f.LibraryId IN ({libs});";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var t = reader.GetInt64(0);
                    intervals.Add((t, t));
                    if (!reader.IsDBNull(1))
                    {
                        places.Add(reader.GetInt32(1));
                    }
                }
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = $"""
                    SELECT DISTINCT s.Kind, s.StartAt, s.EndAt, s.PlaceId, s.PersonId
                    FROM StoryItems si CROSS JOIN Stories s ON s.Id = si.StoryId
                    WHERE si.FileId IN ({ids}) AND s.GroupId = $g AND s.Kind IN (0, 1, 3, 4);
                    """;
                cmd.Parameters.AddWithValue("$g", groupId);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    switch (reader.GetInt32(0))
                    {
                        case 0 or 1:
                            intervals.Add((reader.GetInt64(1), reader.GetInt64(2)));
                            break;
                        case 3 when !reader.IsDBNull(4):
                            persons.Add(reader.GetInt32(4));
                            break;
                        case 4 when !reader.IsDBNull(3):
                            places.Add(reader.GetInt32(3));
                            break;
                    }
                }
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = $"SELECT DISTINCT PersonId FROM Faces WHERE FileId IN ({ids}) AND State = 2 AND PersonId IS NOT NULL;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    persons.Add(reader.GetInt32(0));
                }
            }
        }

        return (intervals, places, persons);
    }

    /// <summary>
    /// Fenster um die geänderten Zeitpunkte: bis zur nächsten Lücke über <paramref name="bridge"/> Sekunden in beide
    /// Richtungen (kein Ereignis, keine Reise kann eine solche Lücke überspannen), zusammengelegt und so lange um die
    /// gespeicherten Ereignisse/Reisen erweitert, die hineinragen, bis sich nichts mehr ändert.
    /// </summary>
    private static List<(long From, long To)> ExpandWindows(SqliteConnection connection, int groupId, IReadOnlyCollection<int> libraries, List<(long From, long To)> seeds, long bridge)
    {
        var windows = Merge(seeds);
        for (var round = 0; round < 20 && windows.Count > 0; round++)
        {
            windows = Merge(windows.Select(w => (FindEdge(connection, libraries, w.From, bridge, backwards: true), FindEdge(connection, libraries, w.To, bridge, backwards: false))).ToList());
            var stories = new List<(long, long)>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = $"""
                    SELECT StartAt, EndAt FROM Stories s
                    WHERE s.GroupId = $g AND s.Kind IN (0, 1) AND ({string.Join(" OR ", windows.Select(w => $"(s.EndAt >= {Inv(w.From)} AND s.StartAt <= {Inv(w.To)})"))});
                    """;
                cmd.Parameters.AddWithValue("$g", groupId);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    stories.Add((reader.GetInt64(0), reader.GetInt64(1)));
                }
            }

            var grown = stories.Where(s => !windows.Any(w => w.From <= s.Item1 && s.Item2 <= w.To)).ToList();
            if (grown.Count == 0)
            {
                break;
            }

            windows = Merge(windows.Concat(grown).ToList());
        }

        return windows;
    }

    /// <summary>
    /// Zeitpunkt der äußersten Datei vor (bzw. nach) <paramref name="start"/>, ab der eine Lücke über <paramref name="bridge"/>
    /// folgt. Liest den Ausdrucks-Index in Abschnitten (alle Dateien der Bibliotheken — mehr Dateien machen das Fenster nur größer).
    /// </summary>
    private static long FindEdge(SqliteConnection connection, IReadOnlyCollection<int> libraries, long start, long bridge, bool backwards)
    {
        var span = Math.Max(GapSearchSpanSeconds, bridge * 2);
        var edge = start;
        var libs = StorySql.IdList(libraries);
        while (true)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = backwards
                ? $"SELECT {StorySql.TimeKey} FROM Files f WHERE f.LibraryId IN ({libs}) AND {StorySql.TimeKey} < $c AND {StorySql.TimeKey} >= $c - $span ORDER BY {StorySql.TimeKey} DESC;"
                : $"SELECT {StorySql.TimeKey} FROM Files f WHERE f.LibraryId IN ({libs}) AND {StorySql.TimeKey} > $c AND {StorySql.TimeKey} <= $c + $span ORDER BY {StorySql.TimeKey};";
            cmd.Parameters.AddWithValue("$c", edge);
            cmd.Parameters.AddWithValue("$span", span);
            var any = false;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                any = true;
                var t = reader.GetInt64(0);
                if (Math.Abs(edge - t) > bridge)
                {
                    return edge;
                }

                edge = t;
            }

            if (!any)
            {
                return edge; // nichts im ganzen Abschnitt: Lücke größer als jede Brücke
            }
        }
    }

    private static List<(long From, long To)> Merge(List<(long From, long To)> intervals)
    {
        var sorted = intervals.OrderBy(i => i.From).ToList();
        var merged = new List<(long From, long To)>();
        foreach (var (from, to) in sorted)
        {
            if (merged.Count > 0 && from <= merged[^1].To)
            {
                merged[^1] = (merged[^1].From, Math.Max(merged[^1].To, to));
            }
            else
            {
                merged.Add((from, to));
            }
        }

        // Sehr viele verstreute Fenster (z. B. ein großer Stapel des Nachtragens): ein einziges von der ersten bis zur
        // letzten Grenze — größer, aber genauso vollständig, und die Abfragen bleiben kurz (SQLite: höchstens 1000 Ebenen).
        return merged.Count > MaxWindows ? [(merged[0].From, merged.Max(w => w.To))] : merged;
    }

    private static string Inv(long value) => value.ToString(CultureInfo.InvariantCulture);
}
