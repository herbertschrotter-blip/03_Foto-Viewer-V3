using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Lumora.Lightbox.Imaging;

/// <summary>
/// Decodiert ein Bild für die Lightbox auf einem Hintergrund-Thread (WIC-Muster aus Spike #4 /
/// <c>WicImageThumbnailRenderer</c>): eigener FileStream mit <c>FileShare.ReadWrite | Delete</c>,
/// <c>BitmapCacheOption.OnLoad</c>, <c>Freeze()</c>. Liest vorab Pixelmaße + EXIF (Orientierung,
/// Kamera, Belichtung …) aus dem Container und wendet die EXIF-Orientierung an (Rotation beim
/// Decode, Spiegelungen via TransformedBitmap). Das Decode-Limit greift nur, wenn die Quelle
/// breiter als <paramref name="maxDecodePixelWidth"/> ist, damit „100 %“ sinnvoll bleibt.
/// </summary>
internal static class LightboxImageDecoder
{
    public static DecodedImage Decode(string path, int maxDecodePixelWidth, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Datei nicht gefunden.", path);
        }

        // Pass 1: Header + Metadaten (kein Pixel-Decode, CacheOption.None).
        int sourceWidth;
        int sourceHeight;
        ImageExifInfo exif;
        using (var headerStream = OpenShared(path))
        {
            var decoder = BitmapDecoder.Create(
                headerStream,
                BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            sourceWidth = frame.PixelWidth;
            sourceHeight = frame.PixelHeight;
            exif = ReadExif(frame.Metadata as BitmapMetadata);
        }

        ct.ThrowIfCancellationRequested();

        // Pass 2: Decode mit Orientierung + optionalem Limit.
        var rotation = exif.Orientation switch
        {
            3 => Rotation.Rotate180,
            5 or 6 => Rotation.Rotate90,
            7 or 8 => Rotation.Rotate270,
            _ => Rotation.Rotate0,
        };
        var flipHorizontal = exif.Orientation is 2 or 5 or 7;
        var flipVertical = exif.Orientation == 4;
        var downscaled = maxDecodePixelWidth > 0 && sourceWidth > maxDecodePixelWidth;

        BitmapSource source;
        using (var stream = OpenShared(path))
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = stream;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.Rotation = rotation;
            if (downscaled)
            {
                bitmap.DecodePixelWidth = maxDecodePixelWidth;
            }

            bitmap.EndInit();
            bitmap.Freeze();
            source = bitmap;
        }

        ct.ThrowIfCancellationRequested();

        if (flipHorizontal || flipVertical)
        {
            var transformed = new TransformedBitmap(
                source,
                new ScaleTransform(flipHorizontal ? -1 : 1, flipVertical ? -1 : 1));
            transformed.Freeze();

            // Spiegelung einmal materialisieren, damit der UI-Thread nicht bei jedem Frame transformiert.
            var cached = new CachedBitmap(transformed, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            cached.Freeze();
            source = cached;
        }

        return new DecodedImage(source, sourceWidth, sourceHeight, downscaled, exif);
    }

    /// <summary>Liest nur die Metadaten (für Info-Panel-Refresh ohne erneuten Pixel-Decode).</summary>
    public static (int Width, int Height, ImageExifInfo Exif) ReadHeader(string path)
    {
        using var stream = OpenShared(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        return (frame.PixelWidth, frame.PixelHeight, ReadExif(frame.Metadata as BitmapMetadata));
    }

    private static FileStream OpenShared(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);

    private static ImageExifInfo ReadExif(BitmapMetadata? metadata)
    {
        if (metadata is null)
        {
            return ImageExifInfo.Empty;
        }

        var orientation = (int)(ReadDouble(metadata, "System.Photo.Orientation") ?? 1);
        if (orientation is < 1 or > 8)
        {
            orientation = 1;
        }

        var make = ReadString(metadata, "System.Photo.CameraManufacturer");
        var model = ReadString(metadata, "System.Photo.CameraModel");
        string? camera = null;
        if (!string.IsNullOrWhiteSpace(model))
        {
            camera = !string.IsNullOrWhiteSpace(make)
                && !model.StartsWith(make, StringComparison.OrdinalIgnoreCase)
                    ? $"{make.Trim()} {model.Trim()}"
                    : model.Trim();
        }
        else if (!string.IsNullOrWhiteSpace(make))
        {
            camera = make.Trim();
        }

        var iso = ReadDouble(metadata, "System.Photo.ISOSpeed");

        return new ImageExifInfo(
            Camera: camera,
            Lens: ReadString(metadata, "System.Photo.LensModel"),
            DateTaken: ReadDate(metadata),
            ExposureSeconds: ReadDouble(metadata, "System.Photo.ExposureTime"),
            FNumber: ReadDouble(metadata, "System.Photo.FNumber"),
            Iso: iso is > 0 ? (int)iso.Value : null,
            FocalLengthMm: ReadDouble(metadata, "System.Photo.FocalLength"),
            Orientation: orientation);
    }

    private static object? Query(BitmapMetadata metadata, string query)
    {
        try
        {
            return metadata.ContainsQuery(query) ? metadata.GetQuery(query) : null;
        }
        catch
        {
            // Container ohne diese Metadaten-Policy (z. B. PNG) → kein Wert.
            return null;
        }
    }

    private static string? ReadString(BitmapMetadata metadata, string query)
    {
        var value = Query(metadata, query);
        var text = value switch
        {
            string s => s,
            string[] arr when arr.Length > 0 => string.Join(" ", arr),
            _ => value?.ToString(),
        };
        text = text?.Trim('\0', ' ', '\t', '\r', '\n');
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static double? ReadDouble(BitmapMetadata metadata, string query)
    {
        var value = Query(metadata, query);
        return value switch
        {
            double d => d,
            float f => f,
            ushort us => us,
            short s => s,
            uint ui => ui,
            int i => i,
            ulong ul => ul,
            long l => l,
            byte b => b,
            string str when double.TryParse(str, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    private static DateTime? ReadDate(BitmapMetadata metadata)
    {
        var value = Query(metadata, "System.Photo.DateTaken");
        switch (value)
        {
            case FILETIME ft:
                try
                {
                    var ticks = ((long)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;
                    return DateTime.FromFileTime(ticks);
                }
                catch
                {
                    return null;
                }

            case DateTime dt:
                return dt;

            case string s:
                if (DateTime.TryParseExact(s, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var exact))
                {
                    return exact;
                }

                return DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var parsed) ? parsed : null;
        }

        // Fallback: WPF-Komfortproperty (bereits als kulturabhängiger String).
        try
        {
            var text = metadata.DateTaken;
            if (!string.IsNullOrWhiteSpace(text)
                && DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var fallback))
            {
                return fallback;
            }
        }
        catch
        {
            // ignorieren
        }

        return null;
    }
}
