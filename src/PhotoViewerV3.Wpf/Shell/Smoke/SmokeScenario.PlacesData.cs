using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using PhotoViewerV3.Gallery.Data;
using PhotoViewerV3.Gallery.Places;
using PhotoViewerV3.Gallery.ViewModels;
using PhotoViewerV3.Services.Metadata;
using PhotoViewerV3.Services.Places;
using PhotoViewerV3.Services.Scanning;

namespace PhotoViewerV3.Shell.Smoke;

/// <summary>Rauchtest-Fake für den Dialog „Ort setzen“: liefert die vorgegebene Antwort und merkt sich die Anfrage.</summary>
internal sealed class SmokePlaceDialog : IPlaceDialogService
{
    public PlaceDialogResult? Next { get; set; }

    public PlaceDialogRequest? LastRequest { get; private set; }

    public Task<PlaceDialogResult?> PickAsync(PlaceDialogRequest request, Window? owner)
    {
        LastRequest = request;
        var next = Next;
        Next = null;
        return Task.FromResult(next);
    }
}

/// <summary>
/// Rauchtest-Schritt „Orte“ (ADR-0050): ein im Schritt erzeugtes JPEG mit GPS (Wien, per WIC geschrieben — unabhängig von
/// Lumoras Schreiber) wird nach dem Scan im Hintergrund gelesen (Herkunft EXIF, Ort „Wien, Österreich“). Für ein JPEG ohne
/// GPS „Ort setzen…“ (Graz): Katalog manuell, GPS in der Datei, Bilddaten ab SOS unverändert, Änderungsdatum
/// beibehalten; Filter „Ort“ über das Flyout zeigt genau diese Datei; „Rückgängig“ stellt die Datei Byte für Byte wieder her.
/// </summary>
internal sealed partial class SmokeScenario
{
    private const string GpsFileName = "smoke-gps-wien.jpg";
    private const string NoGpsFileName = "smoke-ohne-gps.jpg";

    private SmokePlaceDialog? _placeDialog;
    private IPlaceLookup? _placeLookup;

    private void InitPlaces(IServiceProvider services)
    {
        _placeDialog = services.GetService<SmokePlaceDialog>();
        _placeLookup = services.GetService<IPlaceLookup>();
    }

