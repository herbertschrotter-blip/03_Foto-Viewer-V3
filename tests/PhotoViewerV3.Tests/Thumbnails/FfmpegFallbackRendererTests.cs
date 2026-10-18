using Microsoft.Extensions.Logging.Abstractions;
using PhotoViewerV3.Services.Thumbnails;
using PhotoViewerV3.Tests.Fixtures;

namespace PhotoViewerV3.Tests.Thumbnails;

/// <summary>
/// Integrationstests gegen eine echte ffmpeg.exe. Ohne FFmpeg auf der Maschine prüfen sie nur,
/// dass der Wrapper sauber <c>ffmpeg_not_found</c> meldet.
/// </summary>
public sealed class FfmpegFallbackRendererTests
{
    private static FfmpegVideoThumbnailer CreateRenderer() => new(NullLogger<FfmpegVideoThumbnailer>.Instance);

    [Fact]
    public async Task RenderImageAsync_erzeugt_JPEG_aus_BMP_ohne_tmp_Reste()
    {
        var renderer = CreateRenderer();
        using var fx = await TempLibraryFixture.CreateAsync();
        var source = Path.Combine(fx.LibraryRoot, "pixel.bmp");
        WriteBmp(source, 8, 6);
        var target = Path.Combine(fx.Paths.GetLibraryThumbnailsPath(1), "1_320_still.jpg");

        IFallbackImageThumbnailRenderer fallback = renderer;
        var result = await fallback.RenderAsync(source, target, 64, CancellationToken.None);

        if (!renderer.IsAvailable)
        {
            result.Success.Should().BeFalse();
            result.ErrorCode.Should().Be(ThumbnailErrorCodes.FfmpegNotFound);
            return;
        }

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.ByteSize.Should().BeGreaterThan(0);
        File.Exists(target).Should().BeTrue();
        File.Exists(target + ".tmp").Should().BeFalse();
        var header = File.ReadAllBytes(target).Take(2).ToArray();
        header.Should().Equal(0xFF, 0xD8);
    }

    [Fact]
    public async Task RenderImageAsync_meldet_fehlende_Quelldatei()
    {
        var renderer = CreateRenderer();
        using var fx = await TempLibraryFixture.CreateAsync();
        var target = Path.Combine(fx.Paths.GetLibraryThumbnailsPath(1), "2_320_still.jpg");

        var result = await renderer.RenderImageAsync(Path.Combine(fx.LibraryRoot, "fehlt.webp"), target, 64, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(renderer.IsAvailable ? ThumbnailErrorCodes.SourceMissing : ThumbnailErrorCodes.FfmpegNotFound);
        File.Exists(target).Should().BeFalse();
    }

    [Fact]
    public async Task RenderImageAsync_verarbeitet_Pfade_mit_Leerzeichen_Umlauten_und_Sonderzeichen()
    {
        var renderer = CreateRenderer();
        using var fx = await TempLibraryFixture.CreateAsync();
        var source = Path.Combine(fx.LibraryRoot, "Ärger & Co (1) 'test' ;name.bmp");
        WriteBmp(source, 8, 6);
        var target = Path.Combine(fx.Paths.GetLibraryThumbnailsPath(1), "thumbs ä", "3_320_still.jpg");

        var result = await renderer.RenderImageAsync(source, target, 64, CancellationToken.None);

        if (!renderer.IsAvailable)
        {
            result.ErrorCode.Should().Be(ThumbnailErrorCodes.FfmpegNotFound);
            return;
        }

        result.Success.Should().BeTrue(result.ErrorMessage);
        File.Exists(target).Should().BeTrue();
        File.Exists(target + ".tmp").Should().BeFalse();
    }

    [Fact]
    public async Task RenderAsync_liefert_ein_Frame_fuer_Clips_kuerzer_als_der_Seek_von_1s()
    {
        var renderer = CreateRenderer();
        if (!renderer.IsAvailable)
        {
            return; // ohne FFmpeg lässt sich kein Testclip erzeugen
        }

        using var fx = await TempLibraryFixture.CreateAsync();
        var clip = Path.Combine(fx.LibraryRoot, "kurz.mp4");
        if (!TryCreateShortClip(clip, 0.4))
        {
            return; // Generator nicht verfügbar (kein lavfi/mpeg4) → kein Urteil möglich
        }

        var target = Path.Combine(fx.Paths.GetLibraryThumbnailsPath(1), "4_320_still.jpg");

        var result = await renderer.RenderAsync(clip, target, 64, CancellationToken.None);

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.ByteSize.Should().BeGreaterThan(0);
        File.Exists(target).Should().BeTrue();
        File.Exists(target + ".tmp").Should().BeFalse();
    }

    [Fact]
    public async Task RenderAsync_meldet_Abbruch_mit_ffmpeg_cancelled_und_laesst_keine_tmp_zurueck()
    {
        var renderer = CreateRenderer();
        if (!renderer.IsAvailable)
        {
            return;
        }

        using var fx = await TempLibraryFixture.CreateAsync();
        var source = Path.Combine(fx.LibraryRoot, "pixel.bmp");
        WriteBmp(source, 8, 6);
        var target = Path.Combine(fx.Paths.GetLibraryThumbnailsPath(1), "5_320_still.jpg");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await renderer.RenderImageAsync(source, target, 64, cts.Token);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(ThumbnailErrorCodes.FfmpegCancelled);
        File.Exists(target).Should().BeFalse();
        File.Exists(target + ".tmp").Should().BeFalse();
    }

    /// <summary>Erzeugt mit FFmpeg (PATH) einen kurzen Testclip aus dem lavfi-Testbild; false, wenn das nicht klappt.</summary>
    private static bool TryCreateShortClip(string path, double seconds)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("ffmpeg")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            foreach (var arg in new[]
                     {
                         "-hide_banner", "-loglevel", "error", "-y",
                         "-f", "lavfi", "-i", $"testsrc=duration={seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}:size=64x48:rate=10",
                         "-c:v", "mpeg4", "-q:v", "5", path,
                     })
            {
                psi.ArgumentList.Add(arg);
            }

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null)
            {
                return false;
            }

            proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();
            proc.WaitForExit(30_000);
            return proc.ExitCode == 0 && File.Exists(path) && new FileInfo(path).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Minimales 24-Bit-BMP (ohne Bibliotheken), damit FFmpeg etwas Echtes decodiert.</summary>
    private static void WriteBmp(string path, int width, int height)
    {
        var rowBytes = ((width * 3 + 3) / 4) * 4;
        var pixelBytes = rowBytes * height;
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write((byte)'B');
        writer.Write((byte)'M');
        writer.Write(54 + pixelBytes);
        writer.Write(0);
        writer.Write(54);
        writer.Write(40);
        writer.Write(width);
        writer.Write(height);
        writer.Write((short)1);
        writer.Write((short)24);
        writer.Write(0);
        writer.Write(pixelBytes);
        writer.Write(2835);
        writer.Write(2835);
        writer.Write(0);
        writer.Write(0);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                writer.Write((byte)(x * 30));
                writer.Write((byte)(y * 40));
                writer.Write((byte)200);
            }

            for (var pad = width * 3; pad < rowBytes; pad++)
            {
                writer.Write((byte)0);
            }
        }
    }
}
