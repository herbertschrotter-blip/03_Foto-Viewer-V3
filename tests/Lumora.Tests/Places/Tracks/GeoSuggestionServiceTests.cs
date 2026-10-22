using Lumora.Domain.Enums;
using Lumora.Services.Metadata;
using Lumora.Services.Places.Accuracy;
using Lumora.Services.Places.Suggestions;
using Lumora.Services.Places.Tracks;
using Lumora.Tests.Metadata;
using static Lumora.Tests.Places.Tracks.TrackTestFiles;

namespace Lumora.Tests.Places.Tracks;

/// <summary>
/// Genauigkeit im Katalog (Nachtrag, Sprung-Regel) und Vorschläge mit Übernehmen/Rückgängig (ADR-0062): Katalog und EXIF an
/// Temp-Kopien, abgelehnte und von Hand gesetzte Orte, Zähler.
/// </summary>
public sealed class GeoSuggestionServiceTests
{
    private static readonly DateTime Wall = new(2020, 3, 15, 14, 56, 10); // Wanduhrzeit (Wien, Winterzeit +1 h)

    [Fact]
    public async Task Genauigkeit_wird_nachgetragen_Handy_Schaetzung_Netzwerk_echtes_GPS_ohne_GPS_Video()
    {
        using var w = await TrackWorld.CreateAsync();
        var dir = Path.Combine(w.Fx.LibraryRoot, "Handy");
        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(Path.Combine(dir, "schaetzung.jpg"), SyntheticMediaFiles.Jpeg(PhoneTiff(Wall, 46.85, 15.12)));
        await File.WriteAllBytesAsync(Path.Combine(dir, "netz.jpg"), SyntheticMediaFiles.Jpeg(PhoneTiff(Wall, 46.85, 15.12, altitude: 400, method: "NETWORK")));
        await File.WriteAllBytesAsync(Path.Combine(dir, "gps.jpg"), SyntheticMediaFiles.Jpeg(PhoneTiff(Wall, 46.95, 14.96, altitude: 1853, gpsUtc: Wall.AddHours(-1), offset: "+01:00", make: "Apple", model: "iPhone 12")));
        await File.WriteAllBytesAsync(Path.Combine(dir, "ohne.jpg"), SyntheticMediaFiles.Jpeg(PlainTiff(Wall)));
        await File.WriteAllBytesAsync(Path.Combine(dir, "clip.mp4"), SyntheticGpsFiles.Mp4WithXyz("+46.9+015.0/"));
        await w.Fx.ScanAsync(w.Library.Id);
        await w.Geo.RunBackfillPassAsync();

        var report = await w.Accuracy.RunPassAsync();
        report.Read.Should().Be(5);
        report.Estimated.Should().Be(2);
        var schaetzung = (await w.Fx.FindFileAsync("schaetzung.jpg"))!;
        schaetzung.GeoAccuracy.Should().Be(GeoAccuracy.Estimated);
        schaetzung.GeoAccuracyReason.Should().Be(GeoAccuracyReason.NoAltitudeNoTime);
        schaetzung.CameraModel.Should().Be("Samsung SM-G991B");
        (await w.Fx.FindFileAsync("netz.jpg"))!.GeoAccuracyReason.Should().Be(GeoAccuracyReason.NetworkMethod);
        var gps = (await w.Fx.FindFileAsync("gps.jpg"))!;
        gps.GeoAccuracy.Should().Be(GeoAccuracy.Exact);
        gps.GpsTimeUtc.Should().Be(DateTime.SpecifyKind(Wall.AddHours(-1), DateTimeKind.Utc));
        gps.ExifTimeOffset.Should().Be(60);
        gps.CameraModel.Should().Be("Apple iPhone 12");
        var ohne = (await w.Fx.FindFileAsync("ohne.jpg"))!;
        ohne.GeoAccuracy.Should().Be(GeoAccuracy.Unknown);
        ohne.CameraModel.Should().Be("Canon EOS 80D", "Kamera auch ohne GPS (Zeitabgleich)");
        (await w.Fx.FindFileAsync("clip.mp4"))!.GeoAccuracy.Should().Be(GeoAccuracy.Unknown, "Video-GPS wird nicht bewertet");
        (await w.Accuracy.RunPassAsync()).Read.Should().Be(0, "einmal bewertet bleibt bewertet");
        (await w.Suggestions.CountAsync(await w.Scope.GetCurrentAsync())).Estimated.Should().Be(2);
    }

