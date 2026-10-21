using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lumora.Services.Faces;
using Lumora.Thumbnails;

namespace Lumora.Faces;

/// <summary>
/// WIC-Lader für die Gesichtserkennung (ADR-0035, Implementierung von <see cref="IFaceImageLoader"/>): wie der
/// <see cref="WicImageThumbnailRenderer"/> zwei Durchgänge — Kopf (Maße, EXIF-Orientierung) ohne Pixel, dann skaliertes
/// Dekodieren mit der längeren GESPEICHERTEN Seite auf <c>maxEdge</c> (JPEG dekodiert dabei verkleinert, schnell),
/// danach Orientierung anwenden und nach RGB24 wandeln. Datei geteilt geöffnet (Cloud-Sync-tolerant), Ergebnis ohne
/// WPF-Typen. Thread-sicher (keine gemeinsamen Objekte).
/// </summary>
public sealed class WicFaceImageLoader : IFaceImageLoader
{
    public FaceImage Load(string path, int maxEdge)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Bilddatei fehlt.", path);
        }

        try
        {
            return Decode(path, maxEdge);
        }
        catch (Exception ex) when (ex is not FileNotFoundException and not DirectoryNotFoundException)
        {
            throw new FaceImageDecodeException($"Bild nicht dekodierbar: {ex.Message}", ex);
        }
    }

    private static FaceImage Decode(string path, int maxEdge)
    {
        int storedWidth;
        int storedHeight;
        int orientation;
        using (var header = OpenShared(path))
        {
            var decoder = BitmapDecoder.Create(header, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
            var first = decoder.Frames[0];
            storedWidth = first.PixelWidth;
            storedHeight = first.PixelHeight;
            orientation = WicImageThumbnailRenderer.ReadOrientation(first.Metadata as BitmapMetadata);
        }

        var bitmap = new BitmapImage();
        using (var stream = OpenShared(path))
        {
            bitmap.BeginInit();
            bitmap.StreamSource = stream;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (storedWidth >= storedHeight)
            {
                if (storedWidth > maxEdge)
                {
                    bitmap.DecodePixelWidth = maxEdge;
                }
            }
            else if (storedHeight > maxEdge)
            {
                bitmap.DecodePixelHeight = maxEdge;
            }

            bitmap.EndInit();
        }

        bitmap.Freeze();
        BitmapSource oriented = WicImageThumbnailRenderer.ApplyOrientation(bitmap, orientation);
        BitmapSource rgb = oriented.Format == PixelFormats.Rgb24
            ? oriented
            : new FormatConvertedBitmap(oriented, PixelFormats.Rgb24, null, 0);

        var width = rgb.PixelWidth;
        var height = rgb.PixelHeight;
        var pixels = new byte[width * height * 3];
        rgb.CopyPixels(pixels, width * 3, 0);

        var swap = orientation is >= 5 and <= 8;
        return new FaceImage(pixels, width, height, swap ? storedHeight : storedWidth, swap ? storedWidth : storedHeight);
    }

    private static FileStream OpenShared(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
}
