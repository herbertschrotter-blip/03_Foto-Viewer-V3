using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lumora.Gallery.ViewModels;
using Lumora.Services.Scanning;
using Lumora.Services.Thumbnails;

namespace Lumora.Shell.Smoke;

/// <summary>
/// Rauchtest-Schritt „Vorschaubild-Größe und Bildmaße“ (0.2.45, ADR-0043): ein im Schritt erzeugtes Hochkant-Foto mit
/// EXIF-Orientierung 6 (gespeichert 1200 × 600) bekommt ein Vorschaubild mit kurzer Kante 400 (400 × 800) und
/// <c>Files.Width/Height</c> = 600 × 1200 (wie angezeigt). Für alle Bilder gilt: kurze Kante des Vorschaubilds =
/// min(400, Original), lange höchstens 1200, Maße gesetzt; Videos tragen ihre Maße aus FFmpeg; keine alte Größe mehr.
/// Die Kacheln laden passend zu Kachelgröße × Bildschirm-Skalierung und dekodieren nach dem Vergrößern neu.
/// </summary>
internal sealed partial class SmokeScenario
{
    private const string Exif6FileName = "smoke-exif6-hochkant.jpg";

    private async Task ThumbnailSizeAsync()
    {
        // (1) EXIF-6-Foto anlegen, Incremental-Scan, Galerie fordert das Vorschaubild an.
        var path = Path.Combine(_root, "Unsortiert", Exif6FileName);
        WriteExif6Jpeg(path, 1200, 600);
        _lastScan = null;
        await _library.RunSmokeScanAsync(_root).ConfigureAwait(true);
        Check(_lastScan is { Mode: ScanMode.Incremental, Outcome: ScanOutcome.Completed, Added: 1 }, $"EXIF-6-Foto aufgenommen (Added={_lastScan?.Added})");
        _mediaCount++;
        await WaitUntilAsync(
            () => !_gallery.IsLoading && _gallery.TotalCount == _mediaCount && _gallery.CurrentPage.Any(i => i.Name == Exif6FileName),
            ShortWait,
            $"Galerie zeigt {Exif6FileName}").ConfigureAwait(true);
        var item = _gallery.CurrentPage.First(i => i.Name == Exif6FileName);
        await WaitUntilAsync(() => item.Thumbnail is not null, LongWait, "Vorschaubild des EXIF-6-Fotos geladen").ConfigureAwait(true);

        var dims = await DbRowsAsync("SELECT Width, Height FROM Files WHERE Id = $id", ("$id", item.Id)).ConfigureAwait(true);
        Check(dims.SingleOrDefault() == "600 | 1200", $"Files.Width/Height des EXIF-6-Fotos: {dims.SingleOrDefault()} (erwartet 600 | 1200, wie angezeigt)");
        var thumbPath = await DbScalarAsync<string>(
            "SELECT CachePath FROM Thumbnails WHERE FileId = $id AND Size = $s AND Status = 2",
            ("$id", item.Id),
            ("$s", ThumbnailService.Size)).ConfigureAwait(true);
        var thumb = ReadImageSize(thumbPath);
        Check(thumb == (400, 800), $"Vorschaubild des EXIF-6-Fotos: {thumb.Width} × {thumb.Height} (erwartet 400 × 800, gedreht)");

        // (2) Alle Bilder mit Vorschaubild: kurze Kante = min(400, Original), lange ≤ 1200, Maße gesetzt.
        var rows = await DbRowsAsync(
            "SELECT f.Name, f.MediaType, IFNULL(f.Width, 0), IFNULL(f.Height, 0), t.CachePath FROM Thumbnails t JOIN Files f ON f.Id = t.FileId " +
            "WHERE t.Size = $s AND t.Status = 2 AND f.LibraryId = $lib",
            ("$s", ThumbnailService.Size),
            ("$lib", _libraryId)).ConfigureAwait(true);
        var images = 0;
        var videos = 0;
        foreach (var row in rows)
        {
            var cells = row.Split(" | ");
            var (mediaType, width, height) = (int.Parse(cells[1], CultureInfo.InvariantCulture), int.Parse(cells[2], CultureInfo.InvariantCulture), int.Parse(cells[3], CultureInfo.InvariantCulture));
            var size = ReadImageSize(cells[4]);
            if (mediaType == 1)
            {
                videos++;
                Check(width > 0 && height > 0, $"Video {cells[0]}: Maße aus FFmpeg {width} × {height}, Vorschaubild {size.Width} × {size.Height}");
                Check(Math.Min(size.Width, size.Height) <= ThumbnailService.Size + 1, $"Video {cells[0]}: kurze Kante ≤ {ThumbnailService.Size}");
                continue;
            }

            images++;
            var expectedShort = Math.Min(ThumbnailService.Size, Math.Min(width, height));
            var shortEdge = Math.Min(size.Width, size.Height);
            var longEdge = Math.Max(size.Width, size.Height);
            if (width <= 0 || height <= 0 || Math.Abs(shortEdge - expectedShort) > 1 || longEdge > ThumbnailSizing.MaxLongEdge(ThumbnailService.Size))
            {
                Check(false, $"Bild {cells[0]}: Original {width} × {height}, Vorschaubild {size.Width} × {size.Height} (kurze Kante erwartet {expectedShort})");
            }
        }

        Check(images >= 20, $"{images} Bilder: kurze Kante = min(400, Original), lange ≤ 1200, Files.Width/Height gesetzt; {videos} Videos mit Maßen");
        var legacy = await DbScalarAsync<int>("SELECT COUNT(*) FROM Thumbnails WHERE Size <> $s", ("$s", ThumbnailService.Size)).ConfigureAwait(true);
        Check(legacy == 0, $"keine Vorschaubilder alter Größe ({legacy})");

        // (3) Kacheln: Dekodierfläche = Kachel × Skalierung; nach dem Vergrößern scharf neu dekodiert.
        var dpi = VisualTreeHelper.GetDpi(Application.Current.MainWindow!).DpiScaleX;
        var expectedBox = ThumbnailDecodeBox.FromTile(_gallery.TileWidth, _gallery.TileHeight, dpi);
        Check(_gallery.ThumbnailDecodeBox == expectedBox, $"Dekodierfläche {_gallery.ThumbnailDecodeBox.Width} × {_gallery.ThumbnailDecodeBox.Height} px = Kachel {_gallery.TileWidth} × {_gallery.TileHeight} × {dpi:P0}");
        Check(Covers(item, expectedBox, 400, 800), $"Kachelbild {Describe(item)} bedeckt {expectedBox.Width} × {expectedBox.Height} (bisher fest 180 breit)");

        var previousTileSize = _gallery.TileSize;
        _gallery.TileSize = GalleryViewModel.MaxTileSize;
        var bigBox = ThumbnailDecodeBox.FromTile(_gallery.TileWidth, _gallery.TileHeight, dpi);
        await WaitUntilAsync(() => Covers(item, bigBox, 400, 800), ShortWait, $"nach dem Vergrößern neu dekodiert ({Describe(item)}, Fläche {bigBox.Width} × {bigBox.Height})").ConfigureAwait(true);
        Check(true, $"größte Kachel: Kachelbild {Describe(item)} bedeckt {bigBox.Width} × {bigBox.Height}");
        await SnapshotAsync("25-galerie-vorschaubilder-gross").ConfigureAwait(true);
        _gallery.TileSize = previousTileSize;
    }

