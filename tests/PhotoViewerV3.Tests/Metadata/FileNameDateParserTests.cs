using PhotoViewerV3.Domain.Enums;
using PhotoViewerV3.Services.Metadata;

namespace PhotoViewerV3.Tests.Metadata;

/// <summary>Aufnahmedatum aus dem Dateinamen (ADR-0046) — Muster von Handy, Pixel, Screenshots, WhatsApp, Dropbox, Signal.</summary>
public sealed class FileNameDateParserTests
{
    [Theory]
    [InlineData("IMG_20240712_153012.jpg", "2024-07-12T15:30:12")]
    [InlineData("VID_20240712_153012.mp4", "2024-07-12T15:30:12")]
    [InlineData("PXL_20240712_153012345.jpg", "2024-07-12T15:30:12")]
    [InlineData("PXL_20240712_153012345.MP.jpg", "2024-07-12T15:30:12")]
    [InlineData("20240712_153012.jpg", "2024-07-12T15:30:12")]
    [InlineData("Screenshot_20240712-153012.png", "2024-07-12T15:30:12")]
    [InlineData("Screenshot 2026-09-30 105535.png", "2026-09-30T10:55:35")]
    [InlineData("2024-07-12 15.30.12.jpg", "2024-07-12T15:30:12")]
    [InlineData("PHOTO-2024-07-12-15-30-12.jpg", "2024-07-12T15:30:12")]
    [InlineData("signal-2024-07-12-153012.jpg", "2024-07-12T15:30:12")]
    [InlineData("20240712153012.jpg", "2024-07-12T15:30:12")]
    public void Datum_mit_Uhrzeit(string name, string expected)
    {
        var value = FileNameDateParser.Parse(name, TimeZoneInfo.Utc);

        value.Should().NotBeNull(name);
        value!.Value.Source.Should().Be(CaptureDateSource.FileName);
        value.Value.Precision.Should().Be(CaptureDatePrecision.Exact);
        value.Value.Local.Should().Be(DateTime.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("IMG-20240712-WA0001.jpg", "2024-07-12")]
    [InlineData("VID-20240712-WA0003.mp4", "2024-07-12")]
    [InlineData("VID_20240801.mov", "2024-08-01")]
    [InlineData("2024-01-11_foto.jpg", "2024-01-11")]
    [InlineData("Urlaub_2018_08_15.jpg", "2018-08-15")]
    [InlineData("scan 1999.12.31.tif", "1999-12-31")]
    public void Nur_der_Tag_ergibt_Genauigkeit_Tag_um_12_Uhr(string name, string expected)
    {
        var value = FileNameDateParser.Parse(name, TimeZoneInfo.Utc);

        value!.Value.Precision.Should().Be(CaptureDatePrecision.Day);
        value.Value.Local.Should().Be(DateTime.Parse(expected, System.Globalization.CultureInfo.InvariantCulture).AddHours(12));
    }

    [Theory]
    [InlineData("IMG_202407101_1.jpg")] // Zahl länger als ein Datum
    [InlineData("DSC01234.JPG")]
    [InlineData("p001_result_1.jpg")]
    [InlineData("IMG_20241332_120000.jpg")] // Monat 13
    [InlineData("IMG_20240230.jpg")] // 30. Februar
    [InlineData("2019-2020 Urlaub.jpg")]
    [InlineData("IMG_20990101_120000.jpg")] // Zukunft
    [InlineData("18991231.jpg")] // vor 1900
    [InlineData("")]
    public void Kein_Datum(string name) => FileNameDateParser.Parse(name, TimeZoneInfo.Utc).Should().BeNull();

    [Fact]
    public void Uhrzeit_im_Namen_ist_Ortszeit_der_angegebenen_Zone()
    {
        var vienna = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        var value = FileNameDateParser.Parse("IMG_20240712_153012.jpg", vienna)!.Value;

        value.OffsetMinutes.Should().Be(120, "Sommerzeit");
        value.Utc.Should().Be(new DateTime(2024, 7, 12, 13, 30, 12, DateTimeKind.Utc));
    }
}
