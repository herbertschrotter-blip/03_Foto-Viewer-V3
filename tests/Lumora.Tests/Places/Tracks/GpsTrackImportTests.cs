using CommunityToolkit.Mvvm.Messaging;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Services.Library;
using Lumora.Services.Places.Tracks;
using Lumora.Tests.Fixtures;
using static Lumora.Tests.Places.Tracks.TrackTestFiles;

namespace Lumora.Tests.Places.Tracks;

/// <summary>
/// Einlesen der GPS-Spuren (ADR-0062): Garmin-Datenexport mit verschachteltem ZIP, Doppel-Erkennung, Temp-Dateien nur im
/// Temp-Ordner des Katalogs (versteckte Bibliothek: im Container), Ordner, Bibliotheks-Gruppen (getrennt, verknüpfen,
/// lösen, entfernen) und Einstellungen.
/// </summary>
public sealed class GpsTrackImportTests
{
    private static readonly DateTime Day = new(2020, 3, 15, 12, 55, 0, DateTimeKind.Utc);

    /// <summary>Export wie bei Garmin: DI_CONNECT/…/UploadedFiles_0-_Part1.zip mit FIT/GPX, daneben JSON.</summary>
    private static byte[] Export()
    {
        var inner = Zip(
            ("herbert@example.org_1001.fit", FitHike(Day, 50, TimeSpan.FromSeconds(10), (46.95, 14.95), (46.97, 14.99))),
            ("herbert@example.org_1002.fit", FitHike(Day.AddDays(1), 30, TimeSpan.FromSeconds(10), (47.1, 15.4), (47.11, 15.41), device: "Fenix 5")),
            ("herbert@example.org_1003.gpx", Encoding.UTF8.GetBytes(Gpx(("Radtour", "cycling", [(Day.AddDays(2), 47.0, 15.0, null), (Day.AddDays(2).AddSeconds(5), 47.001, 15.001, null)])))),
            ("herbert@example.org_1004.fit", new FitBuilder().Define(0, 20, false, [(253, 4, 0x86), (7, 2, 0x84)]).Data(0, Le(FitTime(Day), (ushort)1)).Build()),
            ("kaputt.fit", Encoding.ASCII.GetBytes("das ist kein FIT")));
        return Zip(
            ("DI_CONNECT/DI-Connect-Uploaded-Files/UploadedFiles_0-_Part1.zip", inner),
            ("DI_CONNECT/DI-Connect-User/user_profile.json", "{}"u8.ToArray()));
    }

    [Fact]
    public async Task Export_ZIP_mit_verschachteltem_ZIP_einlesen_Doppelte_ueberspringen_Temp_aufgeraeumt()
    {
        using var w = await TrackWorld.CreateAsync();
        var zip = Path.Combine(w.Fx.OutsideDir, "garmin-export.zip");
        Directory.CreateDirectory(w.Fx.OutsideDir);
        await File.WriteAllBytesAsync(zip, Export());

        var first = await w.Import.ImportAsync([zip]);
        first.Should().Be(new GpsTrackImportReport(5, 3, 0, 1, 1, false), "3 Aktivitäten (2 FIT, 1 GPX), 1 ohne GPS, 1 kaputt");
        first.Text.Should().Be("3 Aktivitäten eingelesen, 1 ohne GPS, 1 nicht lesbar");
        var second = await w.Import.ImportAsync([zip]);
        second.Imported.Should().Be(0);
        second.Duplicates.Should().Be(3, "dieselben Punkte werden nicht doppelt gespeichert");

        var group = await w.GroupAsync();
        var summary = await w.Store.GetSummaryAsync(group);
        summary.Count.Should().Be(3);
        summary.Devices.Should().BeEquivalentTo(["Fenix 7", "Fenix 5", "Lumora-Test"]);
        GpsTrackStore.Describe(summary).Should().StartWith("3 Aktivitäten · 2020 · ");
        var list = await w.Store.ListAsync(group);
        list.Select(t => t.Sport).Should().BeEquivalentTo(["Wandern", "Wandern", "Radfahren"]);
        list.Single(t => t.Name == "Radtour").Source.Should().Be(Domain.Enums.GpsTrackSource.Gpx);
        var hike = list.Single(t => t.Device == "Fenix 7");
        hike.PointCount.Should().Be(50);
        hike.DistanceMeters.Should().BeInRange(3500, 4000);
        (await w.Store.LoadPointsAsync(hike.Id)).Should().HaveCount(50);
        (await w.Store.FindOverlappingAsync(group, hike.StartUnix + 100, hike.StartUnix + 100)).Should().ContainSingle().Which.Id.Should().Be(hike.Id);

        Directory.Exists(w.Storage.TempRoot).Should().BeTrue("das verschachtelte ZIP lag kurz im Temp-Ordner des Katalogs");
        Directory.EnumerateFileSystemEntries(w.Storage.TempRoot).Should().BeEmpty("danach ist er leer");
        Directory.EnumerateFiles(w.Fx.OutsideDir, "*", SearchOption.AllDirectories).Should().Equal([zip], "nichts wird neben den Export entpackt");
    }

