using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Domain.Entities;
using Lumora.Domain.Enums;
using Lumora.Services.Motifs;
using Lumora.Services.Quality;
using Lumora.Tests.Duplicates;
using Lumora.Tests.Motifs;

namespace Lumora.Tests.Quality;

/// <summary>
/// Hintergrund-Index der Foto-Güte (ADR-0057): neue Dateien werden bewertet, geänderte neu, gesperrter Katalog und
/// belegte Bibliothek werden abgewartet, Videos nur über ihr Vorschaubild, Bildwirkung nur mit Motiv-Vektor, nur im Bereich.
/// </summary>
public sealed class FileQualityIndexTests : IAsyncLifetime
{
    private DuplicateWorld _w = null!;
    private readonly List<FileQualityIndexService> _services = [];

    public async Task InitializeAsync() => _w = await DuplicateWorld.CreateAsync(inbox: false);

    public async Task DisposeAsync()
    {
        foreach (var service in _services)
        {
            service.Dispose();
        }

        await _w.DisposeAsync();
    }

    private FileQualityIndexService Create(IQualityAesthetics? aesthetics = null, FixedLibraryScope? scope = null)
    {
        var service = new FileQualityIndexService(
            _w.Fx.ContextFactory,
            NullLogger<FileQualityIndexService>.Instance,
            _w.Loader,
            scope,
            aesthetics,
            _w.Gate,
            _w.Fx.Messenger)
        { StartDelay = Timeout.InfiniteTimeSpan };
        _services.Add(service);
        return service;
    }

    private double? Value(string column, string name)
    {
        using var connection = _w.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT q.{column} FROM FileQuality q JOIN Files f ON f.Id = q.FileId WHERE f.Name = $n";
        cmd.Parameters.AddWithValue("$n", name);
        return cmd.ExecuteScalar() is double value ? value : null;
    }

    [Fact]
    public async Task Neue_Dateien_werden_bewertet_und_ein_zweiter_Durchgang_hat_nichts_zu_tun()
    {
        var sharp = QualityImages.Detailed(1, 256, 192);
        _w.Loader.Add("scharf.jpg", sharp).Add("weich.jpg", QualityImages.Blur(sharp, 4)).Add("dunkel.jpg", QualityImages.Scale(sharp, 0.08));
        _w.Write("a/scharf.jpg", DuplicateWorld.Content(1));
        _w.Write("a/weich.jpg", DuplicateWorld.Content(2));
        _w.Write("a/dunkel.jpg", DuplicateWorld.Content(3));
        _w.Write("a/kaputt.jpg", DuplicateWorld.Content(4));
        await _w.ScanAsync();
        var index = Create();
        var statuses = new List<FileQualityIndexStatus>();
        _w.Fx.Messenger.Register<FileQualityIndexStatusMessage>(this, (_, m) => statuses.Add(m.Status));
        var changed = new List<FileQualityChangedMessage>();
        _w.Fx.Messenger.Register<FileQualityChangedMessage>(this, (_, m) => changed.Add(m));

        var report = await index.RunPassAsync();

        report.Rated.Should().Be(3);
        report.Failed.Should().Be(1);
        _w.Scalar<string>("SELECT q.ErrorCode FROM FileQuality q JOIN Files f ON f.Id = q.FileId WHERE f.Name = 'kaputt.jpg'").Should().Be(FileQualityIndexService.DecodeError);
        Value("Sharpness", "scharf.jpg").Should().BeGreaterThan(Value("Sharpness", "weich.jpg")!.Value);
        Value("Exposure", "scharf.jpg").Should().BeGreaterThan(Value("Exposure", "dunkel.jpg")!.Value);
        Value("Aesthetic", "scharf.jpg").Should().BeNull("ohne Motiv-Modell keine Bildwirkung");
        statuses.Should().Contain(s => s.IsRunning && s.Phase == FileQualityIndexStatus.PixelPhase && s.Total == 4);
        statuses[^1].Should().Be(FileQualityIndexStatus.Idle);
        changed.Should().ContainSingle().Which.LibraryIds.Should().Equal(_w.Library.Id);

        var again = await index.RunPassAsync();
        again.Rated.Should().Be(0);
        again.Failed.Should().Be(0, "nicht dekodierbar ist gemerkt — mit derselben Version kein neuer Versuch");
    }