    [Fact]
    public async Task Sprung_Regel_markiert_und_hebt_wieder_auf()
    {
        using var w = await TrackWorld.CreateAsync();
        for (var i = 1; i <= 3; i++)
        {
            w.Fx.WriteLibraryFile($"s/f{i}.jpg", 2000 + i);
        }

        await w.Fx.ScanAsync(w.Library.Id);
        var (a, b, c) = (await w.FileIdAsync("f1.jpg"), await w.FileIdAsync("f2.jpg"), await w.FileIdAsync("f3.jpg"));
        w.SetPhoto(a, Wall, 60, 47.07, 15.44, 1, 1, gpsUtc: Wall.AddHours(-1));
        w.SetPhoto(b, Wall.AddMinutes(10), 60, 48.21, 16.37, 1, 1); // Wien — 10 min nach Graz
        w.SetPhoto(c, Wall.AddMinutes(20), 60, 47.071, 15.441, 1, 1, gpsUtc: Wall.AddHours(-1).AddMinutes(20));
        w.Execute("UPDATE Files SET GeoAccuracy = NULL WHERE Id = $id;", ("$id", c)); // c wird neu bewertet → Sprung-Regel läuft
        w.Execute("UPDATE Files SET GeoSource = 1 WHERE Id = $id;", ("$id", c));

        var report = await w.Accuracy.RunPassAsync();
        report.JumpsFlagged.Should().Be(1);
        var flagged = (await w.Fx.FindFileByIdAsync(b))!;
        flagged.GeoAccuracy.Should().Be(GeoAccuracy.Estimated);
        flagged.GeoAccuracyReason.Should().Be(GeoAccuracyReason.ImpossibleJump);

        // Die starken Nachbarn verschwinden aus dem Bereich → die Markierung fällt beim nächsten Durchgang weg
        w.Execute("UPDATE Files SET Latitude = 48.2101, Longitude = 16.3701, GpsTimeUtc = NULL WHERE Id IN ($a, $c);", ("$a", a), ("$c", c));
        w.Execute("UPDATE Files SET GeoAccuracy = NULL WHERE Id = $a;", ("$a", a));
        (await w.Accuracy.RunPassAsync()).JumpsCleared.Should().Be(1);
        (await w.Fx.FindFileByIdAsync(b))!.GeoAccuracy.Should().Be(GeoAccuracy.Exact);
    }

    /// <summary>
    /// Fünf Fotos am selben Tag: geschätzt in der Spur, ohne Ort zwischen zwei genauen, von Hand gesetzt (nie), abgelehnt
    /// (nie) und eine Garmin-Spur (Wanderung 14:05–14:25 Ortszeit).
    /// </summary>
    private static async Task<(TrackWorld W, int Estimated, int Unlocated, int Manual, int Rejected, int Exact1, int Exact2)> SceneAsync(bool realJpeg = false)
    {
        var w = await TrackWorld.CreateAsync();
        var dir = Path.Combine(w.Fx.LibraryRoot, "Tour");
        Directory.CreateDirectory(dir);
        foreach (var name in new[] { "schaetz.jpg", "ohne.jpg", "manuell.jpg", "abgelehnt.jpg", "gps1.jpg", "gps2.jpg" })
        {
            await File.WriteAllBytesAsync(Path.Combine(dir, name), SyntheticMediaFiles.Jpeg(PhoneTiff(Wall, 46.85, 15.12)));
        }

        await w.Fx.ScanAsync(w.Library.Id);
        var ids = new Dictionary<string, int>();
        foreach (var name in new[] { "schaetz.jpg", "ohne.jpg", "manuell.jpg", "abgelehnt.jpg", "gps1.jpg", "gps2.jpg" })
        {
            ids[name] = await w.FileIdAsync(name);
        }

        var start = new DateTime(2020, 3, 15, 14, 5, 0); // Wanduhrzeit
        w.SetPhoto(ids["schaetz.jpg"], start.AddMinutes(10), 60, 46.85, 15.12, 1, 2, (int)GeoAccuracyReason.NoAltitudeNoTime);
        w.SetPhoto(ids["ohne.jpg"], start.AddHours(2).AddMinutes(10), 60, null, null, 0, 0);
        w.SetPhoto(ids["manuell.jpg"], start.AddMinutes(12), 60, 46.85, 15.12, 3, 2, (int)GeoAccuracyReason.NoAltitudeNoTime);
        w.SetPhoto(ids["abgelehnt.jpg"], start.AddMinutes(14), 60, 46.85, 15.12, 1, 2, (int)GeoAccuracyReason.NoAltitudeNoTime);
        w.SetPhoto(ids["gps1.jpg"], start.AddHours(2), 60, 47.0, 15.0, 1, 1, gpsUtc: start.AddHours(1));
        w.SetPhoto(ids["gps2.jpg"], start.AddHours(2).AddMinutes(20), 60, 47.0, 15.02, 1, 1, gpsUtc: start.AddHours(1).AddMinutes(20));
        await w.Suggestions.SetRejectedAsync([ids["abgelehnt.jpg"]], rejected: true);

        // Spur 13:05–13:25 UTC (= 14:05–14:25 Ortszeit), ein Punkt je 10 s nach Osten
        var points = Enumerable.Range(0, 121)
            .Select(i => new TrackPoint(new DateTimeOffset(2020, 3, 15, 13, 5, 0, TimeSpan.Zero).ToUnixTimeSeconds() + (i * 10), 46.95, 14.95 + (i * 0.0002), 1800 + i))
            .ToList();
        var parsed = new ParsedTrack(points, "Wandern", "Fenix 7", "Wanderung Weinebene", GpsTrackSource.Fit);
        await w.Store.InsertAsync(await w.GroupAsync(), [new NewGpsTrack(parsed, TrackCodec.Checksum(points))]);
        _ = realJpeg;
        return (w, ids["schaetz.jpg"], ids["ohne.jpg"], ids["manuell.jpg"], ids["abgelehnt.jpg"], ids["gps1.jpg"], ids["gps2.jpg"]);
    }

