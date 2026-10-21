using Lumora.Services.Quality;

namespace Lumora.Tests.Quality;

/// <summary>Rechenkern der Highlights (ADR-0057): Serien/Doppelte, Wert, Anzahl, Reihenfolge, Verteilung, Titelbild.</summary>
public sealed class HighlightPlannerTests
{
    /// <summary>Volle Stunde (UTC), damit Stunden-Fenster in den Tests nicht mitten durch eine Gruppe gehen.</summary>
    private const long Start = 1_699_999_200;

    /// <summary>Kandidat mit eigenem, weit entferntem pHash (fremdes Motiv) im Minutenabstand.</summary>
    private static HighlightCandidate Photo(
        int position,
        double sharpness = 0.6,
        double exposure = 0.7,
        long? time = null,
        ulong? hash = null,
        bool favorite = false,
        int faces = 0,
        double faceQuality = 0.8,
        int rating = 0,
        double? aesthetic = null,
        bool video = false,
        byte[]? content = null,
        bool unrated = false)
        => new(
            1000 + position,
            position,
            time ?? Start + (position * 600L),
            video,
            unrated ? null : sharpness,
            unrated ? null : exposure,
            aesthetic,
            faces,
            faceQuality,
            favorite,
            rating,
            hash ?? DistinctHash(position),
            false,
            content);

    /// <summary>Paarweise weit entfernte Hashes (zufällig, fester Seed).</summary>
    private static ulong DistinctHash(int seed)
    {
        var random = new Random(seed * 7907 + 13);
        return (ulong)random.NextInt64();
    }

    [Fact]
    public void Aus_einer_Serie_von_fuenf_fast_gleichen_wird_genau_eines_gewaehlt()
    {
        const ulong baseHash = 0xF0F0_1234_5678_9ABCUL;
        var list = new List<HighlightCandidate>();
        for (var i = 0; i < 5; i++)
        {
            list.Add(Photo(i, sharpness: i == 3 ? 0.9 : 0.5, time: Start + (i * 8), hash: baseHash ^ (1UL << i)));
        }

        for (var i = 5; i < 25; i++)
        {
            list.Add(Photo(i, sharpness: 0.2, exposure: 0.4));
        }

        var picks = HighlightPlanner.Select(list, new HighlightOptions(Share: 1, Min: 1, Max: 100), 10);

        var series = picks.Where(p => p.FileId is >= 1000 and < 1005).ToList();
        series.Should().ContainSingle().Which.FileId.Should().Be(1003, "das schärfste Foto der Serie");
        series[0].SimilarCount.Should().Be(4);
        series[0].Reason.Should().StartWith("beste aus einer Serie von 5");
        picks.Should().HaveCount(21, "eine Gruppe für die Serie plus 20 Einzelfotos");
    }

    [Fact]
    public void Intervallaufnahmen_werden_keine_endlose_Serie()
    {
        // eine Stunde lang alle 30 s ein Foto, jedes dem vorigen sehr ähnlich (langsam wandernde Szene)
        var list = new List<HighlightCandidate>();
        var hash = 0xAAAA_5555_AAAA_5555UL;
        for (var i = 0; i < 120; i++)
        {
            hash ^= 1UL << (i % 64);
            list.Add(Photo(i, time: Start + (i * 30), hash: hash));
        }

        var picks = HighlightPlanner.Select(list, new HighlightOptions(Share: 1, Min: 1, Max: 200), 10);

        picks.Count.Should().BeGreaterThanOrEqualTo(11, "eine Serie umfasst höchstens fünf Minuten (11 Fotos)");
        picks.Should().OnlyContain(p => p.SimilarCount <= HighlightPlanner.SeriesSpanSeconds / 30);
    }

    [Fact]
    public void Byte_gleiche_Kopie_in_einer_Serie_zaehlt_als_eine_Aufnahme()
    {
        const ulong hash = 0x0F0F_0F0F_0F0F_0F0FUL;
        var content = new byte[32];
        content[3] = 9;
        var list = new List<HighlightCandidate>
        {
            Photo(0, time: Start, hash: hash, content: content),
            Photo(1, time: Start + 1, hash: hash, content: content),
            Photo(2, time: Start + 5, hash: hash ^ 3),
        };

        var pick = HighlightPlanner.Select(list, new HighlightOptions(Share: 1, Min: 1, Max: 10), 10).Should().ContainSingle().Subject;

        pick.SimilarCount.Should().Be(2);
        pick.Reason.Should().StartWith("beste aus einer Serie von 2");
    }

