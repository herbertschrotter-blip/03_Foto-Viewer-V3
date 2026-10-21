using Lumora.Domain.Enums;
using Lumora.Services.Stories;

namespace Lumora.Tests.Stories;

/// <summary>
/// Reine Erkennung (ADR-0058) mit synthetischen Zeitachsen: Pause und Ortswechsel trennen Ereignisse, Reisen fassen
/// Tage weg von zu Hause zusammen (Tage zu Hause bleiben Einzel-Ereignisse), Mindestgröße, ungefähre Daten, Personen-
/// und Orte-Schwellen.
/// </summary>
public sealed class StoryDetectorTests
{
    private static readonly StoryHome Home = new(TestPlaces.All[TestPlaces.Graz].Lat, TestPlaces.All[TestPlaces.Graz].Lon, TestPlaces.Graz);
    private static readonly StoryDetectionOptions Options = new();
    private int _id;

    [Fact]
    public void Pause_ueber_der_Einstellung_trennt_Ereignisse()
    {
        // 10:00–11:10 und 18:00–19:10 (Pause 6 h 50 min > 6 h) → zwei Ereignisse; mit 8 h Pause eins.
        var items = Timeline(StoryWorld.Burst(StoryWorld.Utc(2025, 9, 6, 10), 15).Concat(StoryWorld.Burst(StoryWorld.Utc(2025, 9, 6, 18), 15)));
        var events = StoryDetector.DetectEvents(items, Home, Options);
        events.Should().HaveCount(2);
        events.Should().OnlyContain(e => e.Kind == StoryKind.Event && e.Items.Count == 15);
        events[0].Signature.Should().StartWith("E:");

        StoryDetector.DetectEvents(items, Home, Options with { Pause = TimeSpan.FromHours(8) }).Should().ContainSingle()
            .Which.Items.Should().HaveCount(30);
    }

    [Fact]
    public void Ortswechsel_ueber_30_km_trennt_auch_ohne_Pause()
    {
        // Graz 10:00–11:10, eine Stunde später Wien (≈ 145 km) → zwei Ereignisse.
        var items = Timeline(StoryWorld.Burst(StoryWorld.Utc(2025, 5, 3, 10), 15, TestPlaces.Graz)
            .Concat(StoryWorld.Burst(StoryWorld.Utc(2025, 5, 3, 12, 10), 15, TestPlaces.Wien)));
        var events = StoryDetector.DetectEvents(items, Home, Options);
        events.Should().HaveCount(2);
        events[0].IsAway.Should().BeFalse("Graz ist zu Hause");
        events[1].IsAway.Should().BeTrue("ein Tag in Wien = Ausflug");
        events[1].PlaceIds.Should().Equal(TestPlaces.Wien);
    }

    [Fact]
    public void Mindestgroesse_greift()
    {
        StoryDetector.DetectEvents(Timeline(StoryWorld.Burst(StoryWorld.Utc(2025, 1, 1), 11)), Home, Options).Should().BeEmpty();
        StoryDetector.DetectEvents(Timeline(StoryWorld.Burst(StoryWorld.Utc(2025, 1, 1), 12)), Home, Options).Should().ContainSingle();
        StoryDetector.DetectEvents(Timeline(StoryWorld.Burst(StoryWorld.Utc(2025, 1, 1), 11)), Home, Options with { MinFiles = 8 }).Should().ContainSingle();
    }

