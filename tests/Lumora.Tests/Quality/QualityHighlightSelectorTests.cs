using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Services.Duplicates;
using Lumora.Services.Quality;
using Lumora.Tests.Duplicates;
using Xunit.Abstractions;

namespace Lumora.Tests.Quality;

/// <summary>
/// Highlight-Auswahl aus dem Katalog (ADR-0057): echte Fingerabdrücke und Güte aus den Hintergrund-Indizes, bestätigte
/// Gesichter, Favorit; Laufzeit für 5 000 Dateien.
/// </summary>
public sealed class QualityHighlightSelectorTests(ITestOutputHelper output) : IAsyncLifetime
{
    private DuplicateWorld _w = null!;
    private FileQualityIndexService _quality = null!;

    public async Task InitializeAsync()
    {
        _w = await DuplicateWorld.CreateAsync(inbox: false);
        _quality = new FileQualityIndexService(_w.Fx.ContextFactory, NullLogger<FileQualityIndexService>.Instance, _w.Loader, null, null, _w.Gate, _w.Fx.Messenger)
        {
            StartDelay = Timeout.InfiniteTimeSpan,
        };
    }

    public async Task DisposeAsync()
    {
        _quality.Dispose();
        await _w.DisposeAsync();
    }

    private QualityHighlightSelector Selector() => new(_w.Fx.ContextFactory, new DuplicateSettingsStore(_w.Fx.ContextFactory));

    private Task<List<int>> IdsInCaptureOrderAsync()
        => Task.FromResult(_w.Ints("SELECT Id FROM Files ORDER BY COALESCE(CapturedAt, ModifiedAtUtc), Id"));

    [Fact]
    public async Task Serie_aus_fuenf_fast_gleichen_Fotos_wird_ein_Highlight_mit_vier_aehnlichen()
    {
        var scene = QualityImages.Detailed(30, 320, 240);
        for (var i = 0; i < 5; i++)
        {
            var image = i == 2 ? scene : QualityImages.Blur(SyntheticImages.Recode(scene, i + 1), 1);
            _w.Loader.Add($"serie{i}.jpg", image);
            _w.Write($"urlaub/serie{i}.jpg", DuplicateWorld.Content(10 + i, 3000 + i));
        }

        for (var i = 0; i < 10; i++)
        {
            _w.Loader.Add($"einzeln{i}.jpg", QualityImages.Detailed(100 + i, 320, 240));
            _w.Write($"urlaub/einzeln{i}.jpg", DuplicateWorld.Content(100 + i, 5000 + i));
        }

        await _w.ScanAsync();
        // Serie: fünf Aufnahmen binnen 20 s; Einzelfotos je 10 min auseinander
        _w.Exec("UPDATE Files SET CapturedAt = 1699999200 + CAST(substr(Name, 6, 1) AS INTEGER) * 5 WHERE Name LIKE 'serie%'");
        _w.Exec("UPDATE Files SET CapturedAt = 1700001000 + CAST(substr(Name, 8, 1) AS INTEGER) * 600 WHERE Name LIKE 'einzeln%'");
        await _w.Index.RunPassAsync(); // pHash (ADR-0053)
        await _quality.RunPassAsync();

        var ids = await IdsInCaptureOrderAsync();
        var picks = await Selector().SelectAsync(ids, new HighlightOptions(Share: 1, Min: 1, Max: 100));

        picks.Should().HaveCount(11);
        var best = await _w.IdAsync("serie2.jpg");
        var fromSeries = picks.Where(p => _w.Scalar<string>("SELECT Name FROM Files WHERE Id = $id", ("$id", p.FileId))!.StartsWith("serie", StringComparison.Ordinal)).ToList();
        fromSeries.Should().ContainSingle().Which.FileId.Should().Be(best, "das unbearbeitete, schärfste Foto der Serie");
        fromSeries[0].SimilarCount.Should().Be(4);
        fromSeries[0].Reason.Should().StartWith("beste aus einer Serie von 5");
        picks.Select(p => ids.IndexOf(p.FileId)).Should().BeInAscendingOrder("Reihenfolge der Eingabe");
    }

    [Fact]
    public async Task Bestaetigte_Personen_und_Favorit_entscheiden_bei_gleicher_Guete()
    {
        var image = QualityImages.Detailed(5, 320, 240);
        foreach (var name in new[] { "a.jpg", "b.jpg", "c.jpg" })
        {
            _w.Loader.Add(name, image);
        }

        _w.Write("x/a.jpg", DuplicateWorld.Content(1, 3000));
        _w.Write("x/b.jpg", DuplicateWorld.Content(2, 3100));
        _w.Write("x/c.jpg", DuplicateWorld.Content(3, 3200));
        await _w.ScanAsync();
        await _quality.RunPassAsync();
        var a = await _w.IdAsync("a.jpg");
        var b = await _w.IdAsync("b.jpg");
        var c = await _w.IdAsync("c.jpg");
        _w.InsertFace(b, 0.1, 0.1, 0.2, 0.2, personId: null, state: 2);
        _w.InsertFace(b, 0.5, 0.1, 0.2, 0.2, personId: null, state: 2);
        _w.InsertFace(a, 0.5, 0.1, 0.2, 0.2, personId: null, state: 0); // unbestätigt zählt nicht
        _w.Exec("UPDATE Files SET IsFavorite = 1 WHERE Id = $id", ("$id", c));

        var picks = await Selector().SelectAsync([a, b, c], new HighlightOptions(Share: 1, Min: 3, Max: 3));

        var score = picks.ToDictionary(p => p.FileId, p => p.Score);
        score[b].Should().BeGreaterThan(score[a]);
        score[c].Should().BeGreaterThan(score[a]);
        picks.Single(p => p.FileId == b).Reason.Should().Contain("2 Personen");
        picks.Single(p => p.FileId == c).Reason.Should().Contain("Favorit");
        (await Selector().SelectAsync([a, b, c], new HighlightOptions(Share: 0, Min: 1, Max: 1))).Single().FileId.Should().NotBe(a);
    }