    [Fact]
    public async Task Gesperrte_Datei_wird_nicht_als_kaputt_gemerkt()
    {
        _w.Write("a/gesperrt.jpg", DuplicateWorld.Content(1));
        await _w.ScanAsync();
        var locking = new LockingLoader(_w.Loader) { Locked = true };
        var index = new FileQualityIndexService(_w.Fx.ContextFactory, NullLogger<FileQualityIndexService>.Instance, locking, null, null, _w.Gate, _w.Fx.Messenger)
        {
            StartDelay = Timeout.InfiniteTimeSpan,
        };
        _services.Add(index);

        var first = await index.RunPassAsync();
        first.Skipped.Should().Be(1);
        first.Failed.Should().Be(0);
        _w.Scalar<int>("SELECT COUNT(*) FROM FileQuality").Should().Be(0, "eine gesperrte Datei ist nicht kaputt");

        locking.Locked = false;
        (await index.RunPassAsync()).Rated.Should().Be(1, "der nächste Durchgang versucht es wieder");
    }

    [Fact]
    public async Task Defektes_Sprachmodell_laesst_die_Bildwirkung_leer()
    {
        var encoders = new BrokenTextEncoderFactory();
        var aesthetics = new QualityAesthetics(encoders, _w.Fx.ContextFactory);

        (await aesthetics.GetModelAsync()).Should().BeNull();
        (await aesthetics.GetModelAsync()).Should().BeNull();
        encoders.Attempts.Should().Be(1, "kein neuer Versuch mit demselben Modell in dieser Sitzung");
    }

    [Fact]
    public async Task Geaenderte_Datei_wird_neu_bewertet()
    {
        var sharp = QualityImages.Detailed(2, 256, 192);
        _w.Loader.Add("foto.jpg", sharp);
        _w.Write("a/foto.jpg", DuplicateWorld.Content(1));
        await _w.ScanAsync();
        var index = Create();
        await index.RunPassAsync();
        var before = Value("Sharpness", "foto.jpg");

        _w.Loader.Add("foto.jpg", QualityImages.Blur(sharp, 5));
        _w.Exec("UPDATE Files SET ModifiedAtUtc = ModifiedAtUtc + 60 WHERE Name = 'foto.jpg'"); // wie nach einem Incremental-Scan

        (await index.RunPassAsync()).Rated.Should().Be(1);
        Value("Sharpness", "foto.jpg").Should().BeLessThan(before!.Value);

        _w.Exec("UPDATE Files SET Size = Size + 1 WHERE Name = 'foto.jpg'");
        (await index.RunPassAsync()).Rated.Should().Be(1, "auch eine andere Größe macht den Wert ungültig");
    }

    [Fact]
    public async Task Durchgang_ruht_solange_die_Bibliothek_belegt_ist()
    {
        _w.Write("a/1.jpg", DuplicateWorld.Content(1));
        await _w.ScanAsync();
        var index = Create();
        var lease = await _w.Gate.AcquireAsync(_w.Library.Id, CancellationToken.None);
        var pass = index.RunPassAsync();
        await Task.Delay(600);
        pass.IsCompleted.Should().BeFalse("Scan bzw. Datei-Batch belegt die Bibliothek");

        lease.Dispose();
        (await pass.WaitAsync(TimeSpan.FromSeconds(15))).Rated.Should().Be(1);
    }

    [Fact]
    public async Task Gesperrter_Katalog_wird_abgewartet()
    {
        _w.Write("a/1.jpg", DuplicateWorld.Content(1));
        await _w.ScanAsync();
        var index = Create();
        var suspension = _w.Fx.ContextFactory.Suspend("Test-Umzug");
        var pass = index.RunPassAsync();
        await Task.Delay(600);
        pass.IsCompleted.Should().BeFalse("die Katalog-DB zieht gerade um");

        suspension.Dispose();
        (await pass.WaitAsync(TimeSpan.FromSeconds(15))).Rated.Should().Be(1);
    }

    [Fact]
    public async Task Abbrechen_beendet_den_Durchgang_und_das_Erreichte_bleibt()
    {
        _w.Write("a/1.jpg", DuplicateWorld.Content(1));
        await _w.ScanAsync();
        var index = Create();
        var lease = await _w.Gate.AcquireAsync(_w.Library.Id, CancellationToken.None);
        var pass = index.RunPassAsync();
        await Task.Delay(300);

        index.CancelPass();

        var act = async () => await pass.WaitAsync(TimeSpan.FromSeconds(10));
        await act.Should().ThrowAsync<OperationCanceledException>();
        lease.Dispose();
        (await index.RunPassAsync()).Rated.Should().Be(1, "der nächste Durchgang holt es nach");
    }

