using Lumora.Domain.Enums;
using Lumora.Services.Metadata;
using Lumora.Services.Places.Accuracy;
using Lumora.Tests.Metadata;

namespace Lumora.Tests.Places.Tracks;

/// <summary>Erkennung „Position geschätzt“ (ADR-0062): Tag-Kombinationen, Verfahren, Fehlerradius, Sprung-Regel, EXIF-Leser.</summary>
public sealed class GeoAccuracyRulesTests
{
    private static GpsExifDetails Details(bool coordinate = true, bool altitude = false, bool time = false, string? method = null, double? error = null)
        => new(coordinate, altitude, time, time ? new DateTime(2020, 3, 15, 13, 56, 0, DateTimeKind.Utc) : null, method, error, null, null);

    [Theory]
    [InlineData(false, false, GeoAccuracy.Estimated, GeoAccuracyReason.NoAltitudeNoTime)]
    [InlineData(true, false, GeoAccuracy.Exact, GeoAccuracyReason.None)]
    [InlineData(false, true, GeoAccuracy.Exact, GeoAccuracyReason.None)]
    [InlineData(true, true, GeoAccuracy.Exact, GeoAccuracyReason.None)]
    public void Nur_Breite_und_Laenge_ohne_Hoehe_und_Zeit_ist_geschaetzt(bool altitude, bool time, GeoAccuracy expected, GeoAccuracyReason reason)
        => GeoAccuracyRules.Classify(Details(altitude: altitude, time: time)).Should().Be((expected, reason));

    [Theory]
    [InlineData("NETWORK")]
    [InlineData("CELLID")]
    [InlineData("wlan")]
    [InlineData("WiFi")]
    [InlineData("network-fused")]
    public void Netzwerk_Verfahren_ist_geschaetzt_auch_mit_Hoehe_und_Zeit(string method)
    {
        var (accuracy, reason) = GeoAccuracyRules.Classify(Details(altitude: true, time: true, method: method));
        accuracy.Should().Be(GeoAccuracy.Estimated);
        reason.Should().Be(GeoAccuracyReason.NetworkMethod);
    }

    [Fact]
    public void GPS_Verfahren_und_kleiner_Fehlerradius_bleiben_genau_grosser_wird_geschaetzt()
    {
        GeoAccuracyRules.Classify(Details(altitude: true, method: "GPS", error: 12)).Accuracy.Should().Be(GeoAccuracy.Exact);
        GeoAccuracyRules.Classify(Details(altitude: true, error: 500)).Accuracy.Should().Be(GeoAccuracy.Exact, "500 m ist die Grenze");
        GeoAccuracyRules.Classify(Details(altitude: true, error: 1200)).Should().Be((GeoAccuracy.Estimated, GeoAccuracyReason.LargePositionError));
        GeoAccuracyRules.Classify(Details(error: 1200, method: "CELLID")).Reason.Should()
            .Be(GeoAccuracyReason.LargePositionError | GeoAccuracyReason.NetworkMethod | GeoAccuracyReason.NoAltitudeNoTime);
    }

    [Fact]
    public void Ohne_Koordinate_unbekannt_und_Texte()
    {
        GeoAccuracyRules.Classify(Details(coordinate: false)).Should().Be((GeoAccuracy.Unknown, GeoAccuracyReason.None));
        GeoAccuracyRules.Describe(GeoAccuracyReason.NoAltitudeNoTime).Should().Be("ohne GPS-Höhe/-Zeit");
        GeoAccuracyRules.Explain(GeoAccuracyReason.NoAltitudeNoTime).Should().Contain("Mobilfunk/WLAN");
        GeoAccuracyRules.Describe(GeoAccuracyReason.ImpossibleJump).Should().Contain("Sprung");
    }

    [Fact]
    public void EXIF_Leser_liest_Hoehe_GPS_Zeit_Verfahren_Fehler_Zone_und_Kamera_aus_einem_JPEG()
    {
        var wall = new DateTime(2020, 3, 15, 14, 56, 10);
        var tiff = TrackTestFiles.PhoneTiff(wall, 46.9, 15.0, altitude: 1853.4, gpsUtc: new DateTime(2020, 3, 15, 13, 56, 9), method: "GPS", error: 8.5, offset: "+01:00", make: "Apple", model: "iPhone 12");
        var details = ExifGpsDetailsReader.Read(new MemoryStream(SyntheticMediaFiles.Jpeg(tiff)));

        details.Should().NotBeNull();
        var d = details!.Value;
        d.HasCoordinate.Should().BeTrue();
        d.HasAltitude.Should().BeTrue();
        d.HasGpsTime.Should().BeTrue();
        d.GpsTimeUtc.Should().Be(new DateTime(2020, 3, 15, 13, 56, 9, DateTimeKind.Utc));
        d.ProcessingMethod.Should().Be("GPS");
        d.HorizontalError.Should().BeApproximately(8.5, 1e-9);
        d.OffsetMinutes.Should().Be(60);
        d.CameraModel.Should().Be("Apple iPhone 12");
        GeoAccuracyRules.Classify(d).Accuracy.Should().Be(GeoAccuracy.Exact);
    }