    [Fact]
    public void Reise_fasst_Tage_weg_von_zu_Hause_zusammen_Tage_zu_Hause_bleiben_Einzel_Ereignisse()
    {
        var files = new List<StoryFile>();
        files.AddRange(StoryWorld.Burst(StoryWorld.Utc(2025, 7, 11, 9), 14, TestPlaces.Graz));      // zu Hause, Tag davor
        files.AddRange(StoryWorld.Burst(StoryWorld.Utc(2025, 7, 12, 9), 10, TestPlaces.Grado));     // weg: 3 Tage, je < Mindestgröße
        files.AddRange(StoryWorld.Burst(StoryWorld.Utc(2025, 7, 13, 9), 4));                        // Tag ohne Ort mitten in der Reise
        files.AddRange(StoryWorld.Burst(StoryWorld.Utc(2025, 7, 13, 15), 6, TestPlaces.Triest));
        files.AddRange(StoryWorld.Burst(StoryWorld.Utc(2025, 7, 14, 9), 8, TestPlaces.Aquileia));
        files.AddRange(StoryWorld.Burst(StoryWorld.Utc(2025, 7, 15, 9), 14, TestPlaces.Graz));      // zurück zu Hause
        files.AddRange(StoryWorld.Burst(StoryWorld.Utc(2025, 7, 16, 9), 13, TestPlaces.Graz));
        var stories = StoryDetector.DetectEvents(Timeline(files), Home, Options);

        stories.Select(s => s.Kind).Should().Equal(StoryKind.Event, StoryKind.Trip, StoryKind.Event, StoryKind.Event);
        var trip = stories[1];
        trip.Items.Should().HaveCount(28, "drei Tage weg samt dem Tag ohne Ortsangabe");
        trip.Signature.Should().Be($"T:{StoryDetector.DayOf(new DateOnly(2025, 7, 12))}-{StoryDetector.DayOf(new DateOnly(2025, 7, 14))}");
        trip.PlaceIds.Should().Equal(TestPlaces.Grado, TestPlaces.Triest, TestPlaces.Aquileia);
        trip.IsAway.Should().BeTrue();
        stories.Where(s => s.Kind == StoryKind.Event).Should().OnlyContain(s => !s.IsAway && s.FirstDay == s.LastDay, "Tage zu Hause bleiben einzelne Ereignisse");
    }

    [Fact]
    public void Ein_Tag_zu_Hause_beendet_die_Reise_und_eine_kleine_Reise_unter_der_Mindestgroesse_entfaellt()
    {
        var files = StoryWorld.Burst(StoryWorld.Utc(2024, 8, 1, 9), 6, TestPlaces.Rovinj)
            .Concat(StoryWorld.Burst(StoryWorld.Utc(2024, 8, 2, 9), 5, TestPlaces.Rovinj));
        StoryDetector.DetectEvents(Timeline(files), Home, Options).Should().BeEmpty("11 Fotos < 12");

        var split = StoryWorld.Burst(StoryWorld.Utc(2024, 8, 1, 9), 12, TestPlaces.Rovinj)
            .Concat(StoryWorld.Burst(StoryWorld.Utc(2024, 8, 2, 9), 12, TestPlaces.Graz))
            .Concat(StoryWorld.Burst(StoryWorld.Utc(2024, 8, 3, 9), 12, TestPlaces.Rovinj));
        var stories = StoryDetector.DetectEvents(Timeline(split), Home, Options);
        stories.Should().HaveCount(3).And.OnlyContain(s => s.Kind == StoryKind.Event, "der Tag zu Hause unterbricht — zwei Ausflüge und ein Ereignis");
        stories.Count(s => s.IsAway).Should().Be(2);
    }

    [Fact]
    public void Ungefaehre_Daten_bilden_eigene_Gruppen_je_Monat_bzw_Jahr()
    {
        var mid = StoryWorld.Utc(2025, 7, 15, 12);
        var files = Enumerable.Range(0, 13).Select(_ => new StoryFile(mid, Precision: CaptureDatePrecision.Month))
            .Concat(Enumerable.Range(0, 12).Select(_ => new StoryFile(StoryWorld.Utc(1987, 7, 1, 12), Precision: CaptureDatePrecision.Year)))
            .Concat(StoryWorld.Burst(StoryWorld.Utc(2025, 7, 15, 11), 12));
        var stories = StoryDetector.DetectEvents(Timeline(files), Home, Options);
        stories.Should().HaveCount(3);
        stories.Should().ContainSingle(s => s.Signature == "A:M:2025-07").Which.Precision.Should().Be(CaptureDatePrecision.Month);
        stories.Should().ContainSingle(s => s.Signature == "A:Y:1987").Which.Precision.Should().Be(CaptureDatePrecision.Year);
        stories.Should().ContainSingle(s => s.Signature.StartsWith("E:")).Which.Items.Should().HaveCount(12, "ungefähre Daten mischen sich nicht in das echte Ereignis");
    }

