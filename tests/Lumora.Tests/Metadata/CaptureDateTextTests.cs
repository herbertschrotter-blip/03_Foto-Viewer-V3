using Lumora.Domain.Enums;
using Lumora.Services.Metadata;

namespace Lumora.Tests.Metadata;

/// <summary>Anzeigetexte des Aufnahmedatums (Lightbox, Liste, Tooltip; ADR-0046).</summary>
public sealed class CaptureDateTextTests
{
    private static CaptureDateValue Value(DateTime wall, CaptureDateSource source, CaptureDatePrecision precision)
        => CaptureDateValue.FromWallClock(wall, 120, source, precision);

    [Fact]
    public void Genau_mit_Herkunft()
        => CaptureDateText.Format(Value(new DateTime(2024, 7, 12, 15, 30, 12), CaptureDateSource.Exif, CaptureDatePrecision.Exact))
            .Should().Be("12.07.2024 15:30 (EXIF)");

    [Fact]
    public void Ungefaehr_Jahr_und_Monat()
    {
        CaptureDateText.Format(Value(new DateTime(1982, 7, 1, 12, 0, 0), CaptureDateSource.Manual, CaptureDatePrecision.Year)).Should().Be("ca. 1982 (manuell)");
        CaptureDateText.Format(Value(new DateTime(1982, 7, 15, 12, 0, 0), CaptureDateSource.Manual, CaptureDatePrecision.Month)).Should().Be("ca. Juli 1982 (manuell)");
        CaptureDateText.Format(Value(new DateTime(2024, 7, 12, 12, 0, 0), CaptureDateSource.FileName, CaptureDatePrecision.Day)).Should().Be("12.07.2024 (Dateiname)");
    }

    [Fact]
    public void Ohne_Datum()
    {
        CaptureDateText.Describe(null, null).Should().StartWith("noch nicht gelesen");
        CaptureDateText.Describe(null, CaptureDateSource.Unknown).Should().StartWith("unbekannt");
    }

    [Fact]
    public void Aus_den_Katalogspalten_mit_gespeichertem_Abstand()
    {
        var utc = new DateTimeOffset(2024, 7, 12, 13, 30, 12, TimeSpan.Zero).ToUnixTimeSeconds();
        var info = CaptureDateInfo.FromColumns(7, utc, (int)CaptureDateSource.Video, 0, 120);

        info.Value!.Value.Local.Should().Be(new DateTime(2024, 7, 12, 15, 30, 12));
        info.Text.Should().Be("12.07.2024 15:30 (Video)");
        CaptureDateInfo.FromColumns(7, null, (int)CaptureDateSource.Unknown, 0, null).Value.Should().BeNull();
    }

    [Fact]
    public void Verschiebung_als_Text()
    {
        CaptureDateEdit.FormatDelta(TimeSpan.FromHours(-1)).Should().Be("−1 Stunde");
        CaptureDateEdit.FormatDelta(new TimeSpan(2, 3, 5, 0)).Should().Be("+2 Tage 3 Stunden 5 Minuten");
        new CaptureDateEdit.Approximate(1982, null).Describe().Should().Be("Aufnahmedatum ca. 1982");
    }
}
