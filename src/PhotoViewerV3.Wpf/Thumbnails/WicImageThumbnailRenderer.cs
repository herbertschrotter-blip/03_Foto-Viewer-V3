using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoViewerV3.Services.Thumbnails;

namespace PhotoViewerV3.Thumbnails;

/// <summary>
/// WIC-basierte Implementierung von <see cref="IImageThumbnailRenderer"/> für JPG/PNG/GIF/BMP/TIFF.
/// Production-Pattern aus Spike #4: expliziter <see cref="FileStream"/> mit
/// <c>FileShare.ReadWrite | FileShare.Delete</c> + <c>BitmapCacheOption.OnLoad</c>
/// (Cloud-Sync-tolerant, Lock direkt nach Read frei), <c>Freeze()</c> für Worker-Threads,
/// atomarer <c>.tmp</c>+Move. Zusätzlich:
/// <list type="bullet">
/// <item>EXIF-Orientierung (System.Photo.Orientation 1–8) wird angewandt — Hochkant-Handyfotos sonst gedreht.</item>
/// <item>Die längere Seite der gespeicherten Pixel wird auf die Zielgröße dekodiert (Hochkant: DecodePixelHeight),
/// kleinere Bilder werden nicht hochskaliert.</item>
/// <item>Bilder mit Alphakanal werden vor dem JPEG-Encode auf <see cref="AlphaBackground"/> gelegt
/// (sonst schwarze Flächen/Artefakte).</item>
/// </list>
/// Fehlercodes: <c>source_missing</c>, <c>wic_decode_failed</c> (löst den FFmpeg-Fallback im ThumbnailService aus)
/// und <see cref="EncodeFailedErrorCode"/> für Fehler beim Schreiben (kein Fallback nötig).
/// Liegt im Wpf-Projekt, weil System.Windows.Media.Imaging WPF-Framework-Referenzen braucht.
/// </summary>
public sealed class WicImageThumbnailRenderer : IImageThumbnailRenderer
{
    private const int JpegQuality = 80;

    /// <summary>Decode und Orientierung gelangen, aber JPEG-Encode/Schreiben scheiterte.</summary>
    public const string EncodeFailedErrorCode = "wic_encode_failed";

    /// <summary>Hintergrund für transparente Bereiche (PNG/GIF mit Alpha), passend zum dunklen Kachelhintergrund.</summary>
    public static readonly Color AlphaBackground = Color.FromRgb(0x20, 0x20, 0x20);

    public ThumbnailRenderResult Render(string sourcePath, string targetThumbPath, int targetWidth)
    {
        if (!File.Exists(sourcePath))
        {
            return ThumbnailRenderResult.Fail(ThumbnailErrorCodes.SourceMissing, $"Quelldatei fehlt: {sourcePath}");
        }

        BitmapSource frame;
        try
        {
            frame = DecodeThumbnail(sourcePath, targetWidth);
        }
        catch (Exception ex)
        {
            return ThumbnailRenderResult.Fail(ThumbnailErrorCodes.WicDecodeFailed, ex.Message);
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetThumbPath)!);
            var tempPath = targetThumbPath + ".tmp";
            var encoder = new JpegBitmapEncoder { QualityLevel = JpegQuality };
            encoder.Frames.Add(BitmapFrame.Create(frame));
            using (var stream = File.Create(tempPath))
            {
                encoder.Save(stream);
            }

