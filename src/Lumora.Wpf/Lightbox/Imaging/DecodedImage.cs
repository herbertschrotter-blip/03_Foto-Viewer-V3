using System;
using System.Windows.Media.Imaging;

namespace Lumora.Lightbox.Imaging;

/// <summary>EXIF-Auszug für das Info-Panel. Alle Werte optional (PNG hat i. d. R. keine EXIF-Daten).</summary>
public sealed record ImageExifInfo(
    string? Camera,
    string? Lens,
    DateTime? DateTaken,
    double? ExposureSeconds,
    double? FNumber,
    int? Iso,
    double? FocalLengthMm,
    int Orientation)
{
    public static readonly ImageExifInfo Empty = new(null, null, null, null, null, null, null, 1);

    public bool HasAny => Camera is not null || Lens is not null || DateTaken is not null
        || ExposureSeconds is not null || FNumber is not null || Iso is not null || FocalLengthMm is not null;
}

/// <summary>
/// Ergebnis eines Lightbox-Decodes: eingefrorene, bereits EXIF-orientierte BitmapSource plus
/// Pixelmaße (nach Orientierung) und Quell-Pixelmaße (wie gespeichert).
/// </summary>
public sealed class DecodedImage
{
    public DecodedImage(BitmapSource bitmap, int sourceWidth, int sourceHeight, bool wasDownscaled, ImageExifInfo exif)
    {
        Bitmap = bitmap;
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
        WasDownscaled = wasDownscaled;
        Exif = exif;
    }

    /// <summary>Eingefrorene Bitmap (thread-sicher), Orientierung bereits angewandt.</summary>
    public BitmapSource Bitmap { get; }

    /// <summary>Pixelbreite der gespeicherten Datei (vor Orientierung, vor Decode-Limit).</summary>
    public int SourceWidth { get; }

    public int SourceHeight { get; }

    /// <summary>Anzeige-Pixelmaße nach EXIF-Orientierung (Quellmaße, ggf. getauscht).</summary>
    public int DisplayWidth => Exif.Orientation is 5 or 6 or 7 or 8 ? SourceHeight : SourceWidth;

    public int DisplayHeight => Exif.Orientation is 5 or 6 or 7 or 8 ? SourceWidth : SourceHeight;

    /// <summary>True, wenn wegen des Decode-Limits kleiner als die Quelle dekodiert wurde.</summary>
    public bool WasDownscaled { get; }

    public ImageExifInfo Exif { get; }
}
