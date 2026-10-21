using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Lumora.Lightbox.Imaging;

namespace Lumora.Slideshow;

/// <summary>Dekodiertes Diashow-Bild: eingefroren (thread-sicher), EXIF-orientiert.</summary>
public sealed record SlideshowImage(BitmapSource Bitmap)
{
    public int PixelWidth => Bitmap.PixelWidth;

    public int PixelHeight => Bitmap.PixelHeight;

    /// <summary>Breite / Höhe (≥ 1 Pixel angenommen).</summary>
    public double Aspect => (double)Math.Max(1, PixelWidth) / Math.Max(1, PixelHeight);
}

/// <summary>Dekodiert Bilder für die Diashow (Hintergrund-Thread, nie <c>ImageSource</c> in Services).</summary>
public interface ISlideshowImageDecoder
{
    /// <summary>
    /// Bild so groß dekodiert, dass es eingepasst in <paramref name="maxWidth"/> × <paramref name="maxHeight"/> Pixel (Bildschirm
    /// × Skalierung × Zoom-Reserve) scharf bleibt — nie größer als die Quelle. Fehlende/kaputte Datei → Ausnahme.
    /// </summary>
    Task<SlideshowImage> DecodeAsync(string path, int maxWidth, int maxHeight, CancellationToken ct);
}

/// <summary>
/// WIC-Dekodierung über den Lightbox-Decoder (eigener FileStream, <c>BitmapCacheOption.OnLoad</c>, <c>Freeze</c>,
/// EXIF-Orientierung): erst die Kopfdaten (Maße, Orientierung), dann mit Dekodier-Breite so, dass das eingepasste Bild die
/// Bildschirmfläche gerade füllt. Ein 24-MP-Foto belegt auf einem 4K-Schirm so ≈ 4096 × 2304 statt 6000 × 4000 Pixel.
/// </summary>
public sealed class SlideshowImageDecoder : ISlideshowImageDecoder
{
    public Task<SlideshowImage> DecodeAsync(string path, int maxWidth, int maxHeight, CancellationToken ct)
        => Task.Run(() => Decode(path, maxWidth, maxHeight, ct), ct);

    internal static SlideshowImage Decode(string path, int maxWidth, int maxHeight, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Datei nicht gefunden.", path);
        }

        var (width, height, exif) = LightboxImageDecoder.ReadHeader(path);
        var decodeWidth = DecodeWidth(width, height, exif.Orientation, maxWidth, maxHeight);
        ct.ThrowIfCancellationRequested();
        var decoded = LightboxImageDecoder.Decode(path, decodeWidth, ct);
        return new SlideshowImage(decoded.Bitmap);
    }

    /// <summary>
    /// Dekodier-Breite in Quellpixeln (vor der Orientierung): so klein, dass das eingepasste Bild <paramref name="maxWidth"/> ×
    /// <paramref name="maxHeight"/> gerade füllt, nie größer als die Quelle.
    /// </summary>
    public static int DecodeWidth(int sourceWidth, int sourceHeight, int orientation, int maxWidth, int maxHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || maxWidth <= 0 || maxHeight <= 0)
        {
            return sourceWidth;
        }

        var rotated = orientation is 5 or 6 or 7 or 8;
        var displayWidth = rotated ? sourceHeight : sourceWidth;
        var displayHeight = rotated ? sourceWidth : sourceHeight;
        var scale = Math.Min(1.0, Math.Min((double)maxWidth / displayWidth, (double)maxHeight / displayHeight));
        return Math.Max(1, (int)Math.Ceiling(sourceWidth * scale));
    }
}