    [Fact]
    public async Task Videos_nur_ueber_ihr_Vorschaubild()
    {
        _w.Write("v/film.mp4", DuplicateWorld.Content(1, 8000));
        await _w.ScanAsync();
        var index = Create();

        (await index.RunPassAsync()).Rated.Should().Be(0, "ohne Vorschaubild keine Bewertung");
        _w.Loader.Loaded.Should().BeEmpty("das Video selbst wird nie geöffnet");

        var id = await _w.IdAsync("film.mp4");
        var thumb = Path.Combine(_w.Fx.RootDir, "thumb_film.jpg");
        File.WriteAllBytes(thumb, DuplicateWorld.Content(2));
        _w.Exec(
            "INSERT INTO Thumbnails (FileId, Size, Kind, Format, CachePath, SourceModifiedAtUtc, GeneratedAtUtc, ByteSize, Status, AttemptCount) VALUES ($id, 400, 0, 'jpg', $p, 0, 0, 10, 2, 1)",
            ("$id", id), ("$p", thumb));

        (await index.RunPassAsync()).Rated.Should().Be(1);
        _w.Loader.Loaded.Should().ContainSingle().Which.Should().Be(thumb);
    }

    [Fact]
    public async Task Bildwirkung_nur_mit_Motiv_Vektor_und_ohne_Modell_bleibt_sie_leer()
    {
        _w.Write("a/schoen.jpg", DuplicateWorld.Content(1));
        _w.Write("a/missglueckt.jpg", DuplicateWorld.Content(2));
        _w.Write("a/ohne-vektor.jpg", DuplicateWorld.Content(3));
        await _w.ScanAsync();
        InsertEmbedding(await _w.IdAsync("schoen.jpg"), FakeMotifEncoderFactory.Unit(1));
        InsertEmbedding(await _w.IdAsync("missglueckt.jpg"), FakeMotifEncoderFactory.Unit(2));

        var withoutModel = new FakeQualityAesthetics { Available = false };
        var index = Create(withoutModel);
        var first = await index.RunPassAsync();
        first.Rated.Should().Be(3);
        first.Aesthetic.Should().Be(0);
        Value("Aesthetic", "schoen.jpg").Should().BeNull("ohne Modell bleibt die Bildwirkung NULL");

        var aesthetics = new FakeQualityAesthetics();
        var withModel = Create(aesthetics);
        (await withModel.RunPassAsync()).Aesthetic.Should().Be(2);
        Value("Aesthetic", "schoen.jpg").Should().BeGreaterThan(0.95);
        Value("Aesthetic", "missglueckt.jpg").Should().BeLessThan(0.05);
        Value("Aesthetic", "ohne-vektor.jpg").Should().BeNull();
        _w.Scalar<string>("SELECT q.AestheticVersion FROM FileQuality q JOIN Files f ON f.Id = q.FileId WHERE f.Name = 'schoen.jpg'")
            .Should().Be(AestheticPrompts.VersionFor("fake-motif/v1"));

        var calls = aesthetics.Calls;
        (await withModel.RunPassAsync()).Aesthetic.Should().Be(0);
        aesthetics.Calls.Should().Be(calls, "nichts offen → Text-Vektoren werden gar nicht erst geholt");

        _w.Exec("UPDATE Files SET ModifiedAtUtc = ModifiedAtUtc + 5 WHERE Name = 'schoen.jpg'");
        var report = await withModel.RunPassAsync();
        report.Rated.Should().Be(1);
        report.Aesthetic.Should().Be(1, "neu bewertete Dateien bekommen ihre Bildwirkung im selben Durchgang");
    }

    [Fact]
    public async Task Nur_Dateien_des_Bereichs()
    {
        _w.Write("a/hier.jpg", DuplicateWorld.Content(1));
        await _w.ScanAsync();
        var otherRoot = Path.Combine(_w.Fx.RootDir, "zweite");
        Directory.CreateDirectory(otherRoot);
        File.WriteAllBytes(Path.Combine(otherRoot, "dort.jpg"), DuplicateWorld.Content(2));
        int other;
        await using (var ctx = _w.Fx.CreateContext())
        {
            var library = new Library { Name = "Zweite", RootPath = otherRoot, CreatedAt = DateTime.UtcNow, LastOpenedAt = DateTime.UtcNow };
            ctx.Libraries.Add(library);
            await ctx.SaveChangesAsync();
            ctx.LibraryRoots.Add(new LibraryRoot { LibraryId = library.Id, Path = otherRoot, Role = LibraryRootRole.Archive, Name = "zweite", SortOrder = 0 });
            await ctx.SaveChangesAsync();
            other = library.Id;
        }

        await _w.Fx.ScanAsync(other);
        var scope = new FixedLibraryScope(_w.Library.Id);
        var index = Create(scope: scope);

        (await index.RunPassAsync()).Rated.Should().Be(1);
        Value("Sharpness", "dort.jpg").Should().BeNull("andere Bibliothek, nicht im Bereich");

        scope.LibraryIds = [_w.Library.Id, other]; // „Alle verknüpften“
        (await index.RunPassAsync()).Rated.Should().Be(1);

        scope.LibraryIds = [];
        (await index.RunPassAsync()).Should().Be(new FileQualityIndexReport(0, 0, 0, 0));
    }

