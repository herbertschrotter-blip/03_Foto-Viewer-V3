using System.IO;
using PhotoViewerV3.Services.Thumbnails;
using PhotoViewerV3.Thumbnails;
using PhotoViewerV3.Wpf.Tests.Fixtures;

namespace PhotoViewerV3.Wpf.Tests.Thumbnails;

/// <summary>
/// WIC-Renderer: EXIF-Orientierung, Alpha auf dunklem Hintergrund, Zielgröße auf der KURZEN Seite (lange höchstens
/// das Dreifache, ADR-0043), kein Hochskalieren, Maße des Originals nach Drehung, stabile Fehlercodes. Läuft wie in Produktion auf Worker-Threads (kein STA nötig).
/// </summary>
public sealed class WicImageThumbnailRendererTests : IDisposable
{
    private readonly string _dir = TestImages.CreateTempDirectory();
    private readonly WicImageThumbnailRenderer _renderer = new();

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Temp — best effort
        }
    }

    private string Source(string name) => Path.Combine(_dir, name);

    private string Thumb(string name) => Path.Combine(_dir, "thumbs", name);

    [Fact]
    public void Orientation6_RotatesClockwise_LeftHalfBecomesTop()
    {
        // 400×200 Querformat, links rot / rechts blau, EXIF 6 → kurze Kante 50 → Hochkant 50 × 100, rote Hälfte oben
        var source = Source("o6.jpg");
        TestImages.SaveJpeg(source, TestImages.LeftRedRightBlue(400, 200), exifOrientation: 6);

        var result = _renderer.Render(source, Thumb("o6.jpg"), 50);

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.Width.Should().Be(50);
        result.Height.Should().Be(100);
        var image = TestImages.Load(Thumb("o6.jpg"));
        image.Width.Should().Be(50);
        image.Height.Should().Be(100);
        TestImages.IsReddish(image.PixelAt(25, 15)).Should().BeTrue("die linke Hälfte landet nach 90° im Uhrzeigersinn oben");
        TestImages.IsBluish(image.PixelAt(25, 85)).Should().BeTrue("die rechte Hälfte landet unten");
        (result.SourceWidth, result.SourceHeight).Should().Be((200, 400), "Files.Width/Height = Original so, wie es angezeigt wird");
    }

    [Fact]
    public void ReadDimensions_reads_header_with_orientation()
    {
        var rotated = Source("dims6.jpg");
        TestImages.SaveJpeg(rotated, TestImages.LeftRedRightBlue(400, 200), exifOrientation: 6);
        var plain = Source("dims1.png");
        TestImages.SavePng(plain, TestImages.LeftRedRightBlue(40, 30));
        var garbage = Source("dims-garbage.jpg");
        File.WriteAllBytes(garbage, Enumerable.Range(0, 512).Select(i => (byte)(i * 7 % 251)).ToArray());

        _renderer.ReadDimensions(rotated).Should().Be((200, 400));
        _renderer.ReadDimensions(plain).Should().Be((40, 30));
        _renderer.ReadDimensions(garbage).Should().BeNull();
        _renderer.ReadDimensions(Source("fehlt.jpg")).Should().BeNull();
    }

    [Fact]
    public void Orientation8_RotatesCounterClockwise_LeftHalfBecomesBottom()
    {
        var source = Source("o8.jpg");
        TestImages.SaveJpeg(source, TestImages.LeftRedRightBlue(400, 200), exifOrientation: 8);

        var result = _renderer.Render(source, Thumb("o8.jpg"), 50);

        result.Success.Should().BeTrue(result.ErrorMessage);
        var image = TestImages.Load(Thumb("o8.jpg"));
        (image.Width, image.Height).Should().Be((50, 100));
        TestImages.IsBluish(image.PixelAt(25, 15)).Should().BeTrue();
        TestImages.IsReddish(image.PixelAt(25, 85)).Should().BeTrue();
    }

    [Fact]
    public void Orientation3_Rotates180()
    {
        var source = Source("o3.jpg");
        TestImages.SaveJpeg(source, TestImages.LeftRedRightBlue(400, 200), exifOrientation: 3);

        _renderer.Render(source, Thumb("o3.jpg"), 50).Success.Should().BeTrue();

        var image = TestImages.Load(Thumb("o3.jpg"));
        (image.Width, image.Height).Should().Be((100, 50));
        TestImages.IsBluish(image.PixelAt(15, 25)).Should().BeTrue();
        TestImages.IsReddish(image.PixelAt(85, 25)).Should().BeTrue();
    }

    [Fact]
    public void Orientation2_MirrorsHorizontally()
    {
        var source = Source("o2.jpg");
        TestImages.SaveJpeg(source, TestImages.LeftRedRightBlue(400, 200), exifOrientation: 2);

        _renderer.Render(source, Thumb("o2.jpg"), 50).Success.Should().BeTrue();

        var image = TestImages.Load(Thumb("o2.jpg"));
        (image.Width, image.Height).Should().Be((100, 50));
        TestImages.IsBluish(image.PixelAt(15, 25)).Should().BeTrue();
        TestImages.IsReddish(image.PixelAt(85, 25)).Should().BeTrue();
    }

    [Fact]
    public void Orientation1_KeepsImage()
    {
        var source = Source("o1.jpg");
        TestImages.SaveJpeg(source, TestImages.LeftRedRightBlue(400, 200), exifOrientation: 1);

        _renderer.Render(source, Thumb("o1.jpg"), 50).Success.Should().BeTrue();

        var image = TestImages.Load(Thumb("o1.jpg"));
        (image.Width, image.Height).Should().Be((100, 50));
        TestImages.IsReddish(image.PixelAt(15, 25)).Should().BeTrue();
        TestImages.IsBluish(image.PixelAt(85, 25)).Should().BeTrue();
    }

    [Fact]
    public void TransparentPng_IsFlattenedOnDarkBackground()
    {
        // Außen transparent-weiß (ohne Compositing würde JPEG Weiß oder Schwarz liefern), Mitte deckend weiß.
        var source = Source("alpha.png");
        TestImages.SavePng(source, TestImages.Create(64, 64, (x, y) =>
            x is >= 16 and < 48 && y is >= 16 and < 48 ? TestImages.White : TestImages.TransparentWhite));

        var result = _renderer.Render(source, Thumb("alpha.jpg"), 64);

        result.Success.Should().BeTrue(result.ErrorMessage);
        var image = TestImages.Load(Thumb("alpha.jpg"));
        var bg = WicImageThumbnailRenderer.AlphaBackground;
        TestImages.IsNear(image.PixelAt(3, 3), bg.B, bg.G, bg.R).Should().BeTrue($"Ecke muss Hintergrund sein, ist {image.PixelAt(3, 3)}");
        TestImages.IsNear(image.PixelAt(32, 32), 255, 255, 255).Should().BeTrue($"Mitte muss weiß bleiben, ist {image.PixelAt(32, 32)}");
    }

    [Fact]
    public void SmallImage_IsNotUpscaled()
    {
        var source = Source("small.png");
        TestImages.SavePng(source, TestImages.LeftRedRightBlue(40, 30));

        var result = _renderer.Render(source, Thumb("small.jpg"), 320);

        result.Success.Should().BeTrue(result.ErrorMessage);
        (result.Width, result.Height).Should().Be((40, 30));
        (result.SourceWidth, result.SourceHeight).Should().Be((40, 30));
    }

    [Theory]
    [InlineData(800, 400, 640, 320)]
    [InlineData(400, 800, 320, 640)]
    [InlineData(640, 640, 320, 320)]
    [InlineData(900, 600, 480, 320)]
    [InlineData(3000, 500, 960, 160)] // Panorama: lange Kante höchstens 3 × 320
    [InlineData(300, 200, 300, 200)] // kleiner als das Ziel: unverändert
    public void ShorterSide_MatchesTarget_LongerSideCapped(int width, int height, int expectedWidth, int expectedHeight)
    {
        var source = Source($"size-{width}x{height}.jpg");
        TestImages.SaveJpeg(source, TestImages.LeftRedRightBlue(width, height));

        var result = _renderer.Render(source, Thumb($"size-{width}x{height}.jpg"), 320);

        result.Success.Should().BeTrue(result.ErrorMessage);
        (result.Width, result.Height).Should().Be((expectedWidth, expectedHeight));
        (result.SourceWidth, result.SourceHeight).Should().Be((width, height));
        result.ByteSize.Should().BeGreaterThan(0);
        File.Exists(Thumb($"size-{width}x{height}.jpg") + ".tmp").Should().BeFalse("atomarer Move lässt keine .tmp zurück");
    }

    [Fact]
    public void MissingSource_ReportsSourceMissing()
    {
        var result = _renderer.Render(Source("gibt-es-nicht.jpg"), Thumb("x.jpg"), 320);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(ThumbnailErrorCodes.SourceMissing);
    }

    [Fact]
    public void Garbage_ReportsWicDecodeFailed_ForFfmpegFallback()
    {
        var source = Source("garbage.jpg");
        File.WriteAllBytes(source, Enumerable.Range(0, 4096).Select(i => (byte)(i * 31 % 251)).ToArray());

        var result = _renderer.Render(source, Thumb("garbage.jpg"), 320);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(ThumbnailErrorCodes.WicDecodeFailed);
        File.Exists(Thumb("garbage.jpg")).Should().BeFalse();
    }

    [Fact]
    public void Result_IsFrozenAndOpaque()
    {
        var source = Source("frozen.png");
        TestImages.SavePng(source, TestImages.Create(20, 10, (_, _) => TestImages.TransparentWhite));

        var decoded = WicImageThumbnailRenderer.DecodeThumbnail(source, 320);

        decoded.Image.IsFrozen.Should().BeTrue();
        WicImageThumbnailRenderer.IsOpaque(decoded.Image.Format).Should().BeTrue();
        (decoded.SourceWidth, decoded.SourceHeight).Should().Be((20, 10));
    }
}