    private async Task PlacesAsync()
    {
        Check(_placeDialog is not null && _placeLookup is not null, "Fake-Dialog „Ort setzen“ und Ortsnamen registriert");

        // (1) Zwei neue Fotos: eines mit GPS (Wien), eines nur mit EXIF-Datum ohne GPS.
        var dir = Path.Combine(_root, "Unsortiert");
        var withGps = Path.Combine(dir, GpsFileName);
        var without = Path.Combine(dir, NoGpsFileName);
        WriteGpsJpeg(withGps, 48.2082, 16.3738);
        WriteExifDateJpeg(without, "2001:02:03 04:05:06");
        _lastScan = null;
        await _library.RunSmokeScanAsync(_root).ConfigureAwait(true);
        Check(_lastScan is { Mode: ScanMode.Incremental, Outcome: ScanOutcome.Completed, Added: 2 }, $"zwei Fotos aufgenommen (Added={_lastScan?.Added})");
        _mediaCount += 2;
        var gpsId = await DbScalarAsync<int>("SELECT f.Id FROM Files f WHERE f.LibraryId = $lib AND f.Name = $n", ("$lib", _libraryId), ("$n", GpsFileName)).ConfigureAwait(true);
        var plainId = await DbScalarAsync<int>("SELECT f.Id FROM Files f WHERE f.LibraryId = $lib AND f.Name = $n", ("$lib", _libraryId), ("$n", NoGpsFileName)).ConfigureAwait(true);

        // (2) Nachtragen liest GPS und Ort.
        var deadline = DateTime.UtcNow + LongWait;
        while (DateTime.UtcNow < deadline)
        {
            var open = await DbScalarAsync<int>("SELECT COUNT(*) FROM Files WHERE Id IN ($a, $b) AND GeoSource IS NULL", ("$a", gpsId), ("$b", plainId)).ConfigureAwait(true);
            if (open == 0)
            {
                break;
            }

            await Task.Delay(250).ConfigureAwait(true);
        }

        var gpsRow = (await DbRowsAsync("SELECT ROUND(Latitude, 4), ROUND(Longitude, 4), GeoSource FROM Files WHERE Id = $id", ("$id", gpsId)).ConfigureAwait(true)).Single();
        Check(gpsRow == "48.2082 | 16.3738 | 1", $"GPS aus EXIF nachgetragen: {gpsRow} (erwartet 48.2082 | 16.3738 | 1)");
        var gpsPlaceId = await DbScalarAsync<int>("SELECT COALESCE(PlaceId, 0) FROM Files WHERE Id = $id", ("$id", gpsId)).ConfigureAwait(true);
        var gpsPlace = _placeLookup!.GetPlace(gpsPlaceId);
        Check(gpsPlace is { Country: "Österreich", Region: "Wien" }, $"Ort erkannt: {gpsPlace?.Name}, {gpsPlace?.Region}, {gpsPlace?.Country}");
        var plainRow = (await DbRowsAsync("SELECT Latitude, GeoSource FROM Files WHERE Id = $id", ("$id", plainId)).ConfigureAwait(true)).Single();
        Check(plainRow == "NULL | 0", $"Foto ohne GPS: {plainRow} (erwartet NULL | 0)");

        // (3) Galerie: Ort je Kachel (Listenspalte „Ort“).
        await SetSortAsync(GallerySort.ModifiedDesc).ConfigureAwait(true);
        await WaitUntilAsync(() => _gallery.CurrentPage.FirstOrDefault(i => i.Id == gpsId)?.PlaceText.EndsWith("Österreich", StringComparison.Ordinal) == true, ShortWait, "Kachel zeigt den Ort").ConfigureAwait(true);

        // (4) „Ort setzen…“ (Graz) für das Foto ohne GPS.
        var before = await File.ReadAllBytesAsync(without).ConfigureAwait(true);
        var beforeModified = File.GetLastWriteTimeUtc(without);
        var target = _gallery.CurrentPage.First(i => i.Id == plainId);
        _gallery.ClearSelectionCommand.Execute(null);
        target.IsSelected = true;
        await WaitUntilAsync(() => _gallery.SelectedCount == 1, ShortWait, "eine Datei ausgewählt").ConfigureAwait(true);
        _placeDialog!.Next = PlaceDialogResult.Set(47.0707, 15.4395);
        await _gallery.SetSelectionPlaceCommand.ExecuteAsync(null).ConfigureAwait(true);
        Check(_placeDialog.LastRequest is { FileCount: 1, Current.Source: Domain.Enums.GeoSource.None }, $"Dialog mit 1 Datei ohne Ort aufgerufen ({_placeDialog.LastRequest?.CurrentText()})");
        var setRow = (await DbRowsAsync("SELECT ROUND(Latitude, 4), ROUND(Longitude, 4), GeoSource FROM Files WHERE Id = $id", ("$id", plainId)).ConfigureAwait(true)).Single();
        Check(setRow == "47.0707 | 15.4395 | 3", $"Ort im Katalog: {setRow} (erwartet 47.0707 | 15.4395 | 3)");
        var setPlace = _placeLookup.GetPlace(await DbScalarAsync<int>("SELECT COALESCE(PlaceId, 0) FROM Files WHERE Id = $id", ("$id", plainId)).ConfigureAwait(true));
        Check(setPlace?.Name == "Graz", $"Ort aus der Koordinate: {setPlace?.Name}");
        var inFile = MediaGpsReader.Read(without);
        Check(inFile is { } r && Math.Abs(r.Coordinate.Latitude - 47.0707) < 1e-6 && Math.Abs(r.Coordinate.Longitude - 15.4395) < 1e-6,
            $"GPS im EXIF der Datei: {inFile?.Coordinate.DisplayText}");
        var after = await File.ReadAllBytesAsync(without).ConfigureAwait(true);
        Check(SosTail(after).SequenceEqual(SosTail(before)), "Bilddaten ab SOS unverändert");
        Check(File.GetLastWriteTimeUtc(without) == beforeModified, "Änderungsdatum beibehalten");

        // (5) Filter „Ort“ über das Flyout: Graz anhaken → genau diese Datei.
        await _gallery.LoadPlaceFilterCommand.ExecuteAsync(null).ConfigureAwait(true);
        var graz = _gallery.PlaceFilterEntries.FirstOrDefault(e => e.Kind == PlaceFilterEntryKind.Place && e.Name == "Graz");
        Check(graz is { Count: 1 }, $"Flyout „Ort“: Graz mit 1 Datei ({string.Join(" · ", _gallery.PlaceFilterEntries.Take(8))})");
        graz!.IsSelected = true;
        await _gallery.ApplyPlaceFilterCommand.ExecuteAsync(null).ConfigureAwait(true);
        await WaitUntilAsync(() => !_gallery.IsLoading && _gallery.TotalCount == 1 && _gallery.CurrentPage.Count == 1 && _gallery.CurrentPage[0].Id == plainId, ShortWait, "Filter „Ort: Graz“").ConfigureAwait(true);
        Check(_gallery.ActiveConditions.Any(c => c.Kind == GalleryFilterConditionKind.Place && c.Label == "Ort: Graz"), "„Es gilt: Ort: Graz“");
        Check(_gallery.PlaceFilterValueText == "Graz", $"Chip „Ort: {_gallery.PlaceFilterValueText}“");
        await SnapshotAsync("40-galerie-ort").ConfigureAwait(true);
        await _gallery.ClearPlaceFilterCommand.ExecuteAsync(null).ConfigureAwait(true);
        await WaitUntilAsync(() => !_gallery.IsLoading && _gallery.TotalCount == _mediaCount, ShortWait, "Ort-Filter aufgehoben").ConfigureAwait(true);

        // (6) Rückgängig: Datei Byte für Byte wie vorher, Katalog wie vorher.
        target = _gallery.CurrentPage.First(i => i.Id == plainId);
        Check(_gallery.CanUndo, "Rückgängig verfügbar");
        await _gallery.UndoCommand.ExecuteAsync(null).ConfigureAwait(true);
        await WaitUntilAsync(() => !_gallery.IsOperationRunning, ShortWait, "Rückgängig beendet").ConfigureAwait(true);
        var restored = await File.ReadAllBytesAsync(without).ConfigureAwait(true);
        Check(restored.AsSpan().SequenceEqual(before), $"Rückgängig: Datei Byte für Byte wie vorher ({restored.Length:N0} / {before.Length:N0} Bytes)");
        Check(File.GetLastWriteTimeUtc(without) == beforeModified, "Rückgängig: Änderungsdatum wie vorher");
        var undoneRow = (await DbRowsAsync("SELECT Latitude, GeoSource FROM Files WHERE Id = $id", ("$id", plainId)).ConfigureAwait(true)).Single();
        Check(undoneRow == "NULL | 0", $"Rückgängig: Katalog wie vorher ({undoneRow})");
        var batch = await DbScalarAsync<int>("SELECT Status FROM OperationBatches WHERE OperationType = 8 ORDER BY Id DESC LIMIT 1").ConfigureAwait(true);
        Check(batch == 4, $"Protokoll: Batch „Ort setzen“ zurückgenommen (Status {batch})");
        _gallery.ClearSelectionCommand.Execute(null);
    }

