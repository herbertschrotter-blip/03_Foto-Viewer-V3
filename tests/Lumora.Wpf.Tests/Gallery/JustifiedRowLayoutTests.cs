using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lumora.Gallery.Controls;
using Lumora.Gallery.Data;
using Lumora.Gallery.ViewModels;
using Xunit.Abstractions;

namespace Lumora.Wpf.Tests.Gallery;

/// <summary>
/// Reihen-Umbruch der Collage (ADR-0044): gefüllte Reihen enden exakt am rechten Rand, Bildhöhe nie über der Zielhöhe,
/// letzte Reihe bei Zielhöhe linksbündig, Panoramen allein auf voller Breite, extreme Seitenverhältnisse begrenzt;
/// 1000 Items in deutlich unter 2 ms.
/// </summary>
public sealed class JustifiedRowLayoutTests
{
    private const double Width = 1000;
    private const double Target = 180;
    private const double Spacing = 6;

    private readonly ITestOutputHelper _output;

    public JustifiedRowLayoutTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Gefuellte_Reihen_sind_randvoll_und_hoechstens_zielhoch()
    {
        var layout = Compute(MixedAspects(500));

        layout.RowCount.Should().BeGreaterThan(50);
        for (var row = 0; row < layout.RowCount - 1; row++)
        {
            var first = layout.ItemRect(layout.RowFirst(row));
            var last = layout.ItemRect(layout.RowLast(row));
            first.Left.Should().Be(0, $"Reihe {row} beginnt am linken Rand");
            last.Right.Should().Be(Width, $"Reihe {row} endet exakt am rechten Rand");
            layout.RowHeight(row).Should().BeLessThanOrEqualTo(Target + Spacing, $"Reihe {row} wird nie über die Zielhöhe gestreckt");
            layout.RowHeight(row).Should().BeGreaterThan(Target * 0.5);
        }
    }

    [Fact]
    public void Zellen_luekenlos_ohne_Ueberlappung_in_Seitenreihenfolge()
    {
        var layout = Compute(MixedAspects(300));
        var y = 0.0;
        for (var row = 0; row < layout.RowCount; row++)
        {
            layout.RowTop(row).Should().Be(y);
            var x = 0.0;
            for (var i = layout.RowFirst(row); i <= layout.RowLast(row); i++)
            {
                layout.RowOf(i).Should().Be(row);
                var cell = layout.ItemRect(i);
                cell.Left.Should().Be(x, $"Item {i} schließt an seinen Vorgänger an");
                cell.Top.Should().Be(y);
                cell.Height.Should().Be(layout.RowHeight(row));
                x = cell.Right;
            }

            y += layout.RowHeight(row);
        }

        layout.ExtentHeight.Should().Be(y);
        layout.RowLast(layout.RowCount - 1).Should().Be(299);
    }

    [Fact]
    public void Bild_behaelt_in_der_Zelle_sein_Seitenverhaeltnis()
    {
        var aspects = MixedAspects(200);
        var layout = Compute(aspects);
        for (var i = 0; i < aspects.Length; i++)
        {
            var cell = layout.ItemRect(i);
            var imageAspect = (cell.Width - Spacing) / (cell.Height - Spacing);
            imageAspect.Should().BeApproximately(JustifiedRowLayout.ClampAspect(aspects[i]), 0.03, $"Item {i}: höchstens 1 px Rundung");
        }
    }

    [Fact]
    public void Letzte_Reihe_wird_nicht_gestreckt()
    {
        // 7 Querformate 4:3 bei Zielhöhe 180: Reihe 1 nimmt 5 (Überlauf beim 5.), Rest 2 → letzte Reihe
        var layout = Compute(Enumerable.Repeat(4.0 / 3.0, 7).ToArray());

        layout.RowCount.Should().Be(2);
        var lastRow = layout.RowCount - 1;
        layout.RowHeight(lastRow).Should().Be(Target + Spacing, "letzte Reihe bleibt bei Zielhöhe");
        layout.ItemRect(layout.RowLast(lastRow)).Right.Should().BeLessThan(Width, "linksbündig, nicht auf volle Breite gezogen");
        layout.ItemRect(layout.RowFirst(lastRow)).Width.Should().Be(Math.Round(4.0 / 3.0 * Target + Spacing));
    }

