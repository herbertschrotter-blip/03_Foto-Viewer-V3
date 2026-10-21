using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoViewerV3.Domain.Entities;
using PhotoViewerV3.Services.Faces.Indexing;
using PhotoViewerV3.Services.Infrastructure;
using PhotoViewerV3.Services.Motifs;
using PhotoViewerV3.Services.Motifs.Models;
using PhotoViewerV3.Services.Scanning;
using PhotoViewerV3.Tests.Faces;
using PhotoViewerV3.Tests.Fixtures;

namespace PhotoViewerV3.Tests.Motifs;

/// <summary>
/// Hintergrund-Index der Motiv-Erkennung mit Fake-Encoder (ADR-0052): Vektoren und Treffer, Videos nur mit
/// Vorschaubild, Pause/Fortsetzen, Scan-Sperre, gemeinsame GPU-Sperre mit der Gesichtserkennung, aus/kein Modell,
/// neue Dateien, Neu analysieren, Schwelle ändern ohne neue Analyse, Bibliotheks-Trennung.
/// </summary>
public sealed class MotifIndexServiceTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private static MotifIndexOptions Options => new()
    {
        StartDelay = TimeSpan.Zero,
        WriteBatchSize = 2,
        ReadBatchSize = 3,
        StatusInterval = TimeSpan.Zero,
        SuspendedRetryDelay = TimeSpan.FromMilliseconds(200),
        ScanBusyDelay = TimeSpan.FromMilliseconds(50),
    };

    [Fact]
    public async Task Analysiert_alle_Fotos_schreibt_Vektoren_und_Treffer_Videos_nur_mit_Vorschaubild()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var lib = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile("a.jpg");          // Hund
        fx.WriteLibraryFile("p2_katze.jpg");   // Katze
        fx.WriteLibraryFile("sub\\p3_s.jpg");  // Strand
        fx.WriteLibraryFile("p9_leer.jpg");    // nichts
        fx.WriteLibraryFile("film.mp4");       // ohne Vorschaubild: nicht analysierbar
        fx.WriteLibraryFile("clip.mp4");       // mit Vorschaubild (Katze)
        await fx.ScanAsync(lib.Id);
        var clip = (await fx.FindFileAsync("clip.mp4"))!;
        var thumb = fx.WriteLibraryFile("..\\thumbs\\p2_clip.jpg");
        Exec(fx, $"INSERT INTO Thumbnails (FileId, Size, Kind, Format, CachePath, SourceModifiedAtUtc, GeneratedAtUtc, ByteSize, Status, AttemptCount) VALUES ({clip.Id}, 400, 0, 'jpg', '{thumb.Replace("'", "''")}', 0, 0, 10, 2, 1);");

        var (service, factory, _) = Create(fx);
        using (service)
        {
            await service.StartAsync();
            await WaitForIdleAsync(service, total: 5);
        }

        Scalar(fx, "SELECT COUNT(*) FROM FileEmbeddings WHERE ModelVersion = 'fake-motif/v1' AND length(Embedding) = 1536").Should().Be(5);
        Hits(fx, "Hund").Should().Equal("a.jpg");
        Hits(fx, "Katze").Should().BeEquivalentTo("clip.mp4", "p2_katze.jpg");
        Hits(fx, "Strand").Should().Equal("p3_s.jpg");
        Scalar(fx, "SELECT COUNT(*) FROM FileMotifs").Should().Be(4, "das leere Foto und alle übrigen Begriffe bleiben ohne Treffer");
        Scalar(fx, "SELECT COUNT(*) FROM MotifTerms WHERE LibraryId = " + lib.Id).Should().Be(MotifDefaults.Terms.Count, "Standard-Liste beim ersten Durchgang angelegt");
        Scalar(fx, "SELECT COUNT(*) FROM MotifTerms WHERE TextEmbedding IS NOT NULL AND TextModelVersion = 'fake-motif/v1'").Should().Be(MotifDefaults.Terms.Count);
        factory.MaxConcurrent.Should().Be(1);
    }

    [Fact]
    public async Task Pause_haelt_an_und_Fortsetzen_macht_weiter()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var lib = await fx.CreateLibraryAsync();
        for (var i = 0; i < 6; i++)
        {
            fx.WriteLibraryFile($"f{i}.jpg");
        }

        await fx.ScanAsync(lib.Id);
        var (service, factory, _) = Create(fx, gate: new SemaphoreSlim(0));
        using (service)
        {
            await service.StartAsync();
            factory.Gate!.Release(2);
            await WaitUntilAsync(() => factory.Calls == 2, "zwei Fotos");
            service.Pause();
            factory.Gate.Release(10);
            await WaitUntilAsync(() => service.Status.State == MotifIndexState.Paused, "pausiert");
            await Task.Delay(300);
            var whilePaused = factory.Calls;
            await Task.Delay(300);
            factory.Calls.Should().Be(whilePaused, "während der Pause beginnt kein weiteres Foto");
            whilePaused.Should().BeLessThan(6);
            service.Resume();
            await WaitForIdleAsync(service, total: 6);
        }

        Scalar(fx, "SELECT COUNT(*) FROM FileEmbeddings").Should().Be(6);
    }

    [Fact]
    public async Task Ruht_solange_ein_Scan_die_Bibliothek_belegt()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var lib = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile("a.jpg");
        fx.WriteLibraryFile("b.jpg");
        await fx.ScanAsync(lib.Id);
        var gate = new ScanGate(fx.Messenger);
        var lease = await gate.AcquireAsync(lib.Id, CancellationToken.None);
        var (service, factory, _) = Create(fx, scanGate: gate);
        using (service)
        {
            await service.StartAsync();
            await WaitUntilAsync(() => service.Status.State == MotifIndexState.Waiting, "wartet auf den Scan");
            await Task.Delay(300);
            factory.Calls.Should().Be(0, "kein Foto, solange der Scan läuft");
            lease.Dispose();
            await WaitForIdleAsync(service, total: 2);
        }
    }

    [Fact]
    public async Task Gpu_Sperre_haelt_Motive_und_Gesichter_auseinander()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var lib = await fx.CreateLibraryAsync();
        for (var i = 0; i < 8; i++)
        {
            fx.WriteLibraryFile($"f{i}.jpg");
        }

        await fx.ScanAsync(lib.Id);
        var gpu = new GpuGate();
        var inside = 0;
        var maxInside = 0;
        void Enter()
        {
            gpu.IsBusy.Should().BeTrue("jede GPU-Inferenz läuft unter der Sperre");
            var now = Interlocked.Increment(ref inside);
            maxInside = Math.Max(maxInside, now);
            Thread.Sleep(15);
            Interlocked.Decrement(ref inside);
        }

        var (motifs, motifFactory, _) = Create(fx, gpuGate: gpu);
        motifFactory.Provider = "DirectML";
        motifFactory.OnEncode = Enter;
        var faceFactory = new GpuFaceAnalyzerFactory(Enter);
        using var faces = new FaceIndexService(
            fx.ContextFactory, fx.CreateLibraryService(), faceFactory, new FakeFaceImageLoader(),
            new MemoryFaceSettingsStore(new FaceIndexSettings(true, true)), NullLogger<FaceIndexService>.Instance, fx.Messenger,
            new FaceIndexOptions { StartDelay = TimeSpan.Zero, StatusInterval = TimeSpan.Zero }, gpu);
        using (motifs)
        {
            await faces.StartAsync();
            await motifs.StartAsync();
            await WaitForIdleAsync(motifs, total: 8);
            await WaitUntilAsync(() => faces.Status is { State: FaceIndexState.Idle, Done: 8 }, "Gesichter fertig");
        }

        maxInside.Should().Be(1, "Gesichter und Motive rechnen nie gleichzeitig auf der Grafikkarte");
        faceFactory.Calls.Should().Be(8);
        motifFactory.Calls.Should().Be(8);
    }

    [Fact]
    public async Task Ohne_Modell_oder_ausgeschaltet_passiert_nichts()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var lib = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile("a.jpg");
        await fx.ScanAsync(lib.Id);
        var (service, factory, settings) = Create(fx);
        factory.IsAvailable = false;
        using (service)
        {
            await service.StartAsync();
            await WaitUntilAsync(() => service.Status.State == MotifIndexState.ModelMissing, "wartet auf das Modell");
            await service.SetEnabledAsync(false);
            await WaitUntilAsync(() => service.Status.State == MotifIndexState.Disabled, "aus");
            settings.Current.Enabled.Should().BeFalse();
            factory.IsAvailable = true;
            service.RequestRun();
            await Task.Delay(300);
            service.Status.State.Should().Be(MotifIndexState.Disabled);
            factory.Calls.Should().Be(0);

            await service.SetEnabledAsync(true);
            await WaitForIdleAsync(service, total: 1);
        }
    }

    [Fact]
    public async Task Neue_Dateien_nach_einem_Scan_und_Neu_analysieren()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var lib = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile("a.jpg");
        await fx.ScanAsync(lib.Id);
        var (service, factory, _) = Create(fx);
        using (service)
        {
            await service.StartAsync();
            await WaitForIdleAsync(service, total: 1);
            fx.WriteLibraryFile("neu\\p2_b.jpg");
            await fx.CreateScanService().ScanAsync(lib.Id, ScanMode.Incremental, null, CancellationToken.None);
            await WaitForIdleAsync(service, total: 2);
            factory.Calls.Should().Be(2, "nur die neue Datei");

            var reset = await service.RebuildAsync();
            reset.Should().Be(2);
            await WaitUntilAsync(() => factory.Calls == 4, "alle neu");
            await WaitForIdleAsync(service, total: 2);
        }

        Hits(fx, "Katze").Should().Equal("p2_b.jpg");
    }

    [Fact]
    public async Task Globale_Schwelle_aendert_die_Treffer_ohne_neue_Analyse()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var lib = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile("a.jpg");
        await fx.ScanAsync(lib.Id);
        var (service, factory, _) = Create(fx);
        using (service)
        {
            await service.StartAsync();
            await WaitForIdleAsync(service, total: 1);
            Scalar(fx, "SELECT COUNT(*) FROM FileMotifs").Should().Be(1);

            // Treffer hat p ≈ 1; alle anderen p ≈ 5e-8 → mit Schwelle 0,0000001 % wären alle Begriffe Treffer
            await service.SetDefaultThresholdAsync(1e-9);
            Scalar(fx, "SELECT COUNT(*) FROM FileMotifs").Should().Be(MotifDefaults.Terms.Count);
            await service.SetDefaultThresholdAsync(0.01);
            Scalar(fx, "SELECT COUNT(*) FROM FileMotifs").Should().Be(1);
            factory.Calls.Should().Be(1, "die Bild-Vektoren bleiben gespeichert");
        }
    }

    [Fact]
    public async Task Rebuild_der_Bibliothek_traegt_Vektoren_und_Treffer_auf_die_neuen_FileIds()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var lib = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile("a.jpg");
        fx.WriteLibraryFile("p2_b.jpg");
        await fx.ScanAsync(lib.Id);
        var (service, factory, _) = Create(fx);
        using (service)
        {
            await service.StartAsync();
            await WaitForIdleAsync(service, total: 2);
        }

        var oldIds = Scalar(fx, "SELECT SUM(FileId) FROM FileEmbeddings");
        await fx.CreateScanService().ScanAsync(lib.Id, ScanMode.ClearAndRescan, null, CancellationToken.None);
        Scalar(fx, "SELECT SUM(FileId) FROM FileEmbeddings").Should().NotBe(oldIds, "neue FileIds");
        Scalar(fx, "SELECT COUNT(*) FROM FileEmbeddings e JOIN Files f ON f.Id = e.FileId").Should().Be(2, "Vektoren übernommen");
        Hits(fx, "Hund").Should().Equal("a.jpg");
        Hits(fx, "Katze").Should().Equal("p2_b.jpg");
        Scalar(fx, "SELECT COUNT(*) FROM FileEmbeddings WHERE FileId < 0").Should().Be(0);
    }

    [Fact]
    public async Task Geaenderte_Datei_verliert_Vektor_und_Treffer_beim_Incremental_Scan()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var lib = await fx.CreateLibraryAsync();
        var path = fx.WriteLibraryFile("a.jpg");
        fx.WriteLibraryFile("b.jpg");
        await fx.ScanAsync(lib.Id);
        var (service, _, _) = Create(fx);
        using (service)
        {
            await service.StartAsync();
            await WaitForIdleAsync(service, total: 2);
        }

        TempLibraryFixture.WriteFile(path, 4096);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));
        await fx.CreateScanService().ScanAsync(lib.Id, ScanMode.Incremental, null, CancellationToken.None);
        Scalar(fx, "SELECT COUNT(*) FROM FileEmbeddings").Should().Be(1, "die geänderte Datei wird neu analysiert");
        Hits(fx, "Hund").Should().Equal("b.jpg");
    }

    [Fact]
    public async Task Begriffe_und_Treffer_bleiben_in_ihrer_Bibliothek()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var a = await fx.CreateLibraryAsync("A");
        fx.WriteLibraryFile("a.jpg");
        await fx.ScanAsync(a.Id);
        var b = await CreateSecondLibraryAsync(fx);
        TempLibraryFixture.WriteFile(Path.Combine(fx.OutsideDir, "x.jpg"));
        await fx.ScanAsync(b.Id);

        var (service, _, _) = Create(fx);
        var terms = new MotifTermService(new MotifStore(fx.ContextFactory), new FakeMotifEncoderFactory(), new MemoryMotifSettingsStore(MotifSettings.Default), messenger: fx.Messenger);
        using (service)
        {
            await service.StartAsync();
            await WaitForIdleAsync(service, total: 1); // nur die aktive Bibliothek (B, zuletzt angelegt)
            var termsB = await terms.GetTermsAsync(b.Id);
            await terms.AddTermAsync(b.Id, "Leuchtturm-B");
            var termsA = await terms.GetTermsAsync(a.Id);
            termsA.Should().NotContain(t => t.Name == "Leuchtturm-B");
            termsA.Single(t => t.Name == "Hund").FileCount.Should().Be(0, "A ist noch nicht analysiert");
            termsB.Single(t => t.Name == "Hund").FileCount.Should().Be(1);
            termsA.Select(t => t.Id).Should().NotIntersectWith(termsB.Select(t => t.Id));
        }

        Scalar(fx, $"SELECT COUNT(*) FROM FileMotifs m JOIN MotifTerms t ON t.Id = m.TermId JOIN Files f ON f.Id = m.FileId WHERE t.LibraryId <> f.LibraryId").Should().Be(0);
    }

    internal static async Task<Library> CreateSecondLibraryAsync(TempLibraryFixture fx)
    {
        await using var ctx = fx.CreateContext();
        var library = new Library { Name = "B", RootPath = fx.OutsideDir, CreatedAt = DateTime.UtcNow, LastOpenedAt = DateTime.UtcNow.AddMinutes(1) };
        ctx.Libraries.Add(library);
        await ctx.SaveChangesAsync();
        ctx.LibraryRoots.Add(new LibraryRoot { LibraryId = library.Id, Path = fx.OutsideDir, Role = Domain.Enums.LibraryRootRole.Archive, Name = "b", SortOrder = 0 });
        await ctx.SaveChangesAsync();
        return library;
    }

    private static (MotifIndexService Service, FakeMotifEncoderFactory Factory, MemoryMotifSettingsStore Settings) Create(
        TempLibraryFixture fx, SemaphoreSlim? gate = null, IScanGate? scanGate = null, IGpuGate? gpuGate = null)
    {
        var factory = new FakeMotifEncoderFactory { Gate = gate };
        var settings = new MemoryMotifSettingsStore(MotifSettings.Default);
        var store = new MotifStore(fx.ContextFactory);
        var terms = new MotifTermService(store, factory, settings, messenger: fx.Messenger);
        var service = new MotifIndexService(
            fx.CreateLibraryService(), factory, new FakeFaceImageLoader(), settings, terms, store,
            NullLogger<MotifIndexService>.Instance, scanGate, gpuGate, fx.Messenger, Options);
        return (service, factory, settings);
    }

    internal static Task WaitForIdleAsync(MotifIndexService service, int total)
        => WaitUntilAsync(
            () => service.Status is { State: MotifIndexState.Idle } s && s.Total == total && s.Done == total,
            $"Index fertig ({total})",
            () => $"{service.Status}");

    internal static async Task WaitUntilAsync(Func<bool> condition, string what, Func<string>? detail = null)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timeout: {what} {detail?.Invoke()}");
            }

            await Task.Delay(20);
        }
    }

    internal static List<string> Hits(TempLibraryFixture fx, string term)
    {
        using var connection = fx.ContextFactory.OpenSqliteConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT f.Name FROM FileMotifs m JOIN MotifTerms t ON t.Id = m.TermId JOIN Files f ON f.Id = m.FileId WHERE t.Name = $t ORDER BY f.Name";
        cmd.Parameters.AddWithValue("$t", term);
        var list = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(reader.GetString(0));
        }

        return list;
    }

    internal static long Scalar(TempLibraryFixture fx, string sql) => FaceIndexServiceTests.Scalar(fx, sql);

    internal static void Exec(TempLibraryFixture fx, string sql)
    {
        using SqliteConnection connection = fx.ContextFactory.OpenSqliteConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Gesichts-Analysator, der sich als DirectML meldet und bei jedem Lauf <paramref name="onAnalyze"/> aufruft.</summary>
    private sealed class GpuFaceAnalyzerFactory(Action onAnalyze) : PhotoViewerV3.Services.Faces.IFaceAnalyzerFactory
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public string ModelVersion => "fake/gpu";

        public bool IsAvailable => true;

        public PhotoViewerV3.Services.Faces.IFaceAnalyzer Create(bool useGpu) => new Analyzer(this, onAnalyze);

        private sealed class Analyzer(GpuFaceAnalyzerFactory owner, Action onAnalyze) : PhotoViewerV3.Services.Faces.IFaceAnalyzer
        {
            public string ModelVersion => "fake/gpu";

            public string ExecutionProvider => "DirectML";

            public PhotoViewerV3.Services.Faces.FaceAnalysis Analyze(PhotoViewerV3.Services.Faces.FaceImage image, CancellationToken ct = default)
            {
                onAnalyze();
                Interlocked.Increment(ref owner._calls);
                return new PhotoViewerV3.Services.Faces.FaceAnalysis(image.Width, image.Height, []);
            }

            public void Dispose()
            {
            }
        }
    }
}