    [Fact]
    public async Task Leere_und_unbekannte_Listen()
    {
        (await Selector().SelectAsync([], new HighlightOptions())).Should().BeEmpty();
        (await Selector().PickCoverAsync([])).Should().BeNull();
        (await Selector().SelectAsync([999_999], new HighlightOptions())).Should().BeEmpty();
        (await Selector().PickCoverAsync([999_999])).Should().BeNull();
    }

    [Fact]
    public async Task Titelbild_aus_dem_Katalog_ist_weder_unscharf_noch_dunkel()
    {
        var sharp = QualityImages.Detailed(9, 320, 240);
        _w.Loader.Add("gut.jpg", sharp).Add("unscharf.jpg", QualityImages.Blur(sharp, 6)).Add("dunkel.jpg", QualityImages.Scale(sharp, 0.06));
        _w.Write("x/gut.jpg", DuplicateWorld.Content(1, 3000));
        _w.Write("x/unscharf.jpg", DuplicateWorld.Content(2, 3100));
        _w.Write("x/dunkel.jpg", DuplicateWorld.Content(3, 3200));
        await _w.ScanAsync();
        await _quality.RunPassAsync();
        var good = await _w.IdAsync("gut.jpg");
        var blurry = await _w.IdAsync("unscharf.jpg");
        var dark = await _w.IdAsync("dunkel.jpg");
        _w.Exec("UPDATE Files SET IsFavorite = 1, Rating = 5 WHERE Id IN ($a, $b)", ("$a", blurry), ("$b", dark));

        (await Selector().PickCoverAsync([blurry, dark, good])).Should().Be(good);
    }

    [Fact]
    public async Task Auswahl_aus_5000_Dateien_ist_schnell()
    {
        const int count = 5000;
        for (var i = 0; i < count; i++)
        {
            _w.Write($"jahr/{i / 500:00}/f{i:0000}.jpg", DuplicateWorld.Content(i, 64 + (i % 7)));
        }

        await _w.ScanAsync();
        var random = new Random(5);
        _w.Exec($"""
            INSERT INTO FileQuality (FileId, Sharpness, Exposure, Aesthetic, AestheticVersion, Version, SourceSize, SourceModifiedAt, ErrorCode)
            SELECT Id, (Id * 37 % 100) / 100.0, (Id * 53 % 100) / 100.0, (Id * 71 % 100) / 100.0, 'x', {ImageQuality.Version}, Size, ModifiedAtUtc, NULL FROM Files;
            UPDATE Files SET CapturedAt = 1699999200 + Id * 45, IsFavorite = (Id % 23 = 0);
            """);
        using (var connection = _w.Open())
        using (var tx = connection.BeginTransaction())
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO FileFingerprints (FileId, ImageHash, ImageHashVersion, ImageModifiedAt, ImageLowDetail) SELECT Id, $h, 1, ModifiedAtUtc, 0 FROM Files WHERE Id = $id;";
            var id = cmd.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Integer);
            var hash = cmd.Parameters.Add("$h", Microsoft.Data.Sqlite.SqliteType.Integer);
            var series = 0L;
            var k = 0;
            foreach (var fileId in _w.Ints("SELECT Id FROM Files ORDER BY Id"))
            {
                if (k % 4 == 0)
                {
                    series = random.NextInt64(); // Serien zu je 4 ähnlichen, 45 s auseinander
                }

                id.Value = fileId;
                hash.Value = series ^ (1L << (k % 4));
                cmd.ExecuteNonQuery();
                k++;
            }

            tx.Commit();
        }

        foreach (var fileId in _w.Ints("SELECT Id FROM Files WHERE Id % 17 = 0"))
        {
            _w.InsertFace(fileId, 0.1, 0.1, 0.2, 0.2, personId: null, state: 2);
        }

        var ids = await IdsInCaptureOrderAsync();
        ids.Should().HaveCount(count);
        var selector = Selector();
        await selector.SelectAsync(ids.Take(100).ToList(), new HighlightOptions()); // Aufwärmen (JIT, Verbindungs-Pool)

        var runs = new List<double>();
        IReadOnlyList<HighlightPick> picks = [];
        for (var run = 0; run < 5; run++)
        {
            var sw = Stopwatch.StartNew();
            picks = await selector.SelectAsync(ids, new HighlightOptions());
            runs.Add(sw.Elapsed.TotalMilliseconds);
        }

        var cover = Stopwatch.StartNew();
        (await selector.PickCoverAsync(ids)).Should().NotBeNull();
        var coverMs = cover.Elapsed.TotalMilliseconds;

        output.WriteLine($"SelectAsync 5 000 Dateien: Median {runs.Order().ElementAt(2):N0} ms (min {runs.Min():N0}, max {runs.Max():N0}) · PickCoverAsync {coverMs:N0} ms · {picks.Count} Highlights, {picks.Sum(p => p.SimilarCount)} ähnliche weggelassen");
        picks.Should().HaveCount(80);
        picks.Should().OnlyContain(p => p.SimilarCount == 3, "jede Serie zu 4 ähnlichen Fotos binnen 3 min wird ein Highlight");
        runs.Order().ElementAt(2).Should().BeLessThan(1000, "Ziel < 200 ms; großzügige Grenze gegen Wackeln auf belasteten Testrechnern");
    }
}
