using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoViewerV3.Gallery.CaptureDates;
using PhotoViewerV3.Gallery.Data;
using PhotoViewerV3.Services.Metadata;
using PhotoViewerV3.Services.Scanning;

namespace PhotoViewerV3.Shell.Smoke;

/// <summary>Rauchtest-Fake für den Dialog „Datum setzen…“: liefert die vorgegebene Antwort und merkt sich die Anfrage.</summary>
internal sealed class SmokeCaptureDateDialog : ICaptureDateDialogService
{
    public CaptureDateDialogResult? Next { get; set; }

    public CaptureDateDialogRequest? LastRequest { get; private set; }

    public Task<CaptureDateDialogResult?> PickAsync(CaptureDateDialogRequest request, Window? owner)
    {
        LastRequest = request;
        var next = Next;
        Next = null;
        return Task.FromResult(next);
    }
}

/// <summary>
/// Rauchtest-Schritt „Aufnahmedatum“ (0.2.48, ADR-0046): ein im Schritt erzeugtes JPEG mit EXIF-Aufnahmedatum 1975 (Datei
/// selbst brandneu) wird nach dem Scan im Hintergrund gelesen (Herkunft EXIF); die Sortierung nach Aufnahmedatum stellt es
/// an den Anfang, die nach Änderungsdatum nicht. Dann „Datum setzen…“ ungefähr 1982 für eine andere Datei (Herkunft
/// manuell, Genauigkeit Jahr, Listentext „ca. 1982“) und „Rückgängig“ über die Galerie (alter Wert zurück).
/// </summary>
internal sealed partial class SmokeScenario
{
    private const string ExifDateFileName = "smoke-exif-1975.jpg";

    private SmokeCaptureDateDialog? _captureDialog;

