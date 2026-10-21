using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Domain.Entities;
using Lumora.Domain.Enums;
using Lumora.Services.Database;
using Lumora.Services.Thumbnails;
using Lumora.Tests.Fixtures;

namespace Lumora.Tests.Thumbnails;

/// <summary>
/// Regressionstests aus dem Review-Härtungslauf: In-Memory-Zustand pro Dateiversion, Stale/Done
/// ohne Cache-Datei, Abonnenten-Ausnahmen, Versuchszählung bei äußeren Ursachen, .tmp-Bereinigung,
/// Dispose und DB-Ausfall nach dem Rendern.
/// </summary>
public sealed class ThumbnailServiceHardeningTests
{
    private const int LibraryId = 1;
    private static readonly DateTime Modified = new(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ModifiedLater = Modified.AddMinutes(5);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private static ThumbnailService CreateService(
        TempLibraryFixture fx,
        FakeImageRenderer image,
        FakeVideoRenderer? video = null,
        FakeFallbackRenderer? fallback = null,
        ILibraryContextFactory? factory = null)
        => new(factory ?? fx.ContextFactory, fx.Paths, image, video ?? new FakeVideoRenderer(), NullLogger<ThumbnailService>.Instance, fallback);

    private static ThumbnailRequest ImageRequest(TempLibraryFixture fx, int fileId, DateTime? modified = null)
        => new(LibraryId, fileId, fx.WriteLibraryFile($"img{fileId}.jpg"), MediaType.Image, modified ?? Modified);

    private static string CachePath(TempLibraryFixture fx, int fileId)
        => Path.Combine(fx.Paths.GetLibraryThumbnailsPath(LibraryId), $"{fileId}_{ThumbnailService.Size}_still.jpg");

    private static async Task WaitForIdleAsync(ThumbnailService svc)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (svc.PendingCount > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        svc.PendingCount.Should().Be(0);
    }

    private static async Task<Thumbnail?> GetRowAsync(TempLibraryFixture fx, int fileId)
    {
        await using var ctx = fx.CreateContext();
        return await ctx.Thumbnails.AsNoTracking().SingleOrDefaultAsync(t => t.FileId == fileId && t.Size == ThumbnailService.Size);
    }

    private static async Task SeedRowAsync(TempLibraryFixture fx, int fileId, ThumbnailStatus status, int attempts, string cachePath, string? errorCode = null)
    {
        await using var ctx = fx.CreateContext();
        ctx.Thumbnails.Add(new Thumbnail
        {
            FileId = fileId,
            Size = ThumbnailService.Size,
            Kind = ThumbnailKind.Still,
            Format = "jpg",
            CachePath = cachePath,
            SourceModifiedAtUtc = Modified,
            GeneratedAtUtc = status == ThumbnailStatus.Generated ? Modified : null,
            ByteSize = 4,
            Status = status,
            AttemptCount = attempts,
            ErrorCode = errorCode,
        });
        await ctx.SaveChangesAsync();
    }

    private static async Task<ThumbnailReadyEventArgs> RequestAndWaitAsync(ThumbnailService svc, ThumbnailRequest request)
    {
        var ready = new TaskCompletionSource<ThumbnailReadyEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(ThumbnailReadyEventArgs e)
        {
            if (e.FileId == request.FileId)
            {
                ready.TrySetResult(e);
            }
        }

        svc.ThumbnailReady += Handler;
        try
        {
            await svc.RequestThumbnailsAsync(new[] { request });
            var args = await ready.Task.WaitAsync(Timeout);
            await WaitForIdleAsync(svc);
            return args;
        }
        finally
        {
            svc.ThumbnailReady -= Handler;
        }
    }

    [Fact]
    public async Task Done_Zustand_ohne_Cache_Datei_wird_in_derselben_Sitzung_neu_gerendert()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var image = new FakeImageRenderer();
        using var svc = CreateService(fx, image);
        var request = ImageRequest(fx, 21);
        var first = await RequestAndWaitAsync(svc, request);
        image.Calls.Should().Be(1);

        // Wie nach Papierkorb + Id-Wiederverwendung: Cache-Datei und Zeile sind weg, dieselbe Id kommt wieder.
        File.Delete(first.ThumbnailPath);
        await using (var ctx = fx.CreateContext())
        {
            await ctx.Thumbnails.Where(t => t.FileId == 21).ExecuteDeleteAsync();
        }

        var second = await RequestAndWaitAsync(svc, request);

        image.Calls.Should().Be(2);
        File.Exists(second.ThumbnailPath).Should().BeTrue();
        (await GetRowAsync(fx, 21))!.Status.Should().Be(ThumbnailStatus.Generated);
    }

    [Fact]
    public async Task Geaenderte_Quelle_mit_gleicher_FileId_wird_in_derselben_Sitzung_neu_gerendert()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var image = new FakeImageRenderer();
        using var svc = CreateService(fx, image);
        await RequestAndWaitAsync(svc, ImageRequest(fx, 22));
        image.Calls.Should().Be(1);