    [Fact]
    public async Task Ordner_und_einzelne_Dateien_und_ohne_Bibliothek_nichts()
    {
        using var w = await TrackWorld.CreateAsync();
        var folder = Path.Combine(w.Fx.OutsideDir, "Sport", "Garmin");
        Directory.CreateDirectory(Path.Combine(folder, "2020"));
        await File.WriteAllBytesAsync(Path.Combine(folder, "2020", "a.fit"), FitHike(Day, 10, TimeSpan.FromSeconds(1), (47, 15), (47.001, 15.001)));
        await File.WriteAllTextAsync(Path.Combine(folder, "b.gpx"), Gpx(("B", "hiking", [(Day.AddHours(5), 47.2, 15.2, 400.0)])));
        await File.WriteAllTextAsync(Path.Combine(folder, "notiz.txt"), "nicht einlesen");

        var report = await w.Import.ImportAsync([folder]);
        report.Imported.Should().Be(2);
        report.FilesRead.Should().Be(2, "nur FIT/GPX/TCX/ZIP");

        w.Execute("DELETE FROM Libraries;");
        (await w.Import.ImportAsync([folder])).Error.Should().Contain("Keine Bibliothek");
    }

    [Fact]
    public async Task Spuren_je_Gruppe_getrennt_verknuepft_zusammengefuehrt_geloest_kopiert_mit_der_Gruppe_entfernt()
    {
        using var w = await TrackWorld.CreateAsync();
        var libraries = w.Fx.CreateLibraryService();
        using var linking = new LibraryLinkService(w.Fx.ContextFactory, new TestCatalogContext(), NullLogger<LibraryLinkService>.Instance, w.Fx.Messenger);
        var a = w.Library.Id;
        Directory.CreateDirectory(w.Fx.OutsideDir);
        var b = (await libraries.CreateAsync("Zweite", w.Fx.OutsideDir)).Id;
        var shared = Path.Combine(w.Fx.RootDir, "shared.fit");
        await File.WriteAllBytesAsync(shared, FitHike(Day, 10, TimeSpan.FromSeconds(1), (47, 15), (47.001, 15.001)));
        var onlyB = Path.Combine(w.Fx.RootDir, "b.fit");
        await File.WriteAllBytesAsync(onlyB, FitHike(Day.AddDays(3), 10, TimeSpan.FromSeconds(1), (48, 16), (48.001, 16.001)));

        await libraries.SetActiveAsync(a);
        (await w.Import.ImportAsync([shared])).Imported.Should().Be(1);
        var groupA = await w.GroupAsync();
        await libraries.SetActiveAsync(b);
        var groupB = await w.GroupAsync();
        groupB.Should().NotBe(groupA);
        (await w.Store.GetSummaryAsync(groupB)).Count.Should().Be(0, "Spuren aus A sind in B unsichtbar");
        (await w.Import.ImportAsync([shared, onlyB])).Imported.Should().Be(2, "B hat eigene Spuren");

        await linking.LinkAsync(a, b);
        var linked = (await w.Scope.GetForLibraryAsync(a)).GroupId;
        (await w.Store.GetSummaryAsync(linked)).Count.Should().Be(2, "gleiche Spur zusammengeführt");
        w.Scalar("SELECT COUNT(*) FROM GpsTracks").Should().Be(2);

        await linking.UnlinkAsync(b);
        var newB = (await w.Scope.GetForLibraryAsync(b)).GroupId;
        (await w.Store.GetSummaryAsync(newB)).Count.Should().Be(2, "Lösen kopiert die Spuren");
        (await w.Store.GetSummaryAsync(linked)).Count.Should().Be(2);

        await libraries.DeleteAsync(b);
        w.Scalar("SELECT COUNT(*) FROM GpsTracks WHERE GroupId = $g", ("$g", newB)).Should().Be(0, "leere Gruppe nimmt ihre Spuren mit");
        (await w.Store.DeleteAllAsync(linked)).Should().Be(2);
    }

