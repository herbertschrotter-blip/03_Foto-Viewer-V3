using System.IO;
using System.Windows.Media.Imaging;
using PhotoViewerV3.Gallery.ViewModels;
using PhotoViewerV3.Wpf.Tests.Fixtures;

namespace PhotoViewerV3.Wpf.Tests.Gallery;

/// <summary>
/// ADR-0043: Kacheln laden ihr Vorschaubild passend zu Kachelgröße × Bildschirm-Skalierung — so groß, dass die Fläche
/// bedeckt ist („UniformToFill“ skaliert nie hoch), nie größer als die Datei; neu dekodiert wird nur bei deutlich größerer
/// Fläche. Bisher fest 180 px Breite: ein Querformat blieb 180 × 120 px.
/// </summary>
public sealed class GalleryThumbnailDecodeTests : IDisposable
{
    private readonly string _dir = TestImages.CreateTempDirectory();

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

    [Theory]
    [InlineData(180, 160, 1.0, 164, 112)] // Bildbereich ohne Rand (16) und Namenszeile (32)
    [InlineData(180, 160, 1.5, 246, 168)]
    [InlineData(320, 250, 1.5, 456, 303)]
    [InlineData(320, 250, 1.25, 380, 253)]
    [InlineData(120, 122, 0, 104, 74)] // unbekannte Skalierung → 100 %
    public void Flaeche_ist_Bildbereich_der_Kachel_mal_Skalierung(double width, double height, double dpi, int expectedWidth, int expectedHeight)
        => ThumbnailDecodeBox.FromTile(width, height, dpi).Should().Be(new ThumbnailDecodeBox(expectedWidth, expectedHeight));

    [Theory]
    [InlineData(@"C:\thumbs\1\42_400_still.jpg", false)]
    [InlineData(@"C:\thumbs\1\42_320_still.jpg", true)]
    [InlineData(@"C:\thumbs\1\4000_320_still.jpg", true)]
    public void Alte_Groesse_am_Dateinamen_erkannt(string path, bool legacy)
        => GalleryViewModel.IsLegacyThumbnailPath(path).Should().Be(legacy);

    [Theory]
    [InlineData(533, 400, 270, 240, 320)] // Querformat: Höhe bestimmt (240/400) → 320 breit
    [InlineData(400, 533, 270, 240, 270)] // Hochformat: Breite bestimmt
    [InlineData(1200, 400, 480, 375, 1125)] // Panorama in großer Kachel: Höhe 375 → 1125 breit
    [InlineData(400, 533, 480, 375, 0)] // Vorschaubild kleiner als die Fläche: volle Größe, nie hochskalieren
    [InlineData(0, 0, 180, 160, 0)]
    public void Dekodierbreite_bedeckt_die_Flaeche(int imageWidth, int imageHeight, int boxWidth, int boxHeight, int expected)
        => new ThumbnailDecodeBox(boxWidth, boxHeight).DecodeWidthFor(imageWidth, imageHeight).Should().Be(expected);

    [Fact]
    public void Neu_dekodieren_nur_bei_deutlich_groesserer_Flaeche()
    {
        var loaded = new ThumbnailDecodeBox(270, 240);
        loaded.IsClearlySmallerThan(new ThumbnailDecodeBox(300, 260)).Should().BeFalse("+11 % lohnt kein neues Dekodieren");
        loaded.IsClearlySmallerThan(new ThumbnailDecodeBox(320, 270)).Should().BeTrue();
        loaded.IsClearlySmallerThan(new ThumbnailDecodeBox(180, 160)).Should().BeFalse("kleiner ziehen behält das schärfere Bild");
        loaded.IsClearlySmallerThan(new ThumbnailDecodeBox(270, 300)).Should().BeTrue("auch die Höhe zählt");
    }

    [Fact]
    public void Querformat_wird_fuer_grosse_Kachel_bei_150_Prozent_scharf_geladen()
    {
        // Vorschaubild 533 × 400 (kurze Kante 400); größte Kachel 320 × 250 DIP bei 150 %: Bildbereich 456 × 303 px.
        var path = Path.Combine(_dir, "quer.jpg");
        TestImages.SaveJpeg(path, TestImages.LeftRedRightBlue(533, 400));

        var image = GalleryViewModel.TryLoadThumbnail(path, ThumbnailDecodeBox.FromTile(320, 250, 1.5));

        var bitmap = image.Should().BeAssignableTo<BitmapSource>().Subject;
        bitmap.IsFrozen.Should().BeTrue("geht vom Worker-Thread an den UI-Thread");
        bitmap.PixelHeight.Should().BeGreaterThanOrEqualTo(303, "UniformToFill darf nicht hochskalieren (bisher 120 px)");
        bitmap.PixelWidth.Should().BeGreaterThanOrEqualTo(456);
    }

    [Fact]
    public void Kleine_Kachel_dekodiert_klein_und_nie_groesser_als_die_Datei()
    {
        var path = Path.Combine(_dir, "hoch.jpg");
        TestImages.SaveJpeg(path, TestImages.LeftRedRightBlue(400, 533));

        var small = (BitmapSource)GalleryViewModel.TryLoadThumbnail(path, ThumbnailDecodeBox.FromTile(120, 122, 1.0))!;
        var huge = (BitmapSource)GalleryViewModel.TryLoadThumbnail(path, new ThumbnailDecodeBox(2000, 2000))!;

        small.PixelWidth.Should().Be(104, "Speicher: nur so groß wie der Bildbereich der Kachel");
        (huge.PixelWidth, huge.PixelHeight).Should().Be((400, 533));
    }

    [Fact]
    public void Defekte_oder_fehlende_Datei_liefert_null()
    {
        var garbage = Path.Combine(_dir, "kaputt.jpg");
        File.WriteAllBytes(garbage, [1, 2, 3, 4, 5]);

        GalleryViewModel.TryLoadThumbnail(garbage, new ThumbnailDecodeBox(180, 160)).Should().BeNull();
        GalleryViewModel.TryLoadThumbnail(Path.Combine(_dir, "fehlt.jpg"), new ThumbnailDecodeBox(180, 160)).Should().BeNull();
    }
}