    [Fact]
    public void Aehnliche_Fotos_mit_grossem_Zeitabstand_sind_keine_Serie_gleicher_Inhalt_immer()
    {
        const ulong hash = 0x1111_2222_3333_4444UL;
        var content = new byte[32];
        content[0] = 7;
        var list = new List<HighlightCandidate>
        {
            Photo(0, time: Start, hash: hash),
            Photo(1, time: Start + 3600, hash: hash ^ 1),
            Photo(2, time: Start + 7200, content: content),
            Photo(3, time: Start + 90_000, content: content),
        };

        var picks = HighlightPlanner.Select(list, new HighlightOptions(Share: 1, Min: 1, Max: 100), 10);

        picks.Select(p => p.FileId).Should().BeEquivalentTo([1000, 1001, 1002], "eine Stunde Abstand ist keine Serie, die byte-gleiche Kopie ist eine Datei");
        picks.Single(p => p.FileId == 1002).SimilarCount.Should().Be(1);
        picks.Single(p => p.FileId == 1002).Reason.Should().StartWith("beste von 2 gleichen");
    }

    [Fact]
    public void Favorit_und_bestaetigte_Personen_heben_den_Wert()
    {
        var plain = Photo(0);
        var favorite = Photo(1, favorite: true);
        var people = Photo(2, faces: 2);
        var both = Photo(3, favorite: true, faces: 3, rating: 5);
        var picks = HighlightPlanner.Select([plain, favorite, people, both], new HighlightOptions(Share: 1, Min: 4, Max: 4), 10);

        var score = picks.ToDictionary(p => p.FileId, p => p.Score);
        score[1001].Should().BeGreaterThan(score[1000]);
        score[1002].Should().BeGreaterThan(score[1000]);
        score[1003].Should().BeGreaterThan(score[1001]).And.BeGreaterThan(score[1002]);
        picks.Single(p => p.FileId == 1003).Reason.Should().Be("3 Personen · Favorit · 5 Sterne");

        var one = HighlightPlanner.Select([plain, favorite], new HighlightOptions(Share: 0, Min: 1, Max: 1), 10);
        one.Should().ContainSingle().Which.FileId.Should().Be(1001);
    }

    [Theory]
    [InlineData(100, 15)]
    [InlineData(10, 6)]
    [InlineData(1000, 80)]
    [InlineData(3, 3)]
    public void Anteil_Min_und_Max_werden_eingehalten(int files, int expected)
    {
        var list = Enumerable.Range(0, files).Select(i => Photo(i, sharpness: (i % 10) / 10.0)).ToList();

        var picks = HighlightPlanner.Select(list, new HighlightOptions(), 10);

        picks.Should().HaveCount(expected);
    }

    [Fact]
    public void Reihenfolge_ist_die_Eingabe_Reihenfolge()
    {
        // Eingabe absichtlich nicht nach FileId sortiert; die besten liegen verstreut.
        var list = Enumerable.Range(0, 40).Select(i => Photo(i, sharpness: (i * 37 % 40) / 40.0)).Reverse()
            .Select((c, k) => c with { Position = k }).ToList();

        var picks = HighlightPlanner.Select(list, new HighlightOptions(Share: 0.25, Min: 1, Max: 100), 10);

        var positions = picks.Select(p => list.First(c => c.FileId == p.FileId).Position).ToList();
        positions.Should().BeInAscendingOrder();
        picks.Should().HaveCount(10);
    }

    [Fact]
    public void Leere_Liste_liefert_nichts()
    {
        HighlightPlanner.Select([], new HighlightOptions(), 10).Should().BeEmpty();
        HighlightPlanner.PickCover([]).Should().BeNull();
    }