    private async Task CaptureDateAsync()
    {
        Check(_captureDialog is not null, "Fake-Dialog „Datum setzen“ registriert");

        // (1) Foto mit EXIF DateTimeOriginal 1975 anlegen, Incremental-Scan, Nachtragen liest es.
        var path = Path.Combine(_root, "Unsortiert", ExifDateFileName);
        WriteExifDateJpeg(path, "1975:05:17 10:20:30");
        _lastScan = null;
        await _library.RunSmokeScanAsync(_root).ConfigureAwait(true);
        Check(_lastScan is { Mode: ScanMode.Incremental, Outcome: ScanOutcome.Completed, Added: 1 }, $"EXIF-Foto aufgenommen (Added={_lastScan?.Added})");
        _mediaCount++;
        var fileId = await DbScalarAsync<int>("SELECT f.Id FROM Files f WHERE f.LibraryId = $lib AND f.Name = $n", ("$lib", _libraryId), ("$n", ExifDateFileName)).ConfigureAwait(true);
        var deadline = DateTime.UtcNow + LongWait;
        string? row = null;
        while (DateTime.UtcNow < deadline)
        {
            row = (await DbRowsAsync("SELECT CapturedAt, CapturedAtSource, CapturedAtPrecision FROM Files WHERE Id = $id", ("$id", fileId)).ConfigureAwait(true)).SingleOrDefault();
            if (row is not null && !row.StartsWith("NULL", StringComparison.Ordinal))
            {
                break;
            }

            await Task.Delay(250).ConfigureAwait(true);
        }

        var expected = CaptureDateValue.FromWallClock(new DateTime(1975, 5, 17, 10, 20, 30), null, Domain.Enums.CaptureDateSource.Exif, Domain.Enums.CaptureDatePrecision.Exact);
        var expectedUnix = new DateTimeOffset(expected.Utc).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        Check(row == $"{expectedUnix} | 1 | 0", $"Aufnahmedatum aus EXIF nachgetragen: {row} (erwartet {expectedUnix} | 1 | 0)");
        var unread = await DbScalarAsync<int>("SELECT COUNT(*) FROM Files WHERE LibraryId = $lib AND CapturedAtSource IS NULL AND Missing = 0", ("$lib", _libraryId)).ConfigureAwait(true);
        Check(unread == 0, $"alle Dateien der Bibliothek gelesen (noch offen: {unread})");

        // (2) Sortierung: nach Aufnahmedatum alt → neu steht das 1975er-Foto vorn, nach Änderungsdatum nicht.
        await SetSortAsync(GallerySort.CapturedAsc).ConfigureAwait(true);
        await WaitUntilAsync(() => !_gallery.IsLoading && _gallery.CurrentPage.Count > 0, ShortWait, "Galerie nach Aufnahmedatum").ConfigureAwait(true);
        Check(_gallery.CurrentPage[0].Name == ExifDateFileName, $"Aufnahme alt → neu: zuerst {_gallery.CurrentPage[0].Name}");
        Check(_gallery.CurrentPage[0].CaptureDateToolTip.Contains("17.05.1975 10:20 (EXIF)", StringComparison.Ordinal), $"Kachel-Tooltip: {_gallery.CurrentPage[0].CaptureDateToolTip}");
        await SetSortAsync(GallerySort.ModifiedAsc).ConfigureAwait(true);
        Check(_gallery.CurrentPage.Count > 1 && _gallery.CurrentPage[0].Name != ExifDateFileName, $"Änderungsdatum alt → neu: zuerst {_gallery.CurrentPage[0].Name} (nicht das EXIF-Foto)");

        // (3) „Datum setzen…“ ungefähr 1982 für die erste Datei nach Änderungsdatum, dann Rückgängig.
        var target = _gallery.CurrentPage.First(i => i.Name != ExifDateFileName && !i.IsArchive);
        var before = (await DbRowsAsync("SELECT CapturedAt, CapturedAtSource, CapturedAtPrecision FROM Files WHERE Id = $id", ("$id", target.Id)).ConfigureAwait(true)).Single();
        _gallery.ClearSelectionCommand.Execute(null);
        target.IsSelected = true;
        await WaitUntilAsync(() => _gallery.SelectedCount == 1, ShortWait, "eine Datei ausgewählt").ConfigureAwait(true);
        _captureDialog!.Next = new CaptureDateDialogResult(new CaptureDateEdit.Approximate(1982, null), false);
        await _gallery.SetSelectionCaptureDateCommand.ExecuteAsync(null).ConfigureAwait(true);
        Check(_captureDialog.LastRequest is { FileCount: 1 }, $"Dialog mit 1 Datei aufgerufen ({_captureDialog.LastRequest?.Subject})");
        var manual = (await DbRowsAsync("SELECT CapturedAtSource, CapturedAtPrecision FROM Files WHERE Id = $id", ("$id", target.Id)).ConfigureAwait(true)).Single();
        Check(manual == "4 | 3", $"Datum gesetzt: Herkunft/Genauigkeit {manual} (erwartet 4 | 3 = manuell, Jahr)");
        await SetSortAsync(GallerySort.CapturedAsc).ConfigureAwait(true);
        await WaitUntilAsync(() => _gallery.CurrentPage.Count > 1 && _gallery.CurrentPage[1].Id == target.Id, ShortWait, "ca. 1982 direkt nach 1975").ConfigureAwait(true);
        Check(_gallery.CurrentPage[1].CaptureDateText == "ca. 1982", $"Listentext „{_gallery.CurrentPage[1].CaptureDateText}“");
        await SnapshotAsync("26-galerie-aufnahmedatum").ConfigureAwait(true);

        Check(_gallery.CanUndo, "Rückgängig verfügbar");
        await _gallery.UndoCommand.ExecuteAsync(null).ConfigureAwait(true);
        await WaitUntilAsync(() => !_gallery.IsOperationRunning, ShortWait, "Rückgängig beendet").ConfigureAwait(true);
        var after = (await DbRowsAsync("SELECT CapturedAt, CapturedAtSource, CapturedAtPrecision FROM Files WHERE Id = $id", ("$id", target.Id)).ConfigureAwait(true)).Single();
        Check(after == before, $"Rückgängig: alter Wert zurück ({after}, vorher {before})");
        var batch = await DbScalarAsync<int>("SELECT Status FROM OperationBatches WHERE OperationType = 7 ORDER BY Id DESC LIMIT 1").ConfigureAwait(true);
        Check(batch == 4, $"Protokoll: Batch „Datum setzen“ zurückgenommen (Status {batch})");

        _gallery.ClearSelectionCommand.Execute(null);
        await SetSortAsync(GallerySort.ModifiedDesc).ConfigureAwait(true);
    }

    /// <summary>Kleines JPEG mit EXIF DateTimeOriginal (Datei-Änderungsdatum = jetzt).</summary>
    private static void WriteExifDateJpeg(string path, string dateTimeOriginal)
    {
        var pixels = new byte[64 * 48 * 4];
        Array.Fill(pixels, (byte)180);
        var source = BitmapSource.Create(64, 48, 96, 96, PixelFormats.Bgra32, null, pixels, 64 * 4);
        var metadata = new BitmapMetadata("jpg");
        metadata.SetQuery("/app1/ifd/exif/{ushort=36867}", dateTimeOriginal);
        var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
        encoder.Frames.Add(BitmapFrame.Create(source, null, metadata, null));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
