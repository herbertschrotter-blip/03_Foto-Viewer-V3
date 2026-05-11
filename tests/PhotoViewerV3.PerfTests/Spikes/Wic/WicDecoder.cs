using System;
using System.IO;
using System.Windows.Media.Imaging;

namespace PhotoViewerV3.PerfTests.Spikes.Wic;

internal sealed record DecodeResult(int Width, int Height);

/// <summary>
/// WIC-Decoder-Helper für Thumbnail-Erzeugung. Verschiedene Modi entsprechen dem
/// Production-Pattern aus PLAN.md Kap. 13 (OnLoad + Cloud-Sync-tolerante FileShare-Flags).
/// </summary>
internal static class WicDecoder
{
    /// <summary>
    /// Production-Pattern: expliziter FileStream mit FileShare.ReadWrite|FileShare.Delete +
    /// BitmapCacheOption.OnLoad → File-Lock direkt nach Read freigegeben, Cloud-Sync-tolerant.
    /// </summary>
    public static DecodeResult DecodeViaSharedStream(string path, int targetWidth)
    {
        using var fs = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return DecodeFromStream(fs, targetWidth);
    }

    /// <summary>
    /// Naive Variante via UriSource — zur Vergleichsmessung. Hält File-Lock länger,
    /// nicht Cloud-Sync-tolerant.
    /// </summary>
    public static DecodeResult DecodeViaUri(string path, int targetWidth)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.UriSource = new Uri(path);
        bitmap.DecodePixelWidth = targetWidth;
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bitmap.EndInit();
        bitmap.Freeze();
        return new DecodeResult(bitmap.PixelWidth, bitmap.PixelHeight);
    }

    /// <summary>
    /// Variante OHNE OnLoad — illustriert den Lock-Bug. Stream-Source wird verzögert
    /// gelesen, FileStream-Dispose innerhalb des using führt zu Lock-Konflikten.
    /// Nur für Vergleichsmessung, NICHT produktiv verwenden.
    /// </summary>
    public static DecodeResult DecodeViaSharedStreamNoOnLoad(string path, int targetWidth)
    {
        using var fs = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.StreamSource = fs;
        bitmap.DecodePixelWidth = targetWidth;
        // bewusst KEIN OnLoad — soll Lock-Bug zeigen
        bitmap.CacheOption = BitmapCacheOption.Default;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bitmap.EndInit();
        bitmap.Freeze();
        return new DecodeResult(bitmap.PixelWidth, bitmap.PixelHeight);
    }

    private static DecodeResult DecodeFromStream(Stream stream, int targetWidth)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.StreamSource = stream;
        bitmap.DecodePixelWidth = targetWidth;
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bitmap.EndInit();
        bitmap.Freeze();
        return new DecodeResult(bitmap.PixelWidth, bitmap.PixelHeight);
    }

    /// <summary>
    /// Decode + Encode + atomic Move (.tmp → final). Pattern aus PLAN Kap. 13.
    /// </summary>
    public static DecodeResult DecodeAndSaveAtomic(string sourcePath, string thumbPath, int targetWidth)
    {
        BitmapSource frame;
        using (var fs = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete))
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = fs;
            bitmap.DecodePixelWidth = targetWidth;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.EndInit();
            bitmap.Freeze();
            frame = bitmap;
        }

        var tempPath = thumbPath + ".tmp";
        var encoder = new JpegBitmapEncoder { QualityLevel = 80 };
        encoder.Frames.Add(BitmapFrame.Create(frame));
        Directory.CreateDirectory(Path.GetDirectoryName(thumbPath)!);
        using (var stream = File.Create(tempPath))
        {
            encoder.Save(stream);
        }

        File.Move(tempPath, thumbPath, overwrite: true);
        return new DecodeResult(frame.PixelWidth, frame.PixelHeight);
    }
}
