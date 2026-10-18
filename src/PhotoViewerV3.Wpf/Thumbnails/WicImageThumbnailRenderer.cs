using System;
using System.IO;
using System.Windows.Media.Imaging;
using PhotoViewerV3.Services.Thumbnails;

namespace PhotoViewerV3.Thumbnails;

/// <summary>
/// WIC-basierte Implementierung von <see cref="IImageThumbnailRenderer"/> für JPG/PNG.
/// Production-Pattern aus Spike #4: expliziter <see cref="FileStream"/> mit
/// <c>FileShare.ReadWrite | FileShare.Delete</c> + <c>BitmapCacheOption.OnLoad</c>
/// (Cloud-Sync-tolerant, Lock direkt nach Read frei), <c>Freeze()</c> für Worker-Threads,
/// atomarer <c>.tmp</c>+Move. Liegt im Wpf-Projekt, weil System.Windows.Media.Imaging
/// WPF-Framework-Referenzen braucht; angesprochen ausschließlich über das Service-Interface.
/// </summary>
public sealed class WicImageThumbnailRenderer : IImageThumbnailRenderer
{
    private const int JpegQuality = 80;

    public ThumbnailRenderResult Render(string sourcePath, string targetThumbPath, int targetWidth)
    {
        if (!File.Exists(sourcePath))
        {
            return ThumbnailRenderResult.Fail("source_missing", $"Quelldatei fehlt: {sourcePath}");
        }

        try
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
                bitmap.DecodePixelWidth = targetWidth; // Höhe skaliert WPF proportional
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bitmap.EndInit();
                bitmap.Freeze();
                frame = bitmap;
            }

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
            return ThumbnailRenderResult.Fail("wic_decode_failed", ex.Message);
        }
    }
}
