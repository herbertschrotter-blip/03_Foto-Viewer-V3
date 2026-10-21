using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Domain.Entities;
using Lumora.Domain.Enums;
using Lumora.Services.Scanning;
using Lumora.Services.Thumbnails;
using Lumora.Tests.Fixtures;

namespace Lumora.Tests.Thumbnails;

/// <summary>
/// ADR-0043: Bildmaße beim Erzeugen nach Files, Übergang von Vorschaubildern älterer Größe (altes zeigen, bis das neue
/// fertig ist, danach Zeile und Datei weg) und die Pflege im Hintergrund (ältere ersetzen, Maße nachtragen,
/// Scan-Sperre, fehlende Quellen).
/// </summary>
public sealed class ThumbnailUpgradeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private static readonly int LegacySize = ThumbnailSizing.LegacySizes[0];

    private static ThumbnailService CreateService(TempLibraryFixture fx, FakeImageRenderer image, IScanGate? gate = null)
        => new(fx.ContextFactory, fx.Paths, image, new FakeVideoRenderer(), NullLogger<ThumbnailService>.Instance, null, gate)
        {
            MaintenanceStartDelay = System.Threading.Timeout.InfiniteTimeSpan,
        };

    private static async Task WaitForIdleAsync(ThumbnailService svc)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (svc.PendingCount > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        svc.PendingCount.Should().Be(0);
    }

    /// <summary>Bibliothek mit gescannten Dateien a.jpg, b.jpg, … — liefert die Files-Zeilen in Namensreihenfolge.</summary>
    private static async Task<(int LibraryId, List<MediaFile> Files)> CreateLibraryAsync(TempLibraryFixture fx, int count)
    {
        var library = await fx.CreateLibraryAsync();
        for (var i = 0; i < count; i++)
        {
            fx.WriteLibraryFile($"{(char)('a' + i)}.jpg");
        }

        await fx.ScanAsync(library.Id);
        await using var ctx = fx.CreateContext();
        var files = await ctx.Files.AsNoTracking().OrderBy(f => f.Name).ToListAsync();
        files.Should().HaveCount(count);
        return (library.Id, files);
    }

    private static string SourcePath(TempLibraryFixture fx, MediaFile file) => Path.Combine(fx.LibraryRoot, file.Name);

    private static ThumbnailRequest Request(TempLibraryFixture fx, int libraryId, MediaFile file)
        => new(libraryId, file.Id, SourcePath(fx, file), MediaType.Image, file.ModifiedAtUtc);

    private static string ThumbPath(TempLibraryFixture fx, int libraryId, int fileId, int size)
        => Path.Combine(fx.Paths.GetLibraryThumbnailsPath(libraryId), $"{fileId}_{size}_still.jpg");

    private static async Task<string> SeedThumbAsync(TempLibraryFixture fx, int libraryId, MediaFile file, int size)
    {
        var path = ThumbPath(fx, libraryId, file.Id, size);
        TempLibraryFixture.WriteFile(path, 300);
        await using var ctx = fx.CreateContext();
        ctx.Thumbnails.Add(new Thumbnail
        {
            FileId = file.Id,
            Size = size,
            Kind = ThumbnailKind.Still,
            Format = "jpg",
            CachePath = path,
            SourceModifiedAtUtc = file.ModifiedAtUtc,
            GeneratedAtUtc = file.ModifiedAtUtc,
            ByteSize = 300,
            Status = ThumbnailStatus.Generated,
            AttemptCount = 1,
        });
        await ctx.SaveChangesAsync();
        return path;
    }

    private static async Task<List<Thumbnail>> RowsAsync(TempLibraryFixture fx, int fileId)
    {
        await using var ctx = fx.CreateContext();
        return await ctx.Thumbnails.AsNoTracking().Where(t => t.FileId == fileId).OrderBy(t => t.Size).ToListAsync();
    }

    [Fact]
    public async Task Erzeugen_schreibt_Masse_des_Originals_nach_Files()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var (libraryId, files) = await CreateLibraryAsync(fx, 1);
        files[0].Width.Should().BeNull();
        var image = new FakeImageRenderer { SourceSize = (3000, 4000) };
        using var svc = CreateService(fx, image);

        await svc.RequestThumbnailsAsync([Request(fx, libraryId, files[0])]);
        await WaitForIdleAsync(svc);

        var file = await fx.FindFileByIdAsync(files[0].Id);
        (file!.Width, file.Height).Should().Be((3000, 4000));
        (await RowsAsync(fx, files[0].Id)).Should().ContainSingle(t => t.Size == ThumbnailService.Size && t.Status == ThumbnailStatus.Generated);
    }

    [Fact]
    public async Task Renderer_ohne_Masse_laesst_Files_unveraendert()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var (libraryId, files) = await CreateLibraryAsync(fx, 1);
        using var svc = CreateService(fx, new FakeImageRenderer()); // SourceSize (0, 0) wie FFmpeg ohne Eingabe-Beschreibung

        await svc.RequestThumbnailsAsync([Request(fx, libraryId, files[0])]);
        await WaitForIdleAsync(svc);

        (await fx.FindFileByIdAsync(files[0].Id))!.Width.Should().BeNull();
    }

    [Fact]
    public async Task Altes_Vorschaubild_wird_gezeigt_bis_das_neue_fertig_ist_danach_geloescht()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var (libraryId, files) = await CreateLibraryAsync(fx, 1);
        var legacyPath = await SeedThumbAsync(fx, libraryId, files[0], LegacySize);
        var image = new FakeImageRenderer { SourceSize = (640, 480) };
        using var svc = CreateService(fx, image);
        var ready = new List<string>();
        svc.ThumbnailReady += e => { lock (ready) { ready.Add(e.ThumbnailPath); } };

        await svc.RequestThumbnailsAsync([Request(fx, libraryId, files[0])]);
        await WaitForIdleAsync(svc);

        var newPath = ThumbPath(fx, libraryId, files[0].Id, ThumbnailService.Size);
        lock (ready)
        {
            ready.Should().Equal(legacyPath, newPath);
        }

        image.Calls.Should().Be(1);
        File.Exists(newPath).Should().BeTrue();
        File.Exists(legacyPath).Should().BeFalse("die alte Datei wird nach dem Commit gelöscht");
        var rows = await RowsAsync(fx, files[0].Id);
        rows.Should().ContainSingle().Which.Size.Should().Be(ThumbnailService.Size, "die Zeile der alten Größe verschwindet in derselben Transaktion");
        var file = await fx.FindFileByIdAsync(files[0].Id);
        (file!.Width, file.Height).Should().Be((640, 480));

        // Zweite Anfrage: nur noch das neue.
        await svc.RequestThumbnailsAsync([Request(fx, libraryId, files[0])]);
        lock (ready)
        {
            ready.Should().HaveCount(3);
            ready[^1].Should().Be(newPath);
        }
    }

    [Fact]
    public async Task Scheitert_das_neue_bleibt_das_alte_sichtbar_und_erhalten()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var (libraryId, files) = await CreateLibraryAsync(fx, 1);
        var legacyPath = await SeedThumbAsync(fx, libraryId, files[0], LegacySize);
        var image = new FakeImageRenderer { FailCode = "wic_encode_failed" };
        using var svc = CreateService(fx, image);
        var ready = new List<string>();
        svc.ThumbnailReady += e => { lock (ready) { ready.Add(e.ThumbnailPath); } };

        await svc.RequestThumbnailsAsync([Request(fx, libraryId, files[0])]);
        await WaitForIdleAsync(svc);
        await svc.RequestThumbnailsAsync([Request(fx, libraryId, files[0])]); // Fehlschlag bleibt in der Sitzung gesperrt

        lock (ready)
        {
            ready.Should().Equal(legacyPath, legacyPath);
        }

        image.Calls.Should().Be(1);
        File.Exists(legacyPath).Should().BeTrue();
        (await RowsAsync(fx, files[0].Id)).Should().Contain(t => t.Size == LegacySize && t.Status == ThumbnailStatus.Generated);
    }

    [Fact]
    public async Task Altes_Vorschaubild_einer_anderen_Dateiversion_wird_nicht_gezeigt()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var (libraryId, files) = await CreateLibraryAsync(fx, 1);
        await SeedThumbAsync(fx, libraryId, files[0], LegacySize);
        using var svc = CreateService(fx, new FakeImageRenderer());
        var ready = new List<string>();
        svc.ThumbnailReady += e => { lock (ready) { ready.Add(e.ThumbnailPath); } };

        var changed = Request(fx, libraryId, files[0]) with { SourceModifiedAtUtc = files[0].ModifiedAtUtc.AddMinutes(1) };
        await svc.RequestThumbnailsAsync([changed]);
        await WaitForIdleAsync(svc);

        lock (ready)
        {
            ready.Should().Equal(ThumbPath(fx, libraryId, files[0].Id, ThumbnailService.Size));
        }
    }

    [Fact]
    public async Task Pflege_ersetzt_aeltere_Groesse_im_Hintergrund_und_ueberspringt_fehlende_Quellen()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var (libraryId, files) = await CreateLibraryAsync(fx, 3);
        var legacy = new List<string>();
        foreach (var file in files)
        {
            legacy.Add(await SeedThumbAsync(fx, libraryId, file, LegacySize));
        }

        File.Delete(SourcePath(fx, files[2])); // Original weg (noch nicht als „fehlt“ markiert)
        var image = new FakeImageRenderer { SourceSize = (800, 600) };
        using var svc = CreateService(fx, image);
        var ready = new List<ThumbnailReadyEventArgs>();
        svc.ThumbnailReady += e => { lock (ready) { ready.Add(e); } };

        var report = await svc.RunMaintenancePassAsync();
        await WaitForIdleAsync(svc);

        report.UpgradesQueued.Should().Be(2);
        report.UpgradesSkipped.Should().Be(1);
        image.Calls.Should().Be(2);
        lock (ready)
        {
            ready.Select(e => e.ThumbnailPath).Should().BeEquivalentTo(
                [ThumbPath(fx, libraryId, files[0].Id, ThumbnailService.Size), ThumbPath(fx, libraryId, files[1].Id, ThumbnailService.Size)],
                "die Pflege meldet keine alten Vorschaubilder, nur neu erzeugte");
        }

        for (var i = 0; i < 2; i++)
        {
            (await RowsAsync(fx, files[i].Id)).Should().ContainSingle().Which.Size.Should().Be(ThumbnailService.Size);
            File.Exists(legacy[i]).Should().BeFalse();
            (await fx.FindFileByIdAsync(files[i].Id))!.Width.Should().Be(800);
        }

        (await RowsAsync(fx, files[2].Id)).Should().ContainSingle().Which.Size.Should().Be(LegacySize, "ohne Original bleibt das alte");
        File.Exists(legacy[2]).Should().BeTrue();

        // Zweiter Durchgang: nichts mehr zu ersetzen außer der übersprungenen Datei.
        var again = await svc.RunMaintenancePassAsync();
        again.UpgradesQueued.Should().Be(0);
        again.UpgradesSkipped.Should().Be(1);
    }

    [Fact]
    public async Task Pflege_traegt_fehlende_Bildmasse_aus_dem_Dateikopf_nach()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var (libraryId, files) = await CreateLibraryAsync(fx, 3);
        await SeedThumbAsync(fx, libraryId, files[0], ThumbnailService.Size);
        await SeedThumbAsync(fx, libraryId, files[1], ThumbnailService.Size);
        // files[2] hat kein Vorschaubild → wird nicht angefasst (nur Dateien, die schon ein neues Vorschaubild haben)
        await using (var ctx = fx.CreateContext())
        {
            await ctx.Files.Where(f => f.Id == files[1].Id).ExecuteUpdateAsync(u => u.SetProperty(f => f.Width, 10).SetProperty(f => f.Height, 20));
        }

        var image = new FakeImageRenderer { HeaderSize = (4000, 3000) };
        using var svc = CreateService(fx, image);

        var report = await svc.RunMaintenancePassAsync();

        report.DimensionsWritten.Should().Be(1);
        image.Calls.Should().Be(0, "nur der Dateikopf wird gelesen, kein Vorschaubild erzeugt");
        image.DimensionReads.Should().Be(1);
        var first = await fx.FindFileByIdAsync(files[0].Id);
        (first!.Width, first.Height).Should().Be((4000, 3000));
        var second = await fx.FindFileByIdAsync(files[1].Id);
        (second!.Width, second.Height).Should().Be((10, 20), "vorhandene Maße bleiben");
        (await fx.FindFileByIdAsync(files[2].Id))!.Width.Should().BeNull();
    }

    [Fact]
    public async Task Pflege_merkt_nicht_lesbare_Masse_als_0_und_liest_sie_nicht_jede_Sitzung_neu()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var (libraryId, files) = await CreateLibraryAsync(fx, 1);
        await SeedThumbAsync(fx, libraryId, files[0], ThumbnailService.Size);
        var image = new FakeImageRenderer { HeaderSize = null }; // z. B. HEIC ohne Windows-Codec
        using var svc = CreateService(fx, image);

        await svc.RunMaintenancePassAsync();
        await svc.RunMaintenancePassAsync();

        image.DimensionReads.Should().Be(1);
        var file = await fx.FindFileByIdAsync(files[0].Id);
        (file!.Width, file.Height).Should().Be((0, 0), "0 × 0 = „nicht lesbar“");
    }

    [Fact]
    public async Task Masse_einer_inzwischen_ersetzten_Dateiversion_werden_nicht_geschrieben()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var (libraryId, files) = await CreateLibraryAsync(fx, 1);
        using var svc = CreateService(fx, new FakeImageRenderer { SourceSize = (640, 480) });

        var stale = Request(fx, libraryId, files[0]) with { SourceModifiedAtUtc = files[0].ModifiedAtUtc.AddMinutes(-1) };
        await svc.RequestThumbnailsAsync([stale]);
        await WaitForIdleAsync(svc);

        (await fx.FindFileByIdAsync(files[0].Id))!.Width.Should().BeNull("der Katalog kennt schon eine andere Version");
    }

    [Fact]
    public async Task Pflege_wartet_solange_die_Scan_Sperre_die_Bibliothek_belegt()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var (libraryId, files) = await CreateLibraryAsync(fx, 1);
        await SeedThumbAsync(fx, libraryId, files[0], LegacySize);
        var gate = new ScanGate(fx.Messenger);
        var image = new FakeImageRenderer();
        using var svc = CreateService(fx, image, gate);

        var held = await gate.AcquireAsync(libraryId, CancellationToken.None);
        var pass = svc.RunMaintenancePassAsync();
        await Task.Delay(500);
        image.Calls.Should().Be(0, "während eines Scans/Datei-Batches wird nichts eingereiht");
        svc.PendingCount.Should().Be(1, "der noch nicht eingereihte Ersatz zählt in der Statusleiste mit");
        held.Dispose();

        var report = await pass.WaitAsync(Timeout);
        await WaitForIdleAsync(svc);
        report.UpgradesQueued.Should().Be(1);
        image.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Pflege_ruht_solange_der_Dienst_pausiert_ist()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var (libraryId, files) = await CreateLibraryAsync(fx, 1);
        await SeedThumbAsync(fx, libraryId, files[0], LegacySize);
        var image = new FakeImageRenderer();
        using var svc = CreateService(fx, image);

        await svc.PauseAsync(); // Umzug der Katalog-DB (ADR-0026): kein DB-Zugriff
        var pass = svc.RunMaintenancePassAsync();
        await Task.Delay(400);
        pass.IsCompleted.Should().BeFalse();
        image.Calls.Should().Be(0);

        svc.Resume();
        (await pass.WaitAsync(Timeout)).UpgradesQueued.Should().Be(1);
        await WaitForIdleAsync(svc);
        image.Calls.Should().Be(1);
    }

    [Fact]
    public void IsLocallyAvailable_erkennt_fehlende_Dateien_und_Ordner()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pv3-local-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = TempLibraryFixture.WriteFile(Path.Combine(dir, "x.jpg"));
            ThumbnailService.IsLocallyAvailable(file).Should().BeTrue();
            ThumbnailService.IsLocallyAvailable(Path.Combine(dir, "fehlt.jpg")).Should().BeFalse();
            ThumbnailService.IsLocallyAvailable(dir).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