    /// <summary>Bytes ab dem ersten SOS-Marker (FF DA).</summary>
    private static byte[] SosTail(byte[] jpeg)
    {
        for (var i = 2; i < jpeg.Length - 1; i++)
        {
            if (jpeg[i] == 0xFF && jpeg[i + 1] == 0xDA)
            {
                return jpeg[i..];
            }
        }

        return [];
    }

    /// <summary>Kleines JPEG mit GPS, geschrieben über WIC (RATIONAL als UInt64: Zähler unten, Nenner oben).</summary>
    private static void WriteGpsJpeg(string path, double latitude, double longitude)
    {
        var pixels = new byte[64 * 48 * 4];
        Array.Fill(pixels, (byte)120);
        var source = BitmapSource.Create(64, 48, 96, 96, PixelFormats.Bgra32, null, pixels, 64 * 4);
        var metadata = new BitmapMetadata("jpg");
        metadata.SetQuery("/app1/ifd/gps/{ushort=0}", new byte[] { 2, 3, 0, 0 });
        metadata.SetQuery("/app1/ifd/gps/{ushort=1}", latitude < 0 ? "S" : "N");
        metadata.SetQuery("/app1/ifd/gps/{ushort=2}", Dms(Math.Abs(latitude)));
        metadata.SetQuery("/app1/ifd/gps/{ushort=3}", longitude < 0 ? "W" : "E");
        metadata.SetQuery("/app1/ifd/gps/{ushort=4}", Dms(Math.Abs(longitude)));
        var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
        encoder.Frames.Add(BitmapFrame.Create(source, null, metadata, null));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        encoder.Save(stream);

        static ulong[] Dms(double value)
        {
            var d = Math.Floor(value);
            var m = Math.Floor((value - d) * 60);
            var s = Math.Round((((value - d) * 60) - m) * 60 * 1000);
            return [Rational((uint)d, 1), Rational((uint)m, 1), Rational((uint)s, 1000)];
        }

        static ulong Rational(uint numerator, uint denominator) => ((ulong)denominator << 32) | numerator;
    }
}

/// <summary>Kurztext der Dialog-Anfrage fürs Protokoll.</summary>
internal static class SmokePlaceRequestText
{
    public static string CurrentText(this PlaceDialogRequest request)
        => string.Create(CultureInfo.InvariantCulture, $"{request.Subject}, {request.FileCount}, Quelle {request.Current?.Source}");
}
