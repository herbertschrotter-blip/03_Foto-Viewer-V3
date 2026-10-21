using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Lumora.Wpf.Tests.Fixtures;

/// <summary>Erzeugt synthetische Testbilder (JPEG mit EXIF-Orientierung, PNG mit Alpha) und liest Pixel zurück.</summary>
internal static class TestImages
{
    public static readonly (byte B, byte G, byte R, byte A) Red = (0, 0, 255, 255);
    public static readonly (byte B, byte G, byte R, byte A) Blue = (255, 0, 0, 255);
    public static readonly (byte B, byte G, byte R, byte A) White = (255, 255, 255, 255);
    public static readonly (byte B, byte G, byte R, byte A) TransparentWhite = (255, 255, 255, 0);

    public static string CreateTempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "Lumora.Wpf.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static BitmapSource Create(int width, int height, Func<int, int, (byte B, byte G, byte R, byte A)> pixelAt)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (b, g, r, a) = pixelAt(x, y);
                var i = y * stride + x * 4;
                pixels[i] = b;
                pixels[i + 1] = g;
                pixels[i + 2] = r;
                pixels[i + 3] = a;
            }
        }

        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        source.Freeze();
        return source;
    }

    /// <summary>Linke Hälfte rot, rechte Hälfte blau — macht Rotation/Spiegelung im Ergebnis sichtbar.</summary>
    public static BitmapSource LeftRedRightBlue(int width, int height)
        => Create(width, height, (x, _) => x < width / 2 ? Red : Blue);

    public static void SaveJpeg(string path, BitmapSource source, ushort? exifOrientation = null)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = 95 };
        BitmapMetadata? metadata = null;
        if (exifOrientation is { } orientation)
        {
            metadata = new BitmapMetadata("jpg");
            metadata.SetQuery("/app1/ifd/{ushort=274}", orientation);
        }

        encoder.Frames.Add(BitmapFrame.Create(source, null, metadata, null));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    public static void SavePng(string path, BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    public sealed record LoadedImage(int Width, int Height, byte[] Pixels)
    {
        public (byte B, byte G, byte R) PixelAt(int x, int y)
        {
            var i = y * Width * 4 + x * 4;
            return (Pixels[i], Pixels[i + 1], Pixels[i + 2]);
        }
    }

    public static LoadedImage Load(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        var frame = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgr32, null, 0);
        var stride = frame.PixelWidth * 4;
        var pixels = new byte[stride * frame.PixelHeight];
        frame.CopyPixels(pixels, stride, 0);
        return new LoadedImage(frame.PixelWidth, frame.PixelHeight, pixels);
    }

    public static bool IsReddish((byte B, byte G, byte R) p) => p.R > 180 && p.B < 80 && p.G < 80;

    public static bool IsBluish((byte B, byte G, byte R) p) => p.B > 180 && p.R < 80 && p.G < 80;

    public static bool IsNear((byte B, byte G, byte R) p, byte b, byte g, byte r, int tolerance = 12)
        => Math.Abs(p.B - b) <= tolerance && Math.Abs(p.G - g) <= tolerance && Math.Abs(p.R - r) <= tolerance;
}