            File.Move(tempPath, targetThumbPath, overwrite: true);
            var info = new FileInfo(targetThumbPath);
            return ThumbnailRenderResult.Ok(frame.PixelWidth, frame.PixelHeight, info.Length);
        }
        catch (Exception ex)
        {
            return ThumbnailRenderResult.Fail(EncodeFailedErrorCode, ex.Message);
        }
    }

    /// <summary>
    /// Decodiert die Datei auf Thumbnail-Größe (längere Seite = <paramref name="targetSize"/>, kein Hochskalieren),
    /// wendet die EXIF-Orientierung an und legt Alpha auf den dunklen Hintergrund. Ergebnis ist eingefroren.
    /// </summary>
    internal static BitmapSource DecodeThumbnail(string sourcePath, int targetSize)
    {
        // Pass 1: nur Header + Metadaten (Pixelmaße, Orientierung) — kein Pixel-Decode.
        int sourceWidth;
        int sourceHeight;
        int orientation;
        using (var header = OpenShared(sourcePath))
        {
            var decoder = BitmapDecoder.Create(header, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
            var first = decoder.Frames[0];
            sourceWidth = first.PixelWidth;
            sourceHeight = first.PixelHeight;
            orientation = ReadOrientation(first.Metadata as BitmapMetadata);
        }

        // Pass 2: Decode mit Zielgröße auf der längeren Seite der GESPEICHERTEN Pixel (Rotation folgt danach).
        var bitmap = new BitmapImage();
        using (var stream = OpenShared(sourcePath))
        {
            bitmap.BeginInit();
            bitmap.StreamSource = stream;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (sourceWidth >= sourceHeight)
            {
                if (sourceWidth > targetSize)
                {
                    bitmap.DecodePixelWidth = targetSize;
                }
            }
            else if (sourceHeight > targetSize)
            {
                bitmap.DecodePixelHeight = targetSize;
            }

            bitmap.EndInit();
        }

        bitmap.Freeze();

        var result = ApplyOrientation(bitmap, orientation);
        if (!IsOpaque(result.Format))
        {
            result = FlattenAlpha(result, AlphaBackground);
        }

        if (!result.IsFrozen)
        {
            result.Freeze();
        }

        return result;
    }

    private static FileStream OpenShared(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);

    /// <summary>EXIF-Orientierung 1–8 (1 = unverändert); Container ohne Photo-Metadaten (PNG) liefern 1.</summary>
    internal static int ReadOrientation(BitmapMetadata? metadata)
    {
        if (metadata is null)
        {
            return 1;
        }

        foreach (var query in new[] { "System.Photo.Orientation", "/app1/ifd/{ushort=274}" })
        {
            try
            {
                if (!metadata.ContainsQuery(query))
                {
                    continue;
                }

                var orientation = metadata.GetQuery(query) switch
                {
                    ushort us => us,
                    short s => s,
                    int i => i,
                    uint ui => (int)ui,
                    byte b => b,
                    long l => (int)l,
                    _ => 0,
                };
                if (orientation is >= 1 and <= 8)
                {
                    return orientation;
                }
            }
            catch
            {
                // Metadaten-Policy für diesen Container nicht verfügbar (z. B. PNG/BMP) → nächste Abfrage / 1.
            }
        }

        return 1;
    }

    /// <summary>
    /// Wendet die EXIF-Orientierung als Rotation (90°-Schritte, im Uhrzeigersinn) und Spiegelung an.
    /// Gleiche Zuordnung wie der Lightbox-Decoder: 5/7 = Rotation, danach horizontal gespiegelt.
    /// </summary>
    internal static BitmapSource ApplyOrientation(BitmapSource source, int orientation)
    {
        var (angle, flipHorizontal, flipVertical) = orientation switch
        {
            2 => (0.0, true, false),
            3 => (180.0, false, false),
            4 => (0.0, false, true),
            5 => (90.0, true, false),
            6 => (90.0, false, false),
            7 => (270.0, true, false),
            8 => (270.0, false, false),
            _ => (0.0, false, false),
        };

        if (angle == 0 && !flipHorizontal && !flipVertical)
        {
            return source;
        }

        var group = new TransformGroup();
        if (angle != 0)
        {
            group.Children.Add(new RotateTransform(angle));
        }

        if (flipHorizontal || flipVertical)
        {
            group.Children.Add(new ScaleTransform(flipHorizontal ? -1 : 1, flipVertical ? -1 : 1));
        }

        var transformed = new TransformedBitmap(source, group);
        transformed.Freeze();
        return transformed;
    }

    /// <summary>Formate ohne Alphakanal; alles andere (Bgra32, Pbgra32, Rgba64, Indexed mit Palette …) wird flachgelegt.</summary>
    internal static bool IsOpaque(PixelFormat format)
        => format == PixelFormats.Bgr24
           || format == PixelFormats.Bgr32
           || format == PixelFormats.Rgb24
           || format == PixelFormats.Bgr101010
           || format == PixelFormats.Bgr555
           || format == PixelFormats.Bgr565
           || format == PixelFormats.Rgb48
           || format == PixelFormats.Rgb128Float
           || format == PixelFormats.Gray8
           || format == PixelFormats.Gray16
           || format == PixelFormats.Gray32Float
           || format == PixelFormats.Gray2
           || format == PixelFormats.Gray4
           || format == PixelFormats.BlackWhite
           || format == PixelFormats.Cmyk32;

    /// <summary>Legt das Bild per Alpha-Blending auf eine deckende Hintergrundfarbe (Ergebnis Bgr32, eingefroren).</summary>
    internal static BitmapSource FlattenAlpha(BitmapSource source, Color background)
    {
        BitmapSource bgra = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        var width = bgra.PixelWidth;
        var height = bgra.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        bgra.CopyPixels(pixels, stride, 0);

        for (var i = 0; i < pixels.Length; i += 4)
        {
            var alpha = pixels[i + 3];
            if (alpha == 255)
            {
                continue;
            }

            var inverse = 255 - alpha;
            pixels[i] = (byte)((pixels[i] * alpha + background.B * inverse) / 255);
            pixels[i + 1] = (byte)((pixels[i + 1] * alpha + background.G * inverse) / 255);
            pixels[i + 2] = (byte)((pixels[i + 2] * alpha + background.R * inverse) / 255);
            pixels[i + 3] = 255;
        }

        var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, stride);
        result.Freeze();
        return result;
    }
}
