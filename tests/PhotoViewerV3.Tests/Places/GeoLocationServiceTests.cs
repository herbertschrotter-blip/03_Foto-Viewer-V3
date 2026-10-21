using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoViewerV3.Domain.Entities;
using PhotoViewerV3.Domain.Enums;
using PhotoViewerV3.Services.Messaging;
using PhotoViewerV3.Services.Metadata;
using PhotoViewerV3.Services.Places;
using PhotoViewerV3.Services.Scanning;
using PhotoViewerV3.Services.Watch;
using PhotoViewerV3.Tests.Fixtures;
using PhotoViewerV3.Tests.Metadata;

namespace PhotoViewerV3.Tests.Places;

/// <summary>
/// Orte im Katalog (ADR-0050): Nachtragen (EXIF, Video, XMP, Ort, fehlende Dateien, gesperrte Bibliothek), „Ort setzen“
/// mit Protokoll und Rückgängig Byte für Byte, „Ort entfernen“, Fehler, Scan-Verhalten, „Neu aufbauen“ und die
/// Lese-Abfragen der Karte (nur innerhalb der Bibliotheken).
/// </summary>
public sealed class GeoLocationServiceTests : IAsyncLifetime
{
    private static readonly ReverseGeocoder Geocoder = new();
    private TempLibraryFixture _fx = null!;
    private Library _library = null!;
    private readonly List<GeoLocationsChangedMessage> _changed = new();

    public async Task InitializeAsync()
    {
        _fx = await TempLibraryFixture.CreateAsync();
        _library = await _fx.CreateLibraryAsync();
        _fx.Messenger.Register<GeoLocationsChangedMessage>(this, (_, m) =>
        {
            lock (_changed)
            {
                _changed.Add(m);
            }
        });
    }

    public Task DisposeAsync()
    {
        _fx.Dispose();
        return Task.CompletedTask;
    }

    private GeoLocationService CreateService(IScanGate? gate = null, IOwnChangeRegistry? own = null, FakeRecycleBin? bin = null)
        => new(_fx.ContextFactory, new GeoLocationReader(), Geocoder, NullLogger<GeoLocationService>.Instance, gate, own, bin, _fx.Messenger)
        {
            StartDelay = Timeout.InfiniteTimeSpan,
        };

    private GeoQueryService CreateQuery() => new(_fx.ContextFactory, Geocoder, Geocoder);