        // Gleiche Version noch einmal → In-Memory-Treffer, kein Renderer.
        var fired = 0;
        svc.ThumbnailReady += _ => Interlocked.Increment(ref fired);
        await svc.RequestThumbnailsAsync(new[] { ImageRequest(fx, 22) });
        fired.Should().Be(1);
        image.Calls.Should().Be(1);

        // Datei geändert (neuer Zeitstempel) → Cache ungültig → neu rendern.
        await RequestAndWaitAsync(svc, ImageRequest(fx, 22, ModifiedLater));

        image.Calls.Should().Be(2);
        var row = (await GetRowAsync(fx, 22))!;
        row.Status.Should().Be(ThumbnailStatus.Generated);
        row.SourceModifiedAtUtc.Should().Be(ModifiedLater);
        row.AttemptCount.Should().Be(2);
    }

    [Fact]
    public async Task Failed_Zustand_bleibt_fuer_die_Version_gesperrt_und_wird_fuer_neue_Version_erneut_versucht()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var image = new FakeImageRenderer { FailCode = ThumbnailErrorCodes.WicDecodeFailed };
        using var svc = CreateService(fx, image);

        await svc.RequestThumbnailsAsync(new[] { ImageRequest(fx, 23) });
        await WaitForIdleAsync(svc);
        await svc.RequestThumbnailsAsync(new[] { ImageRequest(fx, 23) });
        await WaitForIdleAsync(svc);
        image.Calls.Should().Be(1, "dieselbe Version wird in der Sitzung nicht erneut versucht");

        await svc.RequestThumbnailsAsync(new[] { ImageRequest(fx, 23, ModifiedLater) });
        await WaitForIdleAsync(svc);

        image.Calls.Should().Be(2, "eine geänderte Datei bekommt einen neuen Versuch");
        (await GetRowAsync(fx, 23))!.AttemptCount.Should().Be(2);
    }

    [Fact]
    public async Task Invalidate_erlaubt_einen_erneuten_Versuch_nach_Failed()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var image = new FakeImageRenderer { FailCode = ThumbnailErrorCodes.WicDecodeFailed };
        using var svc = CreateService(fx, image);
        await svc.RequestThumbnailsAsync(new[] { ImageRequest(fx, 24) });
        await WaitForIdleAsync(svc);
        image.Calls.Should().Be(1);

        image.FailCode = null;
        IThumbnailService contract = svc;
        contract.Invalidate(new[] { 24 });
        await RequestAndWaitAsync(svc, ImageRequest(fx, 24));

        image.Calls.Should().Be(2);
        (await GetRowAsync(fx, 24))!.Status.Should().Be(ThumbnailStatus.Generated);
    }

    [Fact]
    public async Task Abonnent_Ausnahme_beschaedigt_weder_Zustand_noch_Worker()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var image = new FakeImageRenderer();
        using var svc = CreateService(fx, image);
        var seen = new List<int>();
        svc.ThumbnailReady += e =>
        {
            lock (seen)
            {
                seen.Add(e.FileId);
            }

            if (e.FileId == 31)
            {
                throw new InvalidOperationException("Abonnent kaputt");
            }
        };

        await svc.RequestThumbnailsAsync(new[] { ImageRequest(fx, 31), ImageRequest(fx, 32) });
        await WaitForIdleAsync(svc);

        image.Calls.Should().Be(2, "der Worker läuft nach der Abonnenten-Ausnahme weiter");
        (await GetRowAsync(fx, 31))!.Status.Should().Be(ThumbnailStatus.Generated);
        (await GetRowAsync(fx, 32))!.Status.Should().Be(ThumbnailStatus.Generated);

        // Datei 31 gilt als erledigt (nicht Failed): erneute Anfrage liefert den Cache-Treffer, ohne zu werfen.
        var act = () => svc.RequestThumbnailsAsync(new[] { ImageRequest(fx, 31) });
        await act.Should().NotThrowAsync();
        lock (seen)
        {
            seen.Count(id => id == 31).Should().Be(2);
        }

        image.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Fehlende_Quelle_verbraucht_keinen_Versuch_und_ruft_keinen_Renderer()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var image = new FakeImageRenderer();
        using var svc = CreateService(fx, image);
        var missing = new ThumbnailRequest(LibraryId, 41, Path.Combine(fx.LibraryRoot, "offline", "fehlt.jpg"), MediaType.Image, Modified);

        await svc.RequestThumbnailsAsync(new[] { missing });
        await WaitForIdleAsync(svc);

        image.Calls.Should().Be(0);
        var row = (await GetRowAsync(fx, 41))!;
        row.Status.Should().Be(ThumbnailStatus.Failed);
        row.ErrorCode.Should().Be(ThumbnailErrorCodes.SourceMissing);
        row.AttemptCount.Should().Be(0, "ein Offline-Root darf die Datei nicht dauerhaft sperren");

        // Auch wenn der Renderer selbst source_missing meldet (Datei zwischen Prüfung und Render weg): kein Versuch verbraucht.
        image.FailCode = ThumbnailErrorCodes.SourceMissing;
        await svc.RequestThumbnailsAsync(new[] { ImageRequest(fx, 42) });
        await WaitForIdleAsync(svc);
        image.Calls.Should().Be(1);
        (await GetRowAsync(fx, 42))!.AttemptCount.Should().Be(0);

        // Echte Decoder-Fehler zählen weiterhin.
        image.FailCode = ThumbnailErrorCodes.WicDecodeFailed;
        await svc.RequestThumbnailsAsync(new[] { ImageRequest(fx, 43) });
        await WaitForIdleAsync(svc);
        (await GetRowAsync(fx, 43))!.AttemptCount.Should().Be(1);
    }

    [Fact]
    public async Task Stale_Zeile_aus_der_Crash_Recovery_wird_neu_gerendert()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var cachePath = CachePath(fx, 51);
        TempLibraryFixture.WriteFile(cachePath, 100);
        await SeedRowAsync(fx, 51, ThumbnailStatus.Stale, 1, cachePath);
        var image = new FakeImageRenderer();
        using var svc = CreateService(fx, image);

        var args = await RequestAndWaitAsync(svc, ImageRequest(fx, 51));

        image.Calls.Should().Be(1, "Stale heißt: Ergebnis des abgebrochenen Laufs ist unbekannt");
        args.ThumbnailPath.Should().Be(cachePath);
        var row = (await GetRowAsync(fx, 51))!;
        row.Status.Should().Be(ThumbnailStatus.Generated);
        row.AttemptCount.Should().Be(2);
    }

    [Fact]
    public async Task Video_ohne_FFmpeg_wird_nicht_eingereiht_und_schreibt_keine_Zeile()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var video = new FakeVideoRenderer { IsAvailable = false };
        using var svc = CreateService(fx, new FakeImageRenderer(), video);
        var fired = 0;
        svc.ThumbnailReady += _ => Interlocked.Increment(ref fired);
        var request = new ThumbnailRequest(LibraryId, 61, fx.WriteLibraryFile("clip.mp4"), MediaType.Video, Modified);

        await svc.RequestThumbnailsAsync(new[] { request });
        await svc.RequestThumbnailsAsync(new[] { request });
        await WaitForIdleAsync(svc);

        video.Calls.Should().Be(0);
        fired.Should().Be(0);
        (await GetRowAsync(fx, 61)).Should().BeNull();
    }

    [Fact]
    public async Task Alte_tmp_Dateien_werden_beim_Start_entfernt_frische_bleiben()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var dir = fx.Paths.GetLibraryThumbnailsPath(LibraryId);
        Directory.CreateDirectory(dir);
        var old = Path.Combine(dir, "7_320_still.jpg.tmp");
        var fresh = Path.Combine(dir, "8_320_still.jpg.tmp");
        File.WriteAllBytes(old, new byte[] { 1 });
        File.WriteAllBytes(fresh, new byte[] { 1 });
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow - TimeSpan.FromHours(1));
        using var svc = CreateService(fx, new FakeImageRenderer());

        await RequestAndWaitAsync(svc, ImageRequest(fx, 71)); // startet die Worker + Bereinigung
        var deadline = DateTime.UtcNow + Timeout;
        while (File.Exists(old) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        File.Exists(old).Should().BeFalse("Reste abgestürzter Läufe werden entfernt");
        File.Exists(fresh).Should().BeTrue("eine frische .tmp könnte zu einem laufenden Job gehören");
    }

    [Fact]
    public async Task Dispose_ist_idempotent_und_spaetere_Anfragen_sind_ein_NoOp()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var image = new FakeImageRenderer();
        var svc = CreateService(fx, image);
        await RequestAndWaitAsync(svc, ImageRequest(fx, 81));

        svc.Dispose();
        var again = () => svc.Dispose();
        again.Should().NotThrow();

        var act = () => svc.RequestThumbnailsAsync(new[] { ImageRequest(fx, 82) });
        await act.Should().NotThrowAsync();
        image.Calls.Should().Be(1);
    }

    [Fact]
    public async Task DB_Ausfall_nach_dem_Rendern_verhindert_ThumbnailReady_nicht()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        // Create #1 = Batch-Check, #2 = Pending, #3 = Generated → der dritte Aufruf scheitert.
        var failing = new FailingContextFactory(fx.ContextFactory, failOnCreate: 3);
        var image = new FakeImageRenderer();
        using var svc = CreateService(fx, image, factory: failing);

        var args = await RequestAndWaitAsync(svc, ImageRequest(fx, 91));

        File.Exists(args.ThumbnailPath).Should().BeTrue();
        failing.Creates.Should().BeGreaterThanOrEqualTo(3);
        var row = (await GetRowAsync(fx, 91))!;
        row.Status.Should().Be(ThumbnailStatus.Pending, "der Generated-Status konnte nicht geschrieben werden; der nächste Start setzt Stale");
        row.AttemptCount.Should().Be(1);

        // Danach erneut anfragen: In-Memory Done + Cache-Datei → sofortiger Treffer ohne DB.
        var fired = 0;
        svc.ThumbnailReady += _ => Interlocked.Increment(ref fired);
        await svc.RequestThumbnailsAsync(new[] { ImageRequest(fx, 91) });
        fired.Should().Be(1);
        image.Calls.Should().Be(1);
    }
}
