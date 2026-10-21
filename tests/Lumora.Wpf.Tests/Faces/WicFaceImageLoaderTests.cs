using System.IO;
using Lumora.Faces;
using Lumora.Services.Faces;
using Lumora.Wpf.Tests.Fixtures;

namespace Lumora.Wpf.Tests.Faces;

/// <summary>WIC-Lader der Gesichtserkennung (ADR-0035): RGB-Reihenfolge, längste Kante, EXIF-Orientierung, Fehlerfälle.</summary>
public sealed class WicFaceImageLoaderTests : IDisposable
{
    private readonly string _dir = TestImages.CreateTempDirectory();
    private readonly WicFaceImageLoader _loader = new();

    [Fact]
    public void Liefert_RGB_in_dieser_Reihenfolge_ohne_Vergroesserung()
    {
        var path = Path.Combine(_dir, "rot-blau.png");
        TestImages.SavePng(path, TestImages.LeftRedRightBlue(200, 100));

        var image = _loader.Load(path, 1280);

        image.Width.Should().Be(200);
        image.Height.Should().Be(100);
        image.Pixels.Length.Should().Be(200 * 100 * 3);
        Rgb(image, 10, 50).Should().Be((255, 0, 0), "links rot, Reihenfolge R, G, B");
        Rgb(image, 190, 50).Should().Be((0, 0, 255));
    }

    [Fact]
    public void Begrenzt_die_laengste_Kante()
    {
        var path = Path.Combine(_dir, "gross.jpg");
        TestImages.SaveJpeg(path, TestImages.LeftRedRightBlue(1600, 800));

        var image = _loader.Load(path, 640);

        image.Width.Should().Be(640);
        image.Height.Should().Be(320);
        image.SourceWidth.Should().Be(1600);
        image.SourceHeight.Should().Be(800);
    }

    [Fact]
    public void Wendet_die_EXIF_Orientierung_an_Hochkant_mit_vertauschten_Quellmassen()
    {
        var path = Path.Combine(_dir, "o6.jpg");
        TestImages.SaveJpeg(path, TestImages.LeftRedRightBlue(400, 200), exifOrientation: 6);

        var image = _loader.Load(path, 1280);

        image.Width.Should().Be(200);
        image.Height.Should().Be(400);
        image.SourceWidth.Should().Be(200);
        image.SourceHeight.Should().Be(400);
        var top = Rgb(image, 100, 20);
        var bottom = Rgb(image, 100, 380);
        top.R.Should().BeGreaterThan(180, "Orientierung 6 dreht im Uhrzeigersinn: links (rot) wird oben");
        bottom.B.Should().BeGreaterThan(180);
    }

    [Fact]
    public void Fehlende_Datei_wirft_FileNotFound_kaputte_Datei_FaceImageDecodeException()
    {
        var missing = Path.Combine(_dir, "fehlt.jpg");
        var broken = Path.Combine(_dir, "kaputt.jpg");
        File.WriteAllBytes(broken, [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 5]);

        FluentActions.Invoking(() => _loader.Load(missing, 640)).Should().Throw<FileNotFoundException>();
        FluentActions.Invoking(() => _loader.Load(broken, 640)).Should().Throw<FaceImageDecodeException>();
    }

    private static (byte R, byte G, byte B) Rgb(FaceImage image, int x, int y)
    {
        var i = (y * image.Width + x) * 3;
        return (image.Pixels[i], image.Pixels[i + 1], image.Pixels[i + 2]);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
