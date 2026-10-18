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