    [Fact]
    public async Task Einstellungen_Rundreise_und_Merker()
    {
        using var w = await TrackWorld.CreateAsync();
        (await w.Settings.LoadAsync()).Should().BeEquivalentTo(GeoTrackSettings.Default);
        var settings = new GeoTrackSettings("Tokyo Standard Time", 300, new Dictionary<string, int> { ["Canon EOS 80D"] = 192 }, @"D:\Sport", false, true);
        await w.Settings.SaveAsync(settings);
        var loaded = await w.Settings.LoadAsync();
        loaded.Should().BeEquivalentTo(settings);
        loaded.ResolveTimeZone().Id.Should().Be("Tokyo Standard Time");
        loaded.OffsetFor("Canon EOS 80D").Should().Be(192);
        loaded.OffsetFor("unbekannt").Should().Be(0);
        (settings with { TimeZoneId = "Gibt es nicht" }).ResolveTimeZone().Should().Be(TimeZoneInfo.Local);
        (await w.Settings.GetMarkerAsync(GeoTrackSettings.UsbLastImportKey)).Should().BeNull();
        await w.Settings.SetMarkerAsync(GeoTrackSettings.UsbLastImportKey, 1234);
        (await w.Settings.GetMarkerAsync(GeoTrackSettings.UsbLastImportKey)).Should().Be(1234);
    }

    [Fact]
    public async Task USB_Uhr_als_Laufwerk_meldet_neue_Aktivitaeten_und_liest_nach_Zustimmung_ein()
    {
        using var w = await TrackWorld.CreateAsync();
        var drive = Path.Combine(w.Fx.RootDir, "uhr");
        var activity = Path.Combine(drive, "GARMIN", "Activity");
        Directory.CreateDirectory(activity);
        await File.WriteAllBytesAsync(Path.Combine(activity, "A1.FIT"), FitHike(Day, 10, TimeSpan.FromSeconds(1), (47, 15), (47.001, 15.001)));
        var detected = new List<Services.Messaging.GarminDeviceDetectedMessage>();
        w.Fx.Messenger.Register<GpsTrackImportTests, Services.Messaging.GarminDeviceDetectedMessage>(this, (_, m) => { lock (detected) { detected.Add(m); } });
        using var auto = new GpsTrackAutoImport(w.Import, w.Settings, NullLogger<GpsTrackAutoImport>.Instance, w.Fx.Messenger, () => [drive]);

        GpsTrackAutoImport.ActivityFolderOn(drive).Should().Be(activity);
        GpsTrackAutoImport.ActivityFolderOn(w.Fx.RootDir).Should().BeNull();
        await auto.ReconfigureAsync();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (detected.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        detected.Should().ContainSingle().Which.Should().Be(new Services.Messaging.GarminDeviceDetectedMessage(activity, 1));
        w.Scalar("SELECT COUNT(*) FROM GpsTracks").Should().Be(0, "erst nach der Rückfrage");
        (await auto.ImportDeviceAsync(activity)).Imported.Should().Be(1);
        auto.FindNewOnDevice(activity, await w.Settings.GetMarkerAsync(GeoTrackSettings.UsbLastImportKey)).Should().BeEmpty("Merker gesetzt");
    }
}