    [Fact]
    public async Task Vorschlaege_Garmin_Spur_fuer_Ungenau_sicher_fuer_Ohne_Ort_nie_fuer_Hand_oder_Abgelehnt()
    {
        var (w, estimated, unlocated, manual, rejected, _, _) = await SceneAsync();
        using (w)
        {
            var scope = await w.Scope.GetCurrentAsync();
            var inaccurate = await w.Suggestions.LoadAsync(scope, GeoSuggestionListKind.Inaccurate);
            inaccurate.Items.Select(i => i.FileId).Should().Equal(estimated);
            inaccurate.TrackCount.Should().Be(1);
            var item = inaccurate.Items.Single();
            item.Level.Should().Be(GeoSuggestionLevel.Track);
            item.Track!.Title.Should().Be("Wanderung Weinebene");
            item.Suggestion!.Longitude.Should().BeApproximately(14.95 + (60 * 0.0002), 1e-9, "14:15 Ortszeit = 13:15 UTC = Punkt 60");
            item.LocalTime.Should().Be(new DateTime(2020, 3, 15, 14, 15, 0));
            item.CorrectionMeters.Should().BeGreaterThan(10_000);
            item.SourceOnApply.Should().Be(GeoSource.Track);

            var withoutLocation = await w.Suggestions.LoadAsync(scope, GeoSuggestionListKind.Unlocated);
            var u = withoutLocation.Items.Single(i => i.FileId == unlocated);
            u.Level.Should().Be(GeoSuggestionLevel.Sure, "zwischen zwei genauen Fotos 10 min davor und danach");
            u.Suggestion!.Longitude.Should().BeApproximately(15.01, 1e-6);
            u.SourceOnApply.Should().Be(GeoSource.Neighbors);
            withoutLocation.Items.Should().NotContain(i => i.FileId == manual || i.FileId == rejected);
            (await w.Suggestions.GetForFileAsync(estimated))!.Level.Should().Be(GeoSuggestionLevel.Track);
            (await w.Suggestions.GetForFileAsync(manual)).Should().BeNull("von Hand gesetzte Orte werden nie vorgeschlagen");

            await w.Suggestions.SetRejectedAsync([rejected], rejected: false);
            (await w.Suggestions.LoadAsync(scope, GeoSuggestionListKind.Inaccurate)).Items.Should().HaveCount(2, "wieder zugelassen");
            (await w.Suggestions.CountAsync(scope)).Should().Be(new GeoAccuracyCounts(2, 1, 1));
        }
    }

