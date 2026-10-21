using PhotoViewerV3.Gallery.ViewModels;

namespace PhotoViewerV3.Wpf.Tests.Gallery;

/// <summary>
/// Collage (ADR-0044): Vorschaubilder werden für die Reihenhöhe × Skalierung dekodiert (Querformat: kurze Kante,
/// Hochformat: Breite aus der Höhe), nie hochskaliert; beim Wechsel zurück zu den Kacheln reichen die Collage-Bilder.
/// </summary>
public sealed class CollageThumbnailDecodeTests
{
    [Theory]
    [InlineData(180, 1.0, 72, 180)]
    [InlineData(180, 1.5, 108, 270)]
    [InlineData(320, 1.5, 192, 480)]
    [InlineData(120, 0, 48, 120)] // unbekannte Skalierung → 100 %
    public void Flaeche_ist_Reihenhoehe_mal_Skalierung(double rowHeight, double dpi, int expectedWidth, int expectedHeight)
        => ThumbnailDecodeBox.FromCollageRow(rowHeight, dpi).Should().Be(new ThumbnailDecodeBox(expectedWidth, expectedHeight));

    [Theory]
    [InlineData(533, 400, 360)] // Querformat 4:3: Höhe 270 → 360 breit (kurze Kante reicht)
    [InlineData(400, 533, 203)] // Hochformat 3:4: Höhe 270 → Breite 203
    [InlineData(1200, 400, 810)] // Panorama 3:1: Höhe 270 → 810 breit
    public void Dekodiert_wird_auf_die_Reihenhoehe(int thumbWidth, int thumbHeight, int expectedDecodeWidth)
    {
        var box = ThumbnailDecodeBox.FromCollageRow(180, 1.5); // 108 × 270
        var decodeWidth = box.DecodeWidthFor(thumbWidth, thumbHeight);
        decodeWidth.Should().Be(expectedDecodeWidth);
        (decodeWidth * thumbHeight / (double)thumbWidth).Should().BeApproximately(270, 1, "Bildhöhe = Reihenhöhe × Skalierung");
    }

    [Fact]
    public void Sehr_hohes_Bild_bedeckt_die_schmalste_Zelle_und_wird_nie_hochskaliert()
    {
        var box = ThumbnailDecodeBox.FromCollageRow(180, 1.5); // 108 × 270
        box.DecodeWidthFor(400, 1200).Should().Be(108, "1:3 wird in der Zelle auf 0,4:1 begrenzt → die Breite bestimmt");
        ThumbnailDecodeBox.FromCollageRow(320, 2.0).DecodeWidthFor(400, 533).Should().Be(0, "Vorschaubild kleiner als die Fläche: volle Größe");
    }

    [Fact]
    public void Collage_Bilder_reichen_fuer_die_Kacheln_nicht_umgekehrt()
    {
        var tile = ThumbnailDecodeBox.FromTile(180, 160, 1.5); // 246 × 168
        var collage = ThumbnailDecodeBox.FromCollageRow(180, 1.5); // 108 × 270

        // Querformat 4:3 in der Collage dekodiert: 360 × 270 bedeckt die Kachel → kein Neu-Dekodieren beim Zurückschalten
        tile.IsCoveredBy(360, 270).Should().BeTrue();

        // In der Kachel dekodiert: 246 × 185 ist für die Collage-Reihe (270 hoch) zu klein → neu dekodieren
        collage.IsCoveredBy(246, 185).Should().BeFalse();
        tile.IsClearlySmallerThan(collage).Should().BeTrue();

        // Hochformat aus der Kachel (246 × 328) reicht der Collage (Höhe 270)
        collage.IsCoveredBy(246, 328).Should().BeTrue();
    }
}
