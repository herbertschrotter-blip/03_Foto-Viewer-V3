using System.Diagnostics;
using System.Globalization;
using Lumora.Services.Stories;
using Xunit.Abstractions;

namespace Lumora.Tests.Stories;

/// <summary>Nur mit <c>LUMORA_STORY_PERF=1</c> (Messung für ADR-0058 / Spike 17), sonst übersprungen.</summary>
public sealed class StoryPerfFactAttribute : FactAttribute
{
    public StoryPerfFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("LUMORA_STORY_PERF") != "1")
        {
            Skip = "Messung nur mit LUMORA_STORY_PERF=1.";
        }
    }
}

/// <summary>
/// Messung der Story-Erkennung (docs/spikes/17-story-erkennung.md): synthetischer Katalog mit
/// <c>LUMORA_STORY_PERF_FILES</c> (Standard 500 000) Dateien über 20 Jahre — Ereignisse zu Hause, Ausflüge, Reisen,
/// 25 % ohne Ort, 20 Personen mit bestätigten Gesichtern. Gemessen: volle Erkennung (erste und zweite), inkrementell nach
/// neuen Fotos am Rand eines Ereignisses, Übersicht laden (kalt und warm).
/// </summary>
public sealed class StoryPerformanceTests(ITestOutputHelper output)
{
    [StoryPerfFact]
    public async Task Volle_und_inkrementelle_Erkennung_und_Uebersicht()
    {
        var total = int.TryParse(Environment.GetEnvironmentVariable("LUMORA_STORY_PERF_FILES"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 500_000;
        using var w = await StoryWorld.CreateAsync();
        var group = await w.GroupIdAsync();
        var sw = Stopwatch.StartNew();
        var (ids, eventStarts) = Generate(w, total);
        Log($"Katalog: {ids.Count:N0} Dateien in {sw.Elapsed.TotalSeconds:N1} s erzeugt");

        sw.Restart();
        var persons = AddFaces(w, ids, group);
        Log($"Gesichter: {persons:N0} bestätigte in {sw.Elapsed.TotalSeconds:N1} s");

        var first = await w.Index.RunFullAsync(group);
        Report("Volle Erkennung (leer)", first);
        var stories = w.Scalar("SELECT COUNT(*) FROM Stories");
        var items = w.Scalar("SELECT COUNT(*) FROM StoryItems");
        Log($"  → {stories:N0} Storys, {items:N0} Einträge (Ereignisse {w.Scalar("SELECT COUNT(*) FROM Stories WHERE Kind = 0"):N0}, Reisen {w.Scalar("SELECT COUNT(*) FROM Stories WHERE Kind = 1"):N0}, Personen {w.Scalar("SELECT COUNT(*) FROM Stories WHERE Kind = 3"):N0}, Orte {w.Scalar("SELECT COUNT(*) FROM Stories WHERE Kind = 4"):N0})");

        var second = await w.Index.RunFullAsync(group);
        Report("Volle Erkennung (ohne Änderung)", second);

        // Neue Fotos am Rand eines Ereignisses (20 Minuten vor seinem Anfang).
        var start = eventStarts[eventStarts.Count / 2];
        var edge = w.Add(Enumerable.Range(0, 20).Select(i => TestPlaces.At(TestPlaces.Graz, start.AddMinutes(-20 + i))));
        var incremental = await w.Index.RunIncrementalAsync(group, edge);
        Report("Inkrementell (20 neue Fotos am Rand)", incremental);
        var incremental2 = await w.Index.RunIncrementalAsync(group, edge.Take(1).ToList());
        Report("Inkrementell (1 Datei, keine Änderung)", incremental2);

        sw.Restart();
        var overview = await w.Stories.GetOverviewAsync();
        Log($"Übersicht kalt: {sw.Elapsed.TotalMilliseconds:N0} ms ({overview.EventsAndTrips.Count:N0} Ereignisse/Reisen, {overview.Persons.Count} Personen, {overview.Places.Count} Orte, {overview.Memories.Count} Rückblicke)");
        var warm = new List<double>();
        for (var i = 0; i < 5; i++)
        {
            sw.Restart();
            _ = await w.Stories.GetOverviewAsync();
            warm.Add(sw.Elapsed.TotalMilliseconds);
        }

        Log($"Übersicht warm: Median {warm.Order().ElementAt(2):N0} ms (min {warm.Min():N0}, max {warm.Max():N0})");
        sw.Restart();
        var memories = await w.Stories.GetMemoriesAsync();
        Log($"  davon Rückblicke allein: {sw.Elapsed.TotalMilliseconds:N0} ms ({memories.Count})");
        var trip = overview.EventsAndTrips.First(c => c.Kind == Lumora.Domain.Enums.StoryKind.Trip);
        sw.Restart();
        var details = await w.Stories.GetDetailsAsync(trip.Id);
        var highlights = await w.Stories.GetHighlightsAsync(trip.Id);
        Log($"Story-Ansicht (Reise mit {details!.Card.FileCount:N0} Fotos, {details.Days.Count} Tagen) + Highlights ({highlights.Count}): {sw.Elapsed.TotalMilliseconds:N0} ms");
        first.Groups.Should().Be(1);
    }

    private void Report(string label, StoryIndexReport r)
        => Log($"{label}: {r.Total.TotalMilliseconds:N0} ms — lesen {r.Read.TotalMilliseconds:N0}, erkennen {r.Detect.TotalMilliseconds:N0}, schreiben {r.Write.TotalMilliseconds:N0}, Titelbilder {r.Covers.TotalMilliseconds:N0} ms; {r.Files:N0} Dateien, {r.Detected:N0} erkannt, {r.Inserted:N0} neu, {r.Changed:N0} geändert, {r.Deleted:N0} entfernt");

    private void Log(string text) => output.WriteLine(text);

    /// <summary>Zufällige, aber feste Fotobibliothek: an etwa jedem dritten Tag ein bis drei Ereignisse, drei Reisen im Jahr.</summary>
    private static (List<int> Ids, List<DateTime> EventStarts) Generate(StoryWorld w, int total)
    {
        var random = new Random(4711);
        var places = new List<(double Lat, double Lon, int Id)>();
        for (var i = 0; i < 300; i++)
        {
            places.Add((random.NextDouble() * 20 + 36, random.NextDouble() * 25 - 5, 20_000 + i)); // Europa
        }

        var home = TestPlaces.All[TestPlaces.Graz];
        var files = new List<StoryFile>(total + 1000);
        var eventStarts = new List<DateTime>();
        var day = new DateTime(2006, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var perDay = total / (20 * 365.0);
        while (files.Count < total)
        {
            if (random.NextDouble() < 0.012)
            {
                // Reise: 4–14 Tage an 1–4 Orten
                var length = random.Next(4, 15);
                var stops = Enumerable.Range(0, random.Next(1, 5)).Select(_ => places[random.Next(places.Count)]).ToList();
                for (var d = 0; d < length && files.Count < total; d++)
                {
                    var stop = stops[d * stops.Count / length];
                    var start = day.AddDays(d).AddHours(8 + random.Next(3));
                    eventStarts.Add(start);
                    var count = random.Next(40, 250);
                    for (var i = 0; i < count; i++)
                    {
                        var t = start.AddSeconds(i * random.Next(20, 200));
                        files.Add(random.NextDouble() < 0.25
                            ? new StoryFile(t, Video: random.NextDouble() < 0.05)
                            : new StoryFile(t, stop.Lat + random.NextDouble() * 0.02, stop.Lon + random.NextDouble() * 0.02, stop.Id, random.NextDouble() < 0.05));
                    }
                }

                day = day.AddDays(length);
                continue;
            }

            var events = random.NextDouble() < 0.35 ? random.Next(1, 4) : 0;
            for (var e = 0; e < events && files.Count < total; e++)
            {
                var start = day.AddHours(7 + e * 5 + random.Next(3));
                eventStarts.Add(start);
                var away = random.NextDouble() < 0.1 ? places[random.Next(places.Count)] : (home.Lat, home.Lon, TestPlaces.Graz);
                var count = Math.Max(3, (int)(random.NextDouble() * perDay * 5));
                for (var i = 0; i < count; i++)
                {
                    var t = start.AddSeconds(i * random.Next(10, 120));
                    files.Add(random.NextDouble() < 0.25
                        ? new StoryFile(t, Video: random.NextDouble() < 0.05)
                        : new StoryFile(t, away.Item1 + random.NextDouble() * 0.01, away.Item2 + random.NextDouble() * 0.01, away.Item3, random.NextDouble() < 0.05));
                }
            }

            day = day.AddDays(1);
        }

        var ids = new List<int>(files.Count);
        foreach (var chunk in files.Take(total).Chunk(50_000))
        {
            ids.AddRange(w.Add(chunk));
        }

        return (ids, eventStarts);
    }

    /// <summary>20 Personen, je Person ein Anteil der Dateien mit bestätigtem Gesicht (leere Vektoren genügen der Erkennung).</summary>
    private static int AddFaces(StoryWorld w, List<int> ids, int group)
    {
        using var connection = w.Fx.ContextFactory.OpenSqliteConnection();
        using var tx = connection.BeginTransaction();
        var personIds = new List<long>();
        for (var p = 0; p < 20; p++)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO Persons (Name, FirstName, LastName, CreatedAtUtc, IsHidden, IsMe, GroupId) VALUES ($n, $n, '', 0, 0, 0, $g); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$n", "Person " + p.ToString(CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$g", group);
            personIds.Add((long)cmd.ExecuteScalar()!);
        }

        var random = new Random(42);
        var count = 0;
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO Faces (FileId, BoxX, BoxY, BoxW, BoxH, Landmarks, DetScore, Size, Sharpness, Quality, Embedding, ModelVersion, PersonId, State, CreatedAtUtc)
                VALUES ($f, 0.4, 0.4, 0.2, 0.2, x'', 0.9, 100, 10, 0.9, x'', 'perf', $p, 2, 0);
                """;
            var f = cmd.Parameters.Add("$f", Microsoft.Data.Sqlite.SqliteType.Integer);
            var pp = cmd.Parameters.Add("$p", Microsoft.Data.Sqlite.SqliteType.Integer);
            foreach (var id in ids)
            {
                if (random.NextDouble() < 0.2)
                {
                    f.Value = id;
                    pp.Value = personIds[(int)Math.Min(19, Math.Abs(random.NextDouble() * random.NextDouble() * 20))];
                    cmd.ExecuteNonQuery();
                    count++;
                }
            }
        }

        tx.Commit();
        return count;
    }
}