    [Fact]
    public async Task Uebernehmen_nur_im_Katalog_und_Rueckgaengig()
    {
        var (w, estimated, unlocated, _, _, _, _) = await SceneAsync();
        using (w)
        {
            var scope = await w.Scope.GetCurrentAsync();
            var path = Path.Combine(w.Fx.LibraryRoot, "Tour", "schaetz.jpg");
            var before = await File.ReadAllBytesAsync(path);
            var item = (await w.Suggestions.LoadAsync(scope, GeoSuggestionListKind.Inaccurate)).Items.Single();
            var u = (await w.Suggestions.LoadAsync(scope, GeoSuggestionListKind.Unlocated)).Items.Single(i => i.FileId == unlocated);

            var result = await w.Suggestions.ApplyAsync(
                [new GeoSuggestionApply(item.FileId, item.Suggestion!.Latitude, item.Suggestion.Longitude, item.SourceOnApply),
                 new GeoSuggestionApply(u.FileId, u.Suggestion!.Latitude, u.Suggestion.Longitude, u.SourceOnApply)],
                writeToFile: false);
            result.BatchId.Should().BeGreaterThan(0);
            result.CatalogOnly.Should().Be(2);
            result.Written.Should().Be(0);
            (await File.ReadAllBytesAsync(path)).Should().Equal(before, "nur im Katalog: die Datei bleibt unberührt");
            var applied = (await w.Fx.FindFileByIdAsync(estimated))!;
            applied.GeoSource.Should().Be(GeoSource.Track);
            applied.Longitude!.Value.Should().BeApproximately(item.Suggestion.Longitude, 1e-9);
            applied.PlaceId.Should().NotBeNull();
            (await w.Fx.FindFileByIdAsync(unlocated))!.GeoSource.Should().Be(GeoSource.Neighbors);
            (await w.Fx.GetBatchAsync(result.BatchId))!.Description.Should().Contain("nur im Katalog");
            (await w.Suggestions.LoadAsync(scope, GeoSuggestionListKind.Inaccurate)).Items.Should().BeEmpty("übernommen = nicht mehr geschätzt");
            (await w.Geo.GetUndoableBatchIdAsync()).Should().Be(result.BatchId, "dieselbe Rückgängig-Reihe wie „Ort setzen“");

            var undo = await w.Geo.UndoAsync(result.BatchId);
            undo!.Restored.Should().Be(2);
            var restored = (await w.Fx.FindFileByIdAsync(estimated))!;
            restored.GeoSource.Should().Be(GeoSource.Exif);
            restored.Latitude.Should().Be(46.85);
            (await w.Fx.FindFileByIdAsync(unlocated))!.Latitude.Should().BeNull();
            (await w.Suggestions.LoadAsync(scope, GeoSuggestionListKind.Inaccurate)).Items.Should().ContainSingle();
        }
    }

    [Fact]
    public async Task Uebernehmen_ins_Foto_schreibt_EXIF_an_der_Temp_Kopie_und_Rueckgaengig_stellt_die_Bytes_wieder_her()
    {
        var (w, estimated, _, _, _, _, _) = await SceneAsync();
        using (w)
        {
            var path = Path.Combine(w.Fx.LibraryRoot, "Tour", "schaetz.jpg"); // synthetische Kopie im Temp-Ordner des Tests
            var before = await File.ReadAllBytesAsync(path);
            var item = (await w.Suggestions.LoadAsync(await w.Scope.GetCurrentAsync(), GeoSuggestionListKind.Inaccurate)).Items.Single();

            var result = await w.Suggestions.ApplyAsync([new GeoSuggestionApply(estimated, item.Suggestion!.Latitude, item.Suggestion.Longitude, GeoSource.Track)], writeToFile: true);
            result.Written.Should().Be(1);
            var read = MediaGpsReader.Read(path)!.Value.Coordinate;
            read.Latitude.Should().BeApproximately(item.Suggestion.Latitude, 1e-5);
            read.Longitude.Should().BeApproximately(item.Suggestion.Longitude, 1e-5);
            (await w.Fx.FindFileByIdAsync(estimated))!.GeoSource.Should().Be(GeoSource.Track);

            await w.Geo.UndoAsync(result.BatchId);
            (await File.ReadAllBytesAsync(path)).Should().Equal(before, "Rückgängig Byte für Byte");
            (await w.Fx.FindFileByIdAsync(estimated))!.GeoSource.Should().Be(GeoSource.Exif);
        }
    }

    [Fact]
    public async Task Kameras_mit_ermittelter_Abweichung()
    {
        var (w, _, _, _, _, gps1, gps2) = await SceneAsync();
        using (w)
        {
            w.Execute("UPDATE Files SET CameraModel = 'Apple iPhone' WHERE Id IN ($a, $b);", ("$a", gps1), ("$b", gps2));
            var cameras = await w.Suggestions.GetCamerasAsync(await w.Scope.GetCurrentAsync());
            cameras.Should().Contain(new CameraTimeInfo("Apple iPhone", 2, null), "zu wenige Fotos mit GPS-Zeit für einen Vorschlag");
        }
    }

