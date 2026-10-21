using PhotoViewerV3.Places.Map;
using PhotoViewerV3.Services.Places;
using PhotoViewerV3.Services.Places.Clustering;

namespace PhotoViewerV3.Wpf.Tests.Places;

/// <summary>Ausschnitt der Karte (ADR-0051): Start-Ausschnitt = Ausdehnung + Rand, ohne Fotos die Welt, Zoom um den Mauspunkt.</summary>
public sealed class MapViewportTests
{
    private static MapViewport Create(double w = 1000, double h = 700)
    {
        var vp = new MapViewport();
        vp.SetSize(w, h);
        return vp;
    }

    [Fact]
    public void Start_Ausschnitt_ist_Ausdehnung_plus_Rand()
    {
        var vp = Create();
        var extent = new GeoBounds(South: 47.07, West: 15.44, North: 48.21, East: 16.37); // Graz – Wien
        vp.FitBounds(extent);

        var (swX, swY) = vp.UnitToScreen(MercatorProjection.LongitudeToX(extent.West), MercatorProjection.LatitudeToY(extent.South));
        var (neX, neY) = vp.UnitToScreen(MercatorProjection.LongitudeToX(extent.East), MercatorProjection.LatitudeToY(extent.North));
        var pad = Math.Max(MapViewport.FitPaddingMinPixels, 700 * MapViewport.FitPaddingFraction);
        foreach (var (x, y) in new[] { (swX, swY), (neX, neY) })
        {
            x.Should().BeInRange(MapViewport.FitPaddingMinPixels - 1, 1000 - MapViewport.FitPaddingMinPixels + 1, "Ecke mit Rand sichtbar");
            y.Should().BeInRange(MapViewport.FitPaddingMinPixels - 1, 700 - MapViewport.FitPaddingMinPixels + 1);
        }

        // Die Ausdehnung füllt eine Achse bis auf den Rand (nicht unnötig weit draußen)
        var fillsX = Math.Abs((neX - swX) - (1000 - (2 * Math.Max(MapViewport.FitPaddingMinPixels, 1000 * MapViewport.FitPaddingFraction)))) < 2;
        var fillsY = Math.Abs((swY - neY) - (700 - (2 * pad))) < 2;
        (fillsX || fillsY).Should().BeTrue();
        vp.VisibleBounds.South.Should().BeLessThan(extent.South);
        vp.VisibleBounds.North.Should().BeGreaterThan(extent.North);
    }

    [Fact]
    public void Ohne_Fotos_ganze_Welt_und_einzelner_Ort_nicht_zu_nah()
    {
        var vp = Create();
        vp.FitBounds(null);
        vp.WorldSize.Should().BeLessThanOrEqualTo(700 + 0.01, "die ganze Welt passt ins Bild");
        vp.Zoom.Should().Be(vp.MinZoomLevel);

        vp.FitBounds(new GeoBounds(48.2, 16.37, 48.2, 16.37));
        vp.Zoom.Should().Be(MapViewport.MaxFitZoom, "ein einzelner Ort: Straßenebene, nicht Hausnummer");
        vp.ScreenToGeo(500, 350).Latitude.Should().BeApproximately(48.2, 1e-6);
    }

    [Fact]
    public void Zoom_um_den_Mauspunkt_laesst_den_Ort_darunter_stehen()
    {
        var vp = Create();
        vp.CenterOn(48.2, 16.37, 10);
        var before = vp.ScreenToGeo(200, 150);
        vp.ZoomAt(12, 200, 150);
        var after = vp.ScreenToGeo(200, 150);
        vp.Zoom.Should().Be(12);
        after.Latitude.Should().BeApproximately(before.Latitude, 1e-9);
        after.Longitude.Should().BeApproximately(before.Longitude, 1e-9);

        vp.ZoomAt(40, 0, 0);
        vp.Zoom.Should().Be(MapViewport.MaxZoomLevel);
        vp.ZoomAt(-5, 0, 0);
        vp.Zoom.Should().Be(vp.MinZoomLevel);
    }

    [Fact]
    public void Verschieben_bleibt_in_der_Welt_und_meldet_Aenderungen()
    {
        var vp = Create();
        var changes = 0;
        vp.Changed += (_, _) => changes++;
        vp.CenterOn(0, 0, 4);
        vp.PanBy(-1_000_000, -1_000_000);
        var (minX, minY, _, _) = vp.VisibleUnitRect();
        minX.Should().BeApproximately(0, 1e-9, "linker Weltrand nicht hineingeschoben");
        minY.Should().BeApproximately(0, 1e-9);
        changes.Should().BeGreaterThanOrEqualTo(2);

        vp.SetSize(500, 400);
        changes.Should().BeGreaterThanOrEqualTo(3, "Größenänderung zählt als Änderung");
    }
}