    [Fact]
    public void Personen_Storys_nach_Schwelle_mit_Jahresspannen()
    {
        var byPerson = new Dictionary<int, List<StoryTimelineItem>>
        {
            [1] = Timeline(StoryWorld.Burst(StoryWorld.Utc(2019, 6, 1), 35)
                .Concat(StoryWorld.Burst(StoryWorld.Utc(2021, 6, 1), 10))
                .Concat(StoryWorld.Burst(StoryWorld.Utc(2022, 6, 1), 12))
                .Concat(StoryWorld.Burst(StoryWorld.Utc(2023, 6, 1), 9))
                .Concat(StoryWorld.Burst(StoryWorld.Utc(2025, 6, 1), 5))),
            [2] = Timeline(StoryWorld.Burst(StoryWorld.Utc(2020, 6, 1), 20)),
        };
        var stories = StoryDetector.DetectPersons(byPerson, Options);
        stories.Select(s => s.Signature).Should().BeEquivalentTo("P:1:2019-2019", "P:1:2021-2023");
        stories.Single(s => s.Signature == "P:1:2021-2023").Items.Should().HaveCount(31);
        stories.Should().OnlyContain(s => s.Kind == StoryKind.Person && s.PersonId == 1);
    }

    [Fact]
    public void Orte_mit_Besuchen_in_mindestens_drei_Jahren_nicht_zu_Hause()
    {
        var files = StoryWorld.Burst(StoryWorld.Utc(2014, 7, 1), 5, TestPlaces.Grado)
            .Concat(StoryWorld.Burst(StoryWorld.Utc(2018, 7, 1), 5, TestPlaces.Grado))
            .Concat(StoryWorld.Burst(StoryWorld.Utc(2018, 7, 9), 3, TestPlaces.Grado))
            .Concat(StoryWorld.Burst(StoryWorld.Utc(2025, 7, 1), 5, TestPlaces.Grado))
            .Concat(StoryWorld.Burst(StoryWorld.Utc(2019, 7, 1), 20, TestPlaces.Rovinj))
            .Concat(StoryWorld.Burst(StoryWorld.Utc(2020, 7, 1), 20, TestPlaces.Rovinj))
            .Concat(StoryWorld.Burst(StoryWorld.Utc(2014, 1, 1), 20, TestPlaces.Graz))
            .Concat(StoryWorld.Burst(StoryWorld.Utc(2015, 1, 1), 20, TestPlaces.Graz))
            .Concat(StoryWorld.Burst(StoryWorld.Utc(2016, 1, 1), 20, TestPlaces.Graz));
        var stories = StoryDetector.DetectPlaces(Timeline(files), Home, Options);
        var grado = stories.Should().ContainSingle("Rovinj nur in zwei Jahren, Graz ist zu Hause").Subject;
        grado.Signature.Should().Be("O:" + TestPlaces.Grado);
        grado.Items.Should().HaveCount(18);
        grado.VisitCount.Should().Be(4, "2018 zwei getrennte Besuche");
    }

    private List<StoryTimelineItem> Timeline(IEnumerable<StoryFile> files)
    {
        var list = files.Select(f =>
        {
            var t = new DateTimeOffset(f.Utc).ToUnixTimeSeconds();
            return new StoryTimelineItem(++_id, t, (int)Math.Floor(t / 86400.0), f.Precision, f.Lat, f.Lon, f.PlaceId, f.Video);
        }).ToList();
        list.Sort((x, y) => x.Time != y.Time ? x.Time.CompareTo(y.Time) : x.FileId.CompareTo(y.FileId));
        return list;
    }
}
