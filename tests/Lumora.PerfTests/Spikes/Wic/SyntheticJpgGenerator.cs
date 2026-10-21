using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;

namespace Lumora.PerfTests.Spikes.Wic;

internal sealed record SynthJpgFile(string Path, int Width, int Height, long SizeBytes);

internal sealed record SynthGenReport(
    int Count,
    long TotalBytes,
    TimeSpan Duration,
    IReadOnlyList<SynthJpgFile> Files)
{
    public double MBytesPerSec => Duration.TotalSeconds <= 0
        ? 0
        : (TotalBytes / 1024.0 / 1024.0) / Duration.TotalSeconds;
}

/// <summary>
/// Erzeugt N synthetische JPGs in gemischten Auflösungen mittels WIC JpegBitmapEncoder.
/// Pixel-Daten: Gradient + Noise (realistische JPEG-Kompression statt random-noise-bloat).
/// </summary>
internal static class SyntheticJpgGenerator
{
    private static readonly (int Width, int Height)[] Resolutions =
    [
        (800, 600),     // 0,48 MP
        (1920, 1080),   // 2,1 MP
        (3840, 2160),   // 8,3 MP
        (4032, 3024),   // 12 MP (typisches Smartphone)
    ];

    private const int JpegQuality = 85;

    public static SynthGenReport Generate(int count, string outputDir, int seed)
    {
        if (count < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        Directory.CreateDirectory(outputDir);
        var random = new Random(seed);
        var files = new List<SynthJpgFile>(count);
        long totalBytes = 0;

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < count; i++)
        {
            var (w, h) = Resolutions[random.Next(Resolutions.Length)];
            var path = Path.Combine(outputDir, $"synth_{i:D5}_{w}x{h}.jpg");
            EncodeJpeg(path, w, h, random);
            var size = new FileInfo(path).Length;
            files.Add(new SynthJpgFile(path, w, h, size));
            totalBytes += size;
        }

        sw.Stop();
        return new SynthGenReport(count, totalBytes, sw.Elapsed, files);
    }

    private static void EncodeJpeg(string path, int width, int height, Random random)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        FillGradientWithNoise(pixels, width, height, random);

        var bitmap = BitmapSource.Create(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgr32, null, pixels, stride);
        bitmap.Freeze();

        var encoder = new JpegBitmapEncoder { QualityLevel = JpegQuality };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void FillGradientWithNoise(byte[] pixels, int width, int height, Random random)
    {
        var stride = width * 4;
        for (var y = 0; y < height; y++)
        {
            var rowOffset = y * stride;
            for (var x = 0; x < width; x++)
            {
                var idx = rowOffset + x * 4;
                // Mild gradient mit 6-Bit-Noise — JPEG-friendlich, aber nicht zu glatt
                var b = (byte)(((x * 256 / width) + (random.Next() & 0x3F)) & 0xFF);
                var g = (byte)(((y * 256 / height) + (random.Next() & 0x3F)) & 0xFF);
                var r = (byte)((((x + y) * 256 / (width + height)) + (random.Next() & 0x3F)) & 0xFF);
                pixels[idx + 0] = b;
                pixels[idx + 1] = g;
                pixels[idx + 2] = r;
                pixels[idx + 3] = 0xFF;
            }
        }
    }
}