    [Fact]
    public async Task Uebernommene_Orte_und_Ablehnungen_ueberstehen_Neu_aufbauen_und_geaenderten_Inhalt()
    {
        var (w, estimated, unlocated, _, rejected, _, _) = await SceneAsync();
        using (w)
        {
            w.Execute("UPDATE Files SET Latitude = 46.96, Longitude = 14.96, GeoSource = 4 WHERE Id = $id;", ("$id", estimated));
            w.Execute("UPDATE Files SET Latitude = 47.0, Longitude = 15.01, GeoSource = 5 WHERE Id = $id;", ("$id", unlocated));
            await w.Fx.CreateScanService().ScanAsync(w.Library.Id, Services.Scanning.ScanMode.ClearAndRescan, null, CancellationToken.None);

            var track = (await w.Fx.FindFileAsync("schaetz.jpg"))!;
            track.GeoSource.Should().Be(GeoSource.Track, "„Neu aufbauen“ trägt die Herkunft mit");
            track.Latitude.Should().Be(46.96);
            (await w.Fx.FindFileAsync("ohne.jpg"))!.GeoSource.Should().Be(GeoSource.Neighbors);
            (await w.Fx.FindFileAsync("abgelehnt.jpg"))!.GeoSuggestionState.Should().Be(1, "abgelehnt bleibt abgelehnt");
            (await w.Fx.FindFileAsync("gps1.jpg"))!.GeoSource.Should().BeNull("alles andere liest das Nachtragen neu");

            // Inhalt an Ort und Stelle geändert: übernommener Ort bleibt, Genauigkeit wird neu bewertet
            var id = (await w.Fx.FindFileAsync("schaetz.jpg"))!.Id;
            w.Execute("UPDATE Files SET GeoAccuracy = 2 WHERE Id = $id;", ("$id", id));
            var path = Path.Combine(w.Fx.LibraryRoot, "Tour", "schaetz.jpg");
            await File.WriteAllBytesAsync(path, SyntheticMediaFiles.Jpeg(PhoneTiff(Wall, 46.0, 15.0)));
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));
            await w.Fx.CreateScanService().ScanAsync(w.Library.Id, Services.Scanning.ScanMode.Incremental, null, CancellationToken.None);
            var changed = (await w.Fx.FindFileByIdAsync(id))!;
            changed.GeoSource.Should().Be(GeoSource.Track);
            changed.GeoAccuracy.Should().BeNull("neu bewerten");
        }
    }

    [Fact]
    public async Task Versteckte_Bibliothek_Spuren_im_eigenen_Katalog_Temp_nur_im_Container()
    {
        using var w = await TrackWorld.CreateAsync();
        var container = Path.Combine(w.Fx.RootDir, "Container", ".lumora");
        var storage = new TestStoragePaths(container, hidden: true);
        var import = new GpsTrackImportService(w.Store, w.Scope, storage, Microsoft.Extensions.Logging.Abstractions.NullLogger<GpsTrackImportService>.Instance, w.Fx.Messenger);
        var zip = Path.Combine(w.Fx.RootDir, "Container", "export.zip");
        Directory.CreateDirectory(container);
        var inner = Zip(("a.fit", FitHike(Wall, 20, TimeSpan.FromSeconds(2), (47, 15), (47.01, 15.01))));
        await File.WriteAllBytesAsync(zip, Zip(("DI_CONNECT/UploadedFiles_0-_Part1.zip", inner)));
        var before = Directory.EnumerateFileSystemEntries(w.Fx.RootDir, "*", SearchOption.AllDirectories).Where(p => !p.StartsWith(container, StringComparison.OrdinalIgnoreCase)).ToHashSet();

        (await import.ImportAsync([zip])).Imported.Should().Be(1);
        w.Scalar("SELECT COUNT(*) FROM GpsTracks").Should().Be(1, "Spuren stehen im Katalog der Sitzung (im Container)");
        Directory.EnumerateFileSystemEntries(w.Fx.RootDir, "*", SearchOption.AllDirectories)
            .Where(p => !p.StartsWith(container, StringComparison.OrdinalIgnoreCase))
            .Should().BeEquivalentTo(before, "außerhalb des Containers entsteht nichts");
        Directory.EnumerateFiles(storage.TempRoot, "*", SearchOption.AllDirectories).Should().BeEmpty();
    }
}