    [Fact]
    public void EXIF_Leser_Handy_Schaetzung_nur_Tags_1_bis_4()
    {
        var tiff = TrackTestFiles.PhoneTiff(new DateTime(2020, 3, 15, 14, 56, 0), 46.85, 15.12);
        var d = ExifGpsDetailsReader.Read(new MemoryStream(SyntheticMediaFiles.Jpeg(tiff)))!.Value;
        d.HasAltitude.Should().BeFalse();
        d.HasGpsTime.Should().BeFalse();
        d.CameraModel.Should().Be("Samsung SM-G991B");
        GeoAccuracyRules.Classify(d).Should().Be((GeoAccuracy.Estimated, GeoAccuracyReason.NoAltitudeNoTime));
        ExifGpsDetailsReader.Read(new MemoryStream([1, 2, 3])).Should().BeNull("kein Bild");
    }

    [Theory]
    [InlineData("NIKON CORPORATION", "NIKON D750", "NIKON D750")]
    [InlineData("Canon", "Canon EOS 80D", "Canon EOS 80D")]
    [InlineData("samsung", "SM-G991B", "samsung SM-G991B")]
    [InlineData(null, "X100V", "X100V")]
    [InlineData("GoPro", null, "GoPro")]
    public void Kamera_aus_Hersteller_und_Modell(string? make, string? model, string expected)
        => ExifGpsDetailsReader.JoinCamera(make, model).Should().Be(expected);

    // ---- Sprung-Regel ----

    private static JumpSample S(int id, int minute, double lat, double lon, bool strong = false, bool exact = true)
        => new(id, 1_600_000_000 + (minute * 60L), lat, lon, exact, strong);

    [Fact]
    public void Sprung_zu_starkem_Nachbarn_markiert_das_schwache_Foto()
    {
        // Graz ↔ Wien (≈ 145 km) in 10 min — unmöglich; der starke Nachbar (mit GPS-Zeit) gewinnt
        var flagged = GeoJumpDetector.Detect([S(1, 0, 47.07, 15.44, strong: true), S(2, 10, 48.21, 16.37), S(3, 20, 47.071, 15.441, strong: true)]);
        flagged.Should().Equal(2);
    }

    [Fact]
    public void Zwei_schwache_Fotos_die_sich_nur_gegenseitig_widersprechen_bleiben_unmarkiert()
        => GeoJumpDetector.Detect([S(1, 0, 47.07, 15.44), S(2, 10, 48.21, 16.37)]).Should().BeEmpty();

    [Fact]
    public void Mehrheit_schwacher_Nachbarn_starke_nie_markiert_kurze_Wege_und_ferne_Zeiten_zaehlen_nicht()
    {
        GeoJumpDetector.Detect([S(1, 0, 47.07, 15.44), S(2, 5, 47.071, 15.441), S(3, 10, 48.21, 16.37), S(4, 15, 47.072, 15.442)])
            .Should().Equal(3);
        GeoJumpDetector.Detect([S(1, 0, 47.07, 15.44), S(2, 10, 48.21, 16.37, strong: true)]).Should().BeEquivalentTo([1], "nur das schwache");
        GeoJumpDetector.Detect([S(1, 0, 47.07, 15.44, strong: true), S(2, 1, 47.08, 15.45)]).Should().BeEmpty("1,3 km in 1 min: unter der Mindeststrecke");
        GeoJumpDetector.Detect([S(1, 0, 47.07, 15.44, strong: true), S(2, 120, 48.21, 16.37)]).Should().BeEmpty("2 h auseinander: nicht zeitnah");
        GeoJumpDetector.Detect([S(1, 0, 47.07, 15.44, strong: true), S(2, 10, 48.21, 16.37, exact: false)]).Should().BeEmpty("Geschätzte prüft die Tag-Regel");
    }
}