    private string Write(string relative, byte[] bytes)
    {
        var path = Path.Combine(_fx.LibraryRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private async Task<MediaFile> FileAsync(string name) => (await _fx.FindFileAsync(name))!;

    [Fact]
    public async Task Nachtragen_liest_EXIF_Video_XMP_und_den_Ort()
    {
        Write("a/wien.jpg", SyntheticMediaFiles.Jpeg(SyntheticGpsFiles.Tiff(48.2082, 16.3738)));
        Write("a/video.mp4", SyntheticGpsFiles.Mp4WithXyz("+41.9028+012.4964/"));
        var raw = Write("a/raw.dng", SyntheticMediaFiles.Tiff("2024:07:12 15:30:12"));
        File.WriteAllBytes(raw + ".xmp", XmpGpsSidecar.Upsert(null, new GeoCoordinate(47.0707, 15.4395))!);
        Write("a/ohne.jpg", SyntheticMediaFiles.Jpeg(null));
        await _fx.ScanAsync(_library.Id);
        (await FileAsync("wien.jpg")).GeoSource.Should().BeNull("nach dem Scan noch nicht gelesen");

        using var service = CreateService();
        var report = await service.RunBackfillPassAsync();

        report.Should().Be(new GeoBackfillReport(4, 3, 0));
        var wien = await FileAsync("wien.jpg");
        wien.GeoSource.Should().Be(GeoSource.Exif);
        wien.Latitude!.Value.Should().BeApproximately(48.2082, 1e-6);
        Geocoder.GetPlace(wien.PlaceId!.Value)!.Name.Should().Be("Wien");
        var video = await FileAsync("video.mp4");
        video.GeoSource.Should().Be(GeoSource.Video);
        Geocoder.GetPlace(video.PlaceId!.Value)!.Name.Should().Be("Rom");
        Geocoder.GetPlace((await FileAsync("raw.dng")).PlaceId!.Value)!.Name.Should().Be("Graz");
        var ohne = await FileAsync("ohne.jpg");
        ohne.GeoSource.Should().Be(GeoSource.None);
        ohne.Latitude.Should().BeNull();
        _changed.SelectMany(m => m.FileIds).Should().HaveCount(3, "nur Dateien mit Koordinaten melden eine Änderung");
        (await service.RunBackfillPassAsync()).Should().Be(new GeoBackfillReport(0, 0, 0));
    }

    [Fact]
    public async Task Nachtragen_ueberspringt_fehlende_Dateien_und_ruht_bei_belegter_Bibliothek()
    {
        Write("b/da.jpg", SyntheticMediaFiles.Jpeg(SyntheticGpsFiles.Tiff(48.2082, 16.3738)));
        var gone = Write("b/weg.jpg", SyntheticMediaFiles.Jpeg(SyntheticGpsFiles.Tiff(48.2082, 16.3738)));
        await _fx.ScanAsync(_library.Id);
        File.Delete(gone);
        var gate = new ScanGate(new WeakReferenceMessenger());
        using var service = CreateService(gate);

        var lease = await gate.AcquireAsync(_library.Id, CancellationToken.None);
        var pass = service.RunBackfillPassAsync();
        await Task.Delay(600);
        pass.IsCompleted.Should().BeFalse("Scan bzw. Datei-Batch belegt die Bibliothek");
        lease.Dispose();

        var report = await pass.WaitAsync(TimeSpan.FromSeconds(10));
        report.Read.Should().Be(1);
        report.Skipped.Should().Be(1);
        (await FileAsync("weg.jpg")).GeoSource.Should().BeNull("bleibt für eine spätere Sitzung offen");
    }

    [Fact]
    public async Task Ort_setzen_schreibt_EXIF_protokolliert_und_Rueckgaengig_stellt_Bytes_wieder_her()
    {
        var path = Write("c/foto.jpg", SyntheticMediaFiles.Jpeg(SyntheticMediaFiles.Tiff("2024:07:12 15:30:12", "+02:00")));
        var original = File.ReadAllBytes(path);
        var modified = new DateTime(2015, 1, 2, 3, 4, 5, DateTimeKind.Utc).AddTicks(7_341_234); // Bruchteile zählen (Sync-Programme)
        File.SetLastWriteTimeUtc(path, modified);
        await _fx.ScanAsync(_library.Id);
        var gate = new ScanGate(new WeakReferenceMessenger());
        var own = new OwnChangeRegistry();
        using var service = CreateService(gate, own);
        await service.RunBackfillPassAsync();
        var file = await FileAsync("foto.jpg");

        var result = await service.SetLocationAsync([file.Id], 47.0707, 15.4395, keepModifiedTime: true, CancellationToken.None);

        result.Should().BeEquivalentTo(new GeoTagResult(1, 0, 0, []));
        own.IsOwn(path).Should().BeTrue("eigener Schreibvorgang für die Ordnerüberwachung");
        File.GetLastWriteTimeUtc(path).Should().Be(modified);
        MediaGpsReader.Read(path)!.Value.Coordinate.Latitude.Should().BeApproximately(47.0707, 1e-6);
        var after = await FileAsync("foto.jpg");
        after.GeoSource.Should().Be(GeoSource.Manual);
        Geocoder.GetPlace(after.PlaceId!.Value)!.Name.Should().Be("Graz");
        after.Size.Should().Be(new FileInfo(path).Length, "Katalog kennt die neue Größe — kein Scan sieht eine Änderung");
        var batchId = (await service.GetUndoableBatchIdAsync())!.Value;
        var batch = await _fx.GetBatchAsync(batchId);
        batch!.OperationType.Should().Be(OperationType.SetGeoLocation);
        batch.Description.Should().Contain("Graz");
        (await _fx.GetOperationsAsync(batchId)).Should().ContainSingle(o => o.Action == OperationType.SetGeoLocation && o.Status == FileOperationStatus.Applied);
        _changed.Should().ContainSingle(m => m.FileIds.Contains(file.Id));

        // Incremental-Scan nach dem eigenen Schreiben: nichts geändert, Ort bleibt.
        var rescan = await _fx.CreateScanService().ScanAsync(_library.Id, ScanMode.Incremental, null, CancellationToken.None);
        rescan.Modified.Should().Be(0);
        (await FileAsync("foto.jpg")).GeoSource.Should().Be(GeoSource.Manual);

        var undo = await service.UndoAsync(batchId);

        undo!.Restored.Should().Be(1);
        undo.Failed.Should().Be(0);
        File.ReadAllBytes(path).Should().Equal(original, "Rückgängig stellt die Datei Byte für Byte wieder her");
        File.GetLastWriteTimeUtc(path).Should().Be(modified);
        var restored = await FileAsync("foto.jpg");
        restored.GeoSource.Should().Be(GeoSource.None);
        restored.Latitude.Should().BeNull();
        restored.Size.Should().Be(original.Length);
        (await _fx.GetBatchAsync(batchId))!.Status.Should().Be(BatchStatus.Undone);
        (await service.GetUndoableBatchIdAsync()).Should().BeNull();
        (await service.UndoAsync(batchId)).Should().BeNull("schon zurückgenommen");
    }

    [Fact]
    public async Task Rueckgaengig_ueberspringt_inzwischen_veraenderte_Dateien()
    {
        var path = Write("d/foto.jpg", SyntheticMediaFiles.Jpeg(null));
        await _fx.ScanAsync(_library.Id);
        using var service = CreateService();
        var file = await FileAsync("foto.jpg");
        await service.SetLocationAsync([file.Id], 47.0707, 15.4395, true, CancellationToken.None);
        GeoFileWriter.Write(path, 0, new GeoCoordinate(1, 2), true); // fremde Änderung am EXIF
        var changed = File.ReadAllBytes(path);

        var undo = await service.UndoAsync((await service.GetUndoableBatchIdAsync())!.Value);

        undo!.Failed.Should().Be(1);
        undo.Errors.Single().Should().Contain("verändert");
        File.ReadAllBytes(path).Should().Equal(changed, "fremde Änderung bleibt");
        (await service.GetUndoableBatchIdAsync()).Should().BeNull("der Batch gilt als bearbeitet, Rückgängig hängt nicht fest");
    }

    [Fact]
    public async Task HEIC_bekommt_XMP_Begleitdatei_Video_nur_Katalog_Rueckgaengig_raeumt_auf()
    {
        var heic = Write("e/bild.heic", SyntheticMediaFiles.Heic(SyntheticMediaFiles.Tiff("2024:07:12 15:30:12")));
        var heicBytes = File.ReadAllBytes(heic);
        var video = Write("e/clip.mp4", SyntheticMediaFiles.Mp4(null));
        var videoBytes = File.ReadAllBytes(video);
        await _fx.ScanAsync(_library.Id);
        var bin = new FakeRecycleBin();
        using var service = CreateService(bin: bin);
        var ids = new[] { (await FileAsync("bild.heic")).Id, (await FileAsync("clip.mp4")).Id };

        var result = await service.SetLocationAsync(ids, 41.9028, 12.4964, true, CancellationToken.None);

        result.Written.Should().Be(1);
        result.CatalogOnly.Should().Be(1);
        File.ReadAllBytes(heic).Should().Equal(heicBytes);
        File.ReadAllBytes(video).Should().Equal(videoBytes);
        XmpGpsSidecar.Read(heic + ".xmp")!.Value.Latitude.Should().BeApproximately(41.9028, 1e-6);
        (await FileAsync("clip.mp4")).GeoSource.Should().Be(GeoSource.Manual);

        await service.UndoAsync((await service.GetUndoableBatchIdAsync())!.Value);

        File.Exists(heic + ".xmp").Should().BeFalse("von Lumora angelegte Begleitdatei in den Papierkorb");
        bin.Recycled.Should().ContainSingle(p => p.EndsWith("bild.heic.xmp", StringComparison.OrdinalIgnoreCase));
        (await FileAsync("clip.mp4")).GeoSource.Should().BeNull("alter Zustand: noch nicht gelesen");
    }

    [Fact]
    public async Task Ort_entfernen_entfernt_GPS_aus_dem_JPEG_und_bleibt_bei_Neu_aufbauen()
    {
        var path = Write("f/gps.jpg", SyntheticMediaFiles.Jpeg(SyntheticGpsFiles.Tiff(48.2082, 16.3738)));
        await _fx.ScanAsync(_library.Id);
        using var service = CreateService();
        await service.RunBackfillPassAsync();
        var id = (await FileAsync("gps.jpg")).Id;

        var result = await service.ClearLocationAsync([id], CancellationToken.None);

        result.Written.Should().Be(1);
        MediaGpsReader.Read(path).Should().BeNull();
        var file = await FileAsync("gps.jpg");
        file.GeoSource.Should().Be(GeoSource.Manual);
        file.Latitude.Should().BeNull();
        (await service.ClearLocationAsync([id], CancellationToken.None)).Should().BeEquivalentTo(new GeoTagResult(0, 1, 0, []), "nichts mehr zu entfernen");

        // „Neu aufbauen“: manuell entfernter Ort bleibt (sonst läse das Nachtragen eine Begleitdatei o. Ä. erneut).
        await _fx.CreateScanService().ScanAsync(_library.Id, ScanMode.ClearAndRescan, null, CancellationToken.None);
        (await FileAsync("gps.jpg")).GeoSource.Should().Be(GeoSource.Manual);
    }

    [Fact]
    public async Task Fehler_werden_gemeldet_ohne_Katalogaenderung()
    {
        var ro = Write("g/ro.jpg", SyntheticMediaFiles.Jpeg(null));
        var ok = Write("g/ok.jpg", SyntheticMediaFiles.Jpeg(null));
        await _fx.ScanAsync(_library.Id);
        File.SetAttributes(ro, FileAttributes.ReadOnly);
        using var service = CreateService();
        try
        {
            var result = await service.SetLocationAsync([(await FileAsync("ro.jpg")).Id, (await FileAsync("ok.jpg")).Id], 47.0707, 15.4395, true, CancellationToken.None);

            result.Written.Should().Be(1);
            result.Failed.Should().Be(1);
            result.Errors.Single().Should().StartWith("ro.jpg: ").And.Contain("schreibgeschützt");
            (await FileAsync("ro.jpg")).GeoSource.Should().BeNull("nicht geschriebene Dateien bleiben im Katalog unverändert");
            (await FileAsync("ok.jpg")).GeoSource.Should().Be(GeoSource.Manual);
        }
        finally
        {
            File.SetAttributes(ro, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task Inhalt_geaendert_liest_Orte_neu_ausser_manuell()
    {
        var auto = Write("h/auto.jpg", SyntheticMediaFiles.Jpeg(SyntheticGpsFiles.Tiff(48.2082, 16.3738)));
        Write("h/manuell.mp4", SyntheticMediaFiles.Mp4(null));
        await _fx.ScanAsync(_library.Id);
        using var service = CreateService();
        await service.RunBackfillPassAsync();
        await service.SetLocationAsync([(await FileAsync("manuell.mp4")).Id], 47.0707, 15.4395, true, CancellationToken.None);

        File.WriteAllBytes(auto, SyntheticMediaFiles.Jpeg(SyntheticGpsFiles.Tiff(41.9028, 12.4964)).Concat(new byte[10]).ToArray());
        File.SetLastWriteTimeUtc(auto, DateTime.UtcNow.AddMinutes(1));
        File.AppendAllText(Path.Combine(_fx.LibraryRoot, "h", "manuell.mp4"), "x");
        File.SetLastWriteTimeUtc(Path.Combine(_fx.LibraryRoot, "h", "manuell.mp4"), DateTime.UtcNow.AddMinutes(1));
        await _fx.CreateScanService().ScanAsync(_library.Id, ScanMode.Incremental, null, CancellationToken.None);

        (await FileAsync("auto.jpg")).GeoSource.Should().BeNull("wird neu gelesen");
        (await FileAsync("manuell.mp4")).GeoSource.Should().Be(GeoSource.Manual, "manuell gesetzt bleibt");
        await service.RunBackfillPassAsync();
        Geocoder.GetPlace((await FileAsync("auto.jpg")).PlaceId!.Value)!.Name.Should().Be("Rom");
    }

    [Fact]
    public async Task Karte_Punkte_Ausschnitt_Ausdehnung_und_Ortszaehler_nur_im_Bereich()
    {
        Write("i/wien.jpg", SyntheticMediaFiles.Jpeg(SyntheticGpsFiles.Tiff(48.2082, 16.3738)));
        Write("i/rom.jpg", SyntheticMediaFiles.Jpeg(SyntheticGpsFiles.Tiff(41.9028, 12.4964)));
        Write("i/fidschi.jpg", SyntheticMediaFiles.Jpeg(SyntheticGpsFiles.Tiff(-16.8, 179.9)));
        Write("i/ohne.jpg", SyntheticMediaFiles.Jpeg(null));
        await _fx.ScanAsync(_library.Id);
        var other = await _fx.CreateLibraryAsync("Andere");
        using (var connection = _fx.ContextFactory.OpenSqliteConnection())
        using (var cmd = connection.CreateCommand())
        {
            // Fremde Bibliothek: eine verortete Datei (darf nirgends auftauchen)
            cmd.CommandText = "UPDATE Files SET LibraryId = $other, Latitude = 48.21, Longitude = 16.37, GeoSource = 1 WHERE Name = 'ohne.jpg';";
            cmd.Parameters.AddWithValue("$other", other.Id);
            cmd.ExecuteNonQuery();
        }

        using var service = CreateService();
        await service.RunBackfillPassAsync();
        var query = CreateQuery();

        var all = await query.GetPointsAsync([_library.Id], null, CancellationToken.None);
        all.Should().HaveCount(3);
        (await query.GetPointsAsync([_library.Id], new GeoBounds(40, 10, 49, 17), CancellationToken.None)).Should().HaveCount(2);
        (await query.GetPointsAsync([_library.Id], new GeoBounds(-20, 170, -10, -170), CancellationToken.None)).Should().ContainSingle("über die Datumsgrenze");
        (await query.GetPointsAsync([], null, CancellationToken.None)).Should().BeEmpty();

        var extent = await query.GetExtentAsync([_library.Id], CancellationToken.None);
        extent.Should().NotBeNull();
        extent!.South.Should().BeApproximately(-16.8, 1e-6);
        extent.West.Should().BeApproximately(12.4964, 1e-6);
        extent.North.Should().BeApproximately(48.2082, 1e-6);
        extent.East.Should().BeApproximately(179.9, 1e-6);
        (await query.GetExtentAsync([other.Id + 1000], CancellationToken.None)).Should().BeNull();

        var counts = await query.GetPlaceCountsAsync([_library.Id]);
        counts.Places.Should().HaveCount(3);
        counts.WithoutLocation.Should().Be(0);
        (await query.GetPlaceCountsAsync([other.Id])).Places.Should().BeEmpty("in der anderen Bibliothek ist nichts nachgetragen");
    }

    [Fact]
    public async Task Zuletzt_verwendete_Orte_zuerst_dann_haeufige()
    {
        Write("j/a.jpg", SyntheticMediaFiles.Jpeg(null));
        Write("j/b.jpg", SyntheticMediaFiles.Jpeg(SyntheticGpsFiles.Tiff(48.2082, 16.3738)));
        await _fx.ScanAsync(_library.Id);
        using var service = CreateService();
        await service.RunBackfillPassAsync();
        await service.SetLocationAsync([(await FileAsync("a.jpg")).Id], 41.9028, 12.4964, true, CancellationToken.None);

        var recent = await CreateQuery().GetRecentLocationsAsync([_library.Id], 5);

        recent.Select(r => r.Label).Should().Equal("Rom, Italien", "Wien, Österreich");
        (await CreateQuery().GetRecentLocationsAsync([_library.Id + 99], 5)).Should().BeEmpty("anderer Bereich");
    }

    [Fact]
    public void Mehrfaches_Dispose_wirft_nicht()
    {
        var service = CreateService();
        service.Start();
        service.Dispose();
        FluentActions.Invoking(service.Dispose).Should().NotThrow("die DI entsorgt den Dienst je Schnittstelle");
    }

    [Fact]
    public async Task Ungueltige_Koordinate_wird_abgelehnt()
    {
        using var service = CreateService();
        await FluentActions.Awaiting(() => service.SetLocationAsync([1], 91, 0, true, CancellationToken.None)).Should().ThrowAsync<ArgumentOutOfRangeException>();
        (await service.SetLocationAsync([], 48, 16, true, CancellationToken.None)).Should().BeEquivalentTo(new GeoTagResult(0, 0, 0, []));
    }
}