    [Fact]
    public async Task Neue_Dateien_stossen_einen_Durchgang_an()
    {
        _w.Write("a/1.jpg", DuplicateWorld.Content(1));
        await _w.ScanAsync();
        var index = new FileQualityIndexService(_w.Fx.ContextFactory, NullLogger<FileQualityIndexService>.Instance, _w.Loader, null, null, _w.Gate, _w.Fx.Messenger)
        {
            StartDelay = TimeSpan.FromMilliseconds(50),
        };
        _services.Add(index);
        var done = new TaskCompletionSource();
        _w.Fx.Messenger.Register<FileQualityChangedMessage>(this, (_, _) => done.TrySetResult());

        index.Start();

        await done.Task.WaitAsync(TimeSpan.FromSeconds(15));
        _w.Scalar<int>("SELECT COUNT(*) FROM FileQuality").Should().Be(1);
    }

    [Fact]
    public async Task Text_Vektoren_werden_einmal_gerechnet_und_im_Katalog_gemerkt()
    {
        var encoders = new FakeMotifEncoderFactory();
        var first = new QualityAesthetics(encoders, _w.Fx.ContextFactory);
        var model = await first.GetModelAsync();
        model.Should().NotBeNull();
        model!.Version.Should().Be(AestheticPrompts.VersionFor(encoders.ModelVersion));
        (await first.GetModelAsync()).Should().BeSameAs(model);
        first.Encodings.Should().Be(1);

        var second = new QualityAesthetics(encoders, _w.Fx.ContextFactory);
        (await second.GetModelAsync())!.Positive.Should().Equal(model.Positive);
        second.Encodings.Should().Be(0, "aus der Settings-Tabelle");

        encoders.ModelVersion = "fake-motif/v2";
        (await second.GetModelAsync())!.EmbeddingModelVersion.Should().Be("fake-motif/v2");
        second.Encodings.Should().Be(1, "anderes Modell → neu rechnen");

        encoders.IsAvailable = false;
        encoders.ModelVersion = "fake-motif/v3";
        (await new QualityAesthetics(encoders, _w.Fx.ContextFactory).GetModelAsync()).Should().BeNull("ohne Modell und ohne passenden Zwischenspeicher");
    }

    /// <summary>Lader, der wie bei einer gesperrten Datei eine <see cref="IOException"/> wirft.</summary>
    private sealed class LockingLoader(Lumora.Services.Faces.IFaceImageLoader inner) : Lumora.Services.Faces.IFaceImageLoader
    {
        public bool Locked { get; set; }

        public Lumora.Services.Faces.FaceImage Load(string path, int maxEdge)
            => Locked ? throw new IOException("Datei wird von einem anderen Prozess verwendet.") : inner.Load(path, maxEdge);
    }

    /// <summary>Modell „vorhanden“, aber das Sprachmodell lässt sich nicht laden.</summary>
    private sealed class BrokenTextEncoderFactory : IMotifEncoderFactory
    {
        public int Attempts { get; private set; }

        public string ModelVersion => "kaputt/v1";

        public bool IsAvailable => true;

        public MotifScoring Scoring => MotifScoring.Siglip2Base;

        public IMotifImageEncoder CreateImageEncoder(bool useGpu) => throw new NotSupportedException();

        public IMotifTextEncoder CreateTextEncoder()
        {
            Attempts++;
            throw new InvalidDataException("Sprachmodell defekt");
        }
    }

    private void InsertEmbedding(int fileId, float[] vector)
        => _w.Exec(
            "INSERT INTO FileEmbeddings (FileId, ModelVersion, ErrorCode, CreatedAtUtc, Embedding) VALUES ($id, 'fake-motif/v1', NULL, 0, $e)",
            ("$id", fileId), ("$e", MotifVectors.EncodeHalf(vector)));
}