    [Fact]
    public void Nicht_alle_Highlights_aus_derselben_Stunde()
    {
        // 100 sehr gute Fotos in Stunde A, 100 mittlere in Stunde B (je 30 s Abstand, alle verschiedene Motive)
        var list = new List<HighlightCandidate>();
        for (var i = 0; i < 100; i++)
        {
            list.Add(Photo(i, sharpness: 0.95, exposure: 0.95, time: Start + (i * 30), favorite: true));
        }

        for (var i = 100; i < 200; i++)
        {
            list.Add(Photo(i, sharpness: 0.4, exposure: 0.5, time: Start + 7200 + ((i - 100) * 30)));
        }

        var picks = HighlightPlanner.Select(list, new HighlightOptions(Share: 0.1, Min: 1, Max: 100), 10);

        picks.Should().HaveCount(20);
        picks.Count(p => p.FileId < 1100).Should().Be(10, "Stunde A hat die Hälfte der Fotos, also höchstens die Hälfte der Highlights");
        picks.Count(p => p.FileId >= 1100).Should().Be(10);
    }

    [Fact]
    public void Titelbild_ist_nie_das_unschaerfste_oder_dunkelste_wenn_es_Alternativen_gibt()
    {
        var plainGood = Photo(0, sharpness: 0.8, exposure: 0.8);
        var blurryStar = Photo(1, sharpness: 0.1, exposure: 0.9, favorite: true, faces: 3, rating: 5, aesthetic: 0.9);
        var darkStar = Photo(2, sharpness: 0.9, exposure: 0.05, favorite: true, faces: 3, rating: 5, aesthetic: 0.9);

        HighlightPlanner.PickCover([plainGood, blurryStar, darkStar]).Should().Be(1000);
        HighlightPlanner.PickCover([blurryStar, darkStar]).Should().NotBeNull();

        // alle mäßig: dann wenigstens nicht das schlechteste
        var a = Photo(0, sharpness: 0.30, exposure: 0.40, favorite: true, faces: 2);
        var b = Photo(1, sharpness: 0.20, exposure: 0.42);
        var c = Photo(2, sharpness: 0.33, exposure: 0.30, favorite: true, faces: 3, rating: 5);
        HighlightPlanner.PickCover([a, b, c]).Should().Be(1000, "b ist das unschärfste, c das dunkelste");

        HighlightPlanner.PickCover([b]).Should().Be(1001, "ohne Alternative");
    }

    [Fact]
    public void Titelbild_lieber_Foto_als_Video()
    {
        var video = Photo(0, sharpness: 0.9, exposure: 0.9, video: true, favorite: true);
        var photo = Photo(1, sharpness: 0.7, exposure: 0.7);
        HighlightPlanner.PickCover([video, photo]).Should().Be(1001);
        HighlightPlanner.PickCover([video]).Should().Be(1000);
    }

    [Fact]
    public void Ohne_Bildwirkung_und_ohne_Bewertung_funktioniert_die_Auswahl()
    {
        var list = Enumerable.Range(0, 30).Select(i => Photo(i, unrated: i % 2 == 0, sharpness: i / 30.0)).ToList();

        var picks = HighlightPlanner.Select(list, new HighlightOptions(), 10);

        picks.Should().HaveCount(6);
        picks.Should().OnlyContain(p => !double.IsNaN(p.Score) && p.Reason.Length > 0);
        HighlightPlanner.PickCover(list).Should().NotBeNull();
    }

    [Fact]
    public void Bildwirkung_zaehlt_wenn_vorhanden()
    {
        var weak = Photo(0, aesthetic: 0.1);
        var strong = Photo(1, aesthetic: 0.95);
        var picks = HighlightPlanner.Select([weak, strong], new HighlightOptions(Share: 1, Min: 2, Max: 2), 10);
        picks.Single(p => p.FileId == 1001).Score.Should().BeGreaterThan(picks.Single(p => p.FileId == 1000).Score);
        picks.Single(p => p.FileId == 1001).Reason.Should().Contain("starke Bildwirkung");
    }

    [Fact]
    public void Videos_sind_leicht_bevorzugt()
    {
        var photo = Photo(0);
        var video = Photo(1, video: true);
        var picks = HighlightPlanner.Select([photo, video], new HighlightOptions(Share: 1, Min: 2, Max: 2), 10);
        picks.Single(p => p.FileId == 1001).Score.Should().BeGreaterThan(picks.Single(p => p.FileId == 1000).Score);
        picks.Single(p => p.FileId == 1001).Reason.Should().Contain("Video");
    }
}
