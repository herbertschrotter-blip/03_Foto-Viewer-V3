using Lumora.Services.Quality;
using Lumora.Tests.Duplicates;

namespace Lumora.Tests.Quality;

/// <summary>Schärfe und Belichtung aus Pixeln (ADR-0057) an synthetischen Bildern.</summary>
public sealed class ImageQualityTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Scharf_vor_weichgezeichnet_und_staerker_weich_noch_unschaerfer(int seed)
    {
        var sharp = QualityImages.Detailed(seed);
        var soft = QualityImages.Blur(sharp, 1);
        var blurry = QualityImages.Blur(sharp, 3);
        var veryBlurry = QualityImages.Blur(sharp, 6);

        var values = new[] { sharp, soft, blurry, veryBlurry }.Select(i => QualityImages.Rate(i).Sharpness).ToList();

        values.Should().BeInDescendingOrder();
        values[0].Should().BeGreaterThan(values[3] + 0.3, "ein scharfes Muster liegt weit vor einem stark weichgezeichneten");
        values.Should().OnlyContain(v => v >= 0 && v <= 1);
    }

    [Fact]
    public void Portraet_mit_unscharfem_Hintergrund_zaehlt_nach_dem_scharfen_Bereich()
    {
        var sharp = QualityImages.Detailed(7);
        var portrait = QualityImages.BlurOutside(sharp, 5, 0.3, 0.25, 0.7, 0.8);
        var allBlurred = QualityImages.Blur(sharp, 5);

        QualityImages.Rate(portrait).Sharpness.Should().BeGreaterThan(QualityImages.Rate(allBlurred).Sharpness + 0.2);
    }

    [Fact]
    public void Belichtung_normal_vor_zu_dunkel_und_zu_hell()
    {
        var normal = SyntheticImages.Scene(11);
        var dark = QualityImages.Scale(normal, 0.15);
        var veryDark = QualityImages.Scale(normal, 0.05);
        var bright = QualityImages.Scale(normal, 2.5);

        var n = QualityImages.Rate(normal).Exposure;
        var d = QualityImages.Rate(dark).Exposure;
        var vd = QualityImages.Rate(veryDark).Exposure;
        var b = QualityImages.Rate(bright).Exposure;

        n.Should().BeGreaterThan(d).And.BeGreaterThan(b);
        d.Should().BeGreaterThan(vd);
        vd.Should().BeLessThan(0.15, "fast schwarz");
        n.Should().BeGreaterThan(0.5);
    }

    [Fact]
    public void Ausgefressene_Flaechen_kosten_Belichtung()
    {
        var normal = SyntheticImages.Scene(5);
        var pixels = (byte[])normal.Pixels.Clone();
        Array.Fill(pixels, (byte)255, 0, pixels.Length / 3); // oberes Drittel weiß
        var clipped = normal with { Pixels = pixels };

        var rated = QualityImages.Rate(clipped);
        rated.Clipped.Should().BeGreaterThan(0.3);
        rated.Exposure.Should().BeLessThan(QualityImages.Rate(normal).Exposure);
    }

    [Fact]
    public void Einfarbig_ist_unscharf_und_winzige_Bilder_werfen_nicht()
    {
        QualityImages.Rate(SyntheticImages.Flat(128)).Sharpness.Should().Be(0);
        QualityImages.Rate(SyntheticImages.Flat(0)).Exposure.Should().Be(0);
        ImageQuality.Analyze(new byte[2 * 2 * 3], 2, 2).Sharpness.Should().Be(0);
        var act = () => ImageQuality.Analyze(new byte[5], 2, 2);
        act.Should().Throw<ArgumentException>();
    }
}