    [Fact]
    public void Panorama_bekommt_allein_die_volle_Breite_mit_kleinerer_Hoehe()
    {
        var layout = Compute([1.5, 5.8, 1.5, 0.75]);

        // 1,5 + 5,8 laufen über → Reihe 0 enthält beide (verkleinert); Panorama allein ist zu breit für Zielhöhe:
        var alone = Compute([5.8]);
        alone.RowCount.Should().Be(1);
        alone.ItemRect(0).Width.Should().Be(Width);
        alone.RowHeight(0).Should().BeLessThan(Target + Spacing);
        ((alone.ItemRect(0).Width - Spacing) / (alone.RowHeight(0) - Spacing)).Should().BeApproximately(5.8, 0.05);

        layout.ItemRect(layout.RowLast(0)).Right.Should().Be(Width);
        layout.RowHeight(0).Should().BeLessThan(Target + Spacing);
    }

    [Theory]
    [InlineData(0.1, JustifiedRowLayout.MinAspectRatio)] // sehr hohes Bild → nicht schmaler als 0,4
    [InlineData(0.46, 0.46)] // Handy-Bildschirmfoto bleibt unbeschnitten
    [InlineData(12.0, JustifiedRowLayout.MaxAspectRatio)] // 360°-Panorama → höchstens 6:1
    [InlineData(0.0, JustifiedRowLayout.DefaultAspectRatio)] // unbekannt → 4:3
    [InlineData(double.NaN, JustifiedRowLayout.DefaultAspectRatio)]
    public void Extreme_Seitenverhaeltnisse_werden_begrenzt(double aspect, double expected)
    {
        JustifiedRowLayout.ClampAspect(aspect).Should().BeApproximately(expected, 1e-9);
        var layout = Compute([aspect, aspect, aspect]);
        layout.AspectOf(0).Should().BeApproximately(expected, 1e-9);
    }

    [Fact]
    public void Sehr_hohe_Bilder_werden_keine_Streifen()
    {
        var layout = Compute(Enumerable.Repeat(0.05, 40).ToArray());
        for (var i = 0; i < 40; i++)
        {
            var cell = layout.ItemRect(i);
            (cell.Width - Spacing).Should().BeGreaterThanOrEqualTo(Math.Floor(JustifiedRowLayout.MinAspectRatio * (cell.Height - Spacing)) - 1);
        }
    }

    [Fact]
    public void Leere_Seite_und_ein_Item()
    {
        var empty = Compute([]);
        empty.RowCount.Should().Be(0);
        empty.ExtentHeight.Should().Be(0);
        empty.RowAt(10).Should().Be(-1);

        var single = Compute([1.0]);
        single.RowCount.Should().Be(1);
        single.ItemRect(0).Should().Be(new System.Windows.Rect(0, 0, Target + Spacing, Target + Spacing));
    }

    [Fact]
    public void RowAt_findet_die_Reihe_zur_Hoehe()
    {
        var layout = Compute(MixedAspects(400));
        for (var row = 0; row < layout.RowCount; row++)
        {
            layout.RowAt(layout.RowTop(row)).Should().Be(row);
            layout.RowAt(layout.RowTop(row) + (layout.RowHeight(row) / 2)).Should().Be(row);
        }

        layout.RowAt(-50).Should().Be(0);
        layout.RowAt(layout.ExtentHeight + 50).Should().Be(layout.RowCount - 1);
    }