    /// <summary>Kachelbild bedeckt die Fläche (oder hat die volle Größe des Vorschaubilds): UniformToFill skaliert nicht hoch.</summary>
    private static bool Covers(GalleryItem item, ThumbnailDecodeBox box, int thumbWidth, int thumbHeight)
        => item.Thumbnail is BitmapSource bitmap
           && bitmap.PixelWidth >= Math.Min(box.Width, thumbWidth) - 1
           && bitmap.PixelHeight >= Math.Min(box.Height, thumbHeight) - 1;

    private static string Describe(GalleryItem item)
        => item.Thumbnail is BitmapSource bitmap ? $"{bitmap.PixelWidth} × {bitmap.PixelHeight}" : "(kein Bild)";

    private static (int Width, int Height) ReadImageSize(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return (0, 0);
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
        return (frame.PixelWidth, frame.PixelHeight);
    }

    /// <summary>Querformat-Pixel (links rot, rechts blau) mit EXIF-Orientierung 6 — angezeigt Hochkant.</summary>
    private static void WriteExif6Jpeg(string path, int width, int height)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * stride) + (x * 4);
                var left = x < width / 2;
                pixels[i] = left ? (byte)0 : (byte)255;
                pixels[i + 2] = left ? (byte)255 : (byte)0;
                pixels[i + 3] = 255;
            }
        }

        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        var metadata = new BitmapMetadata("jpg");
        metadata.SetQuery("/app1/ifd/{ushort=274}", (ushort)6);
        var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
        encoder.Frames.Add(BitmapFrame.Create(source, null, metadata, null));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
