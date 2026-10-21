using Lumora.Domain.Enums;
using Lumora.Services.Metadata;
using Lumora.Slideshow;

namespace Lumora.Wpf.Tests.Slideshow;

/// <summary>Zoom-Ziel (Gesichter/Mitte, immer im Bild) und Datums-/Ortstext der Diashow (ADR-0059).</summary>
public sealed class KenBurnsAndCaptionTests
{
    [Fact]
    public void Ohne_Gesichter_zielt_der_Zoom_auf_die_Bildmitte()
    {
        KenBurnsPlanner.FocusPoint(null).Should().Be((0.5, 0.5));
        KenBurnsPlanner.FocusPoint([]).Should().Be((0.5, 0.5));

        var plan = KenBurnsPlanner.Plan([], zoomIn: true);
        plan.Start.Should().Be(SlideshowViewRect.Full);
        (plan.End.X + (plan.End.Size / 2)).Should().BeApproximately(0.5, 1e-12);
        (plan.End.Y + (plan.End.Size / 2)).Should().BeApproximately(0.5, 1e-12);
        plan.End.Scale.Should().BeApproximately(KenBurnsPlanner.DefaultZoom, 1e-12);
    }

    [Fact]
    public void Mit_Gesichtern_zielt_der_Zoom_auf_die_Mitte_aller_Gesichter()
    {
        // zwei Gesichter: umschließendes Rechteck 0,30..0,60 × 0,20..0,50 → Mitte (0,45 | 0,35)
        var focus = KenBurnsPlanner.FocusPoint([new SlideshowFaceBox(0.30, 0.20, 0.10, 0.10), new SlideshowFaceBox(0.50, 0.40, 0.10, 0.10)]);
        focus.X.Should().BeApproximately(0.45, 1e-12);
        focus.Y.Should().BeApproximately(0.35, 1e-12);

        // bei starkem Zoom liegt das Ziel genau in der Mitte des Ausschnitts …
        var rect = KenBurnsPlanner.TargetRect(focus, 3.0);
        (rect.X + (rect.Size / 2)).Should().BeApproximately(0.45, 1e-12);
        (rect.Y + (rect.Size / 2)).Should().BeApproximately(0.35, 1e-12);

        // … beim ruhigen Standard-Zoom schiebt der Rand den Ausschnitt ins Bild (oben bündig), das Ziel bleibt darin
        var standard = KenBurnsPlanner.TargetRect(focus, KenBurnsPlanner.DefaultZoom);
        standard.Y.Should().Be(0);
        standard.IsInsideImage.Should().BeTrue();
        (standard.X + (standard.Size / 2)).Should().BeApproximately(0.45, 1e-12);
    }

    [Fact]
    public void Ungueltige_Rahmen_werden_ignoriert()
        => KenBurnsPlanner.FocusPoint([new SlideshowFaceBox(0.9, 0.9, 0, 0), new SlideshowFaceBox(double.NaN, 0.1, 0.1, 0.1)]).Should().Be((0.5, 0.5));

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.98, 0.98)]
    [InlineData(0.0, 0.99)]
    [InlineData(0.95, 0.02)]
    [InlineData(0.5, 0.5)]
    public void Zoom_bleibt_im_Bild_auch_bei_Gesichtern_am_Rand(double faceX, double faceY)
    {
        var faces = new[] { new SlideshowFaceBox(faceX, faceY, Math.Min(0.02, 1 - faceX), Math.Min(0.01, 1 - faceY)) };
        foreach (var zoomIn in new[] { true, false })
        {
            foreach (var zoom in new[] { 1.05, KenBurnsPlanner.DefaultZoom, 1.5, 3.0 })
            {
                var plan = KenBurnsPlanner.Plan(faces, zoomIn, zoom);
                plan.Start.IsInsideImage.Should().BeTrue();
                plan.End.IsInsideImage.Should().BeTrue();
                for (var t = 0.0; t <= 1.0; t += 0.05)
                {
                    KenBurnsPlanner.At(plan, t).IsInsideImage.Should().BeTrue($"t={t:0.00}, Zoom {zoom}, hinein={zoomIn}: keine schwarzen Ränder");
                }
            }
        }
    }

    [Fact]
    public void Transformation_legt_den_Ausschnitt_auf_die_ganze_Flaeche()
    {
        var rect = new SlideshowViewRect(0.1, 0.05, 0.8);
        var (scale, tx, ty) = rect.ToTransform(1000, 600);
        scale.Should().BeApproximately(1.25, 1e-12);
        // linke obere Ecke des Ausschnitts landet bei (0 | 0), rechte untere bei der Elementgröße
        ((0.1 * 1000 * scale) + tx).Should().BeApproximately(0, 1e-9);
        ((0.05 * 600 * scale) + ty).Should().BeApproximately(0, 1e-9);
        ((0.9 * 1000 * scale) + tx).Should().BeApproximately(1000, 1e-9);
        ((0.85 * 600 * scale) + ty).Should().BeApproximately(600, 1e-9);
    }

    [Theory]
    [InlineData(CaptureDatePrecision.Exact, "Samstag, 12. Juli 2025")]
    [InlineData(CaptureDatePrecision.Day, "Samstag, 12. Juli 2025")]
    [InlineData(CaptureDatePrecision.Month, "Juli 2025")]
    [InlineData(CaptureDatePrecision.Year, "2025")]
    public void Datumstext_je_Genauigkeit(CaptureDatePrecision precision, string expected)
    {
        var value = CaptureDateValue.FromWallClock(new DateTime(2025, 7, 12, 23, 30, 0), 120, CaptureDateSource.Exif, precision);
        SlideshowCaption.FormatDate(value).Should().Be(expected);
    }

    [Fact]
    public void Datum_als_Wanduhrzeit_der_Aufnahme_nicht_in_UTC()
    {
        // 23:30 Ortszeit in UTC−5 — in UTC schon der 13.: maßgeblich ist die Wanduhrzeit der Aufnahme, nicht UTC
        var value = CaptureDateValue.FromWallClock(new DateTime(2025, 7, 12, 23, 30, 0), -300, CaptureDateSource.Exif, CaptureDatePrecision.Exact);
        SlideshowCaption.FormatDate(value).Should().Be("Samstag, 12. Juli 2025");
    }

    [Fact]
    public void Ort_leer_ergibt_nur_das_Datum()
    {
        var value = CaptureDateValue.FromWallClock(new DateTime(2025, 7, 13, 9, 0, 0), 120, CaptureDateSource.Exif, CaptureDatePrecision.Exact);

        var withoutPlace = SlideshowCaption.For(value, "  ");
        withoutPlace.DateText.Should().Be("Sonntag, 13. Juli 2025");
        withoutPlace.PlaceText.Should().BeNull();
        withoutPlace.HasPlace.Should().BeFalse();

        var withPlace = SlideshowCaption.For(value, "Grado, Italien");
        withPlace.PlaceText.Should().Be("Grado, Italien");

        var onlyPlace = SlideshowCaption.For(null, "Triest, Italien");
        onlyPlace.DateText.Should().BeNull();
        onlyPlace.HasPlace.Should().BeTrue();

        SlideshowCaption.For(null, null).IsEmpty.Should().BeTrue();
    }
}