    [Fact]
    public void Messung_1000_Items_Umbruch_unter_2_ms()
    {
        var aspects = MixedAspects(1000);
        var layout = new JustifiedRowLayout();
        layout.Compute(aspects, Width, Target, Spacing); // Aufwärmen (JIT)

        var runs = new List<double>();
        for (var run = 0; run < 50; run++)
        {
            var sw = Stopwatch.StartNew();
            layout.Compute(aspects, Width, Target, Spacing);
            runs.Add(sw.Elapsed.TotalMilliseconds);
        }

        runs.Sort();
        var median = runs[runs.Count / 2];
        _output.WriteLine($"Collage-Umbruch 1000 Items ({layout.RowCount} Reihen, Breite {Width}): Median {median:N4} ms, max {runs[^1]:N4} ms");
        median.Should().BeLessThan(2.0);
    }

    [Theory]
    [InlineData(4000, 3000, 4.0 / 3.0)]
    [InlineData(600, 1200, 0.5)] // Hochformat nach EXIF-Drehung (Files.Width/Height)
    public void Item_Seitenverhaeltnis_aus_den_Bildmassen(int width, int height, double expected)
        => CreateItem(width, height).LayoutAspectRatio.Should().BeApproximately(expected, 1e-9);

    [Fact]
    public void Item_ohne_Masse_nimmt_das_Vorschaubild_und_meldet_die_Aenderung()
    {
        var item = CreateItem(null, null);
        item.LayoutAspectRatio.Should().BeApproximately(JustifiedRowLayout.DefaultAspectRatio, 1e-9, "ohne Maße und Bild 4:3");
        var changed = new List<string?>();
        item.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        item.Thumbnail = Bitmap(300, 400);

        item.LayoutAspectRatio.Should().BeApproximately(0.75, 1e-9);
        changed.Should().Contain(nameof(GalleryItem.LayoutAspectRatio));

        // Maße beim Neuladen nachgetragen: haben Vorrang vor dem Vorschaubild
        item.UpdateMediaSize(Dto(900, 1600));
        item.LayoutAspectRatio.Should().BeApproximately(900.0 / 1600, 1e-9);
        item.MediaWidth.Should().Be(900);
    }

    [Fact]
    public void Archiv_und_unlesbare_Masse_sind_4_zu_3()
    {
        new GalleryItem(Dto(4000, 1000) with { MediaType = 2, Extension = ".zip", Name = "a.zip" }).LayoutAspectRatio
            .Should().BeApproximately(JustifiedRowLayout.DefaultAspectRatio, 1e-9);
        CreateItem(0, 0).LayoutAspectRatio.Should().BeApproximately(JustifiedRowLayout.DefaultAspectRatio, 1e-9, "0 × 0 = nicht lesbar (HEIC ohne Codec)");
    }

    // ---- Hilfen --------------------------------------------------------------------------------

    private static JustifiedRowLayout Compute(double[] aspects)
    {
        var layout = new JustifiedRowLayout();
        layout.Compute(aspects, Width, Target, Spacing);
        return layout;
    }

    /// <summary>Gemischte Seitenverhältnisse wie in einer Handy-Bibliothek (Quer, Hoch, Quadrat, Panorama, Bildschirmfoto).</summary>
    internal static double[] MixedAspects(int count)
    {
        double[] pattern = [1.5, 0.75, 4.0 / 3.0, 1.0, 0.67, 16.0 / 9.0, 1.5, 2.6, 0.75, 4.0 / 3.0, 0.46, 1.5, 0.67, 1.0, 3.5];
        return Enumerable.Range(0, count).Select(i => pattern[i % pattern.Length]).ToArray();
    }

    private static GalleryFileDto Dto(int? width, int? height)
        => new(1, 1, "IMG_0001.jpg", ".jpg", 1000, 1_700_000_000, 0, false, null, false, @"C:\Testbibliothek") { Width = width, Height = height };

    private static GalleryItem CreateItem(int? width, int? height) => new(Dto(width, height));

    private static BitmapSource Bitmap(int width, int height)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, new byte[width * height], width);
        bitmap.Freeze();
        return bitmap;
    }
}
