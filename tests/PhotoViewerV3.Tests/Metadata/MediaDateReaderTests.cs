using PhotoViewerV3.Domain.Enums;
using PhotoViewerV3.Services.Metadata;

namespace PhotoViewerV3.Tests.Metadata;

/// <summary>Aufnahmedatum aus Metadaten je Format mit synthetischen Dateien (ADR-0046).</summary>
public sealed class MediaDateReaderTests
{
    private static readonly DateTime Expected = new(2024, 7, 12, 15, 30, 12);

    private static CaptureDateValue? Read(byte[] bytes) => MediaDateReader.Read(new MemoryStream(bytes));

    [Fact]
    public void Jpeg_mit_DateTimeOriginal_und_OffsetTimeOriginal()
    {
        var value = Read(SyntheticMediaFiles.Jpeg(SyntheticMediaFiles.Tiff("2024:07:12 15:30:12", "+02:00")));

        value.Should().NotBeNull();
        value!.Value.Source.Should().Be(CaptureDateSource.Exif);
        value.Value.Precision.Should().Be(CaptureDatePrecision.Exact);
        value.Value.OffsetMinutes.Should().Be(120);
        value.Value.Utc.Should().Be(new DateTime(2024, 7, 12, 13, 30, 12, DateTimeKind.Utc));
        value.Value.Local.Should().Be(Expected);
    }

    [Fact]
    public void Jpeg_ohne_Zone_nimmt_die_Zeitzone_des_PCs_und_zeigt_die_Wanduhrzeit()
    {
        var value = Read(SyntheticMediaFiles.Jpeg(SyntheticMediaFiles.Tiff("2024:07:12 15:30:12")));

        value!.Value.Local.Should().Be(Expected);
        value.Value.OffsetMinutes.Should().Be((int)TimeZoneInfo.Local.GetUtcOffset(Expected).TotalMinutes);
    }

    [Fact]
    public void Tiff_big_endian_direkt_als_Datei()
    {
        var value = Read(SyntheticMediaFiles.Tiff("2024:07:12 15:30:12", "-05:00", bigEndian: true));

        value!.Value.Utc.Should().Be(new DateTime(2024, 7, 12, 20, 30, 12, DateTimeKind.Utc));
        value.Value.OffsetMinutes.Should().Be(-300);
    }

    [Fact]
    public void Png_eXIf_auch_hinter_IDAT()
        => Read(SyntheticMediaFiles.Png(SyntheticMediaFiles.Tiff("2024:07:12 15:30:12", "+02:00")))!.Value.Local.Should().Be(Expected);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WebP_EXIF_mit_und_ohne_Exif_Praefix(bool prefix)
        => Read(SyntheticMediaFiles.WebP(SyntheticMediaFiles.Tiff("2024:07:12 15:30:12", "+02:00"), prefix))!.Value.Local.Should().Be(Expected);

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void Heic_Exif_Element_ueber_iinf_und_iloc(int infeVersion)
    {
        var value = Read(SyntheticMediaFiles.Heic(SyntheticMediaFiles.Tiff("2024:07:12 15:30:12", "+02:00"), infeVersion));

        value!.Value.Source.Should().Be(CaptureDateSource.Exif);
        value.Value.Local.Should().Be(Expected);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Mp4_mvhd_ist_UTC_auch_hinter_grossem_mdat(bool moovAfterMdat, bool version1)
    {
        var utc = new DateTime(2024, 7, 12, 13, 30, 12, DateTimeKind.Utc);
        var value = Read(SyntheticMediaFiles.Mp4(utc, moovAfterMdat, version1));

        value!.Value.Source.Should().Be(CaptureDateSource.Video);
        value.Value.Utc.Should().Be(utc);
        value.Value.OffsetMinutes.Should().Be((int)TimeZoneInfo.Local.GetUtcOffset(utc).TotalMinutes);
    }

    [Fact]
    public void Mov_Apple_creationdate_hat_Vorrang_vor_mvhd_und_traegt_die_Zone()
    {
        var value = Read(SyntheticMediaFiles.Mp4(new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddYears(-10), appleCreationDate: "2024-07-12T15:30:12+0200"));

        value!.Value.Source.Should().Be(CaptureDateSource.Video);
        value.Value.OffsetMinutes.Should().Be(120);
        value.Value.Local.Should().Be(Expected);
    }

    [Fact]
    public void Mp4_ohne_Erstellzeit_oder_mit_1904_ergibt_nichts()
    {
        Read(SyntheticMediaFiles.Mp4(null)).Should().BeNull();
        Read(SyntheticMediaFiles.Mp4(new DateTime(1904, 1, 1, 0, 0, 1, DateTimeKind.Utc))).Should().BeNull();
    }

    [Fact]
    public void Ohne_EXIF_unbekanntes_Format_und_Abgeschnittenes_ergeben_null_ohne_Ausnahme()
    {
        Read(SyntheticMediaFiles.Jpeg(null)).Should().BeNull();
        Read(SyntheticMediaFiles.Jpeg(SyntheticMediaFiles.Tiff(null))).Should().BeNull();
        Read("PK\u0003\u0004 kein Bild, ein ZIP-Archiv"u8.ToArray()).Should().BeNull();
        Read(new byte[3]).Should().BeNull();

        var full = SyntheticMediaFiles.Jpeg(SyntheticMediaFiles.Tiff("2024:07:12 15:30:12"));
        for (var length = 4; length < full.Length; length += 7)
        {
            var act = () => Read(full[..length]);
            act.Should().NotThrow();
        }

        var heic = SyntheticMediaFiles.Heic(SyntheticMediaFiles.Tiff("2024:07:12 15:30:12"));
        for (var length = 16; length < heic.Length; length += 5)
        {
            var act = () => Read(heic[..length]);
            act.Should().NotThrow();
        }
    }

    [Fact]
    public void Nullwerte_im_EXIF_ergeben_null()
        => Read(SyntheticMediaFiles.Jpeg(SyntheticMediaFiles.Tiff("0000:00:00 00:00:00"))).Should().BeNull();

    [Theory]
    [InlineData("2024:07:12 15:30:12", "2024-07-12T15:30:12")]
    [InlineData("2024-07-12 15:30:12", "2024-07-12T15:30:12")]
    [InlineData("2024:07:12 15:30", "2024-07-12T15:30:00")]
    [InlineData("2024:07:12T15:30:12", "2024-07-12T15:30:12")]
    [InlineData("    :  :     :  :  ", null)]
    [InlineData("0000:00:00 00:00:00", null)]
    [InlineData("", null)]
    public void ParseExifDateTime(string text, string? expected)
        => ExifTiffReader.ParseExifDateTime(text).Should().Be(expected is null ? null : DateTime.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));

    [Theory]
    [InlineData("+02:00", 120)]
    [InlineData("-05:30", -330)]
    [InlineData("+0100", 60)]
    [InlineData("Z", 0)]
    [InlineData("+25:00", null)]
    [InlineData("02:00", null)]
    [InlineData(null, null)]
    public void ParseOffset(string? text, int? expected) => ExifTiffReader.ParseOffset(text).Should().Be(expected);

    [Fact]
    public void Leser_faellt_auf_den_Dateinamen_zurueck_und_liest_nie_aus_der_Cloud()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PhotoViewerV3.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var withExif = Path.Combine(dir, "IMG_20000101_000000.jpg");
            File.WriteAllBytes(withExif, SyntheticMediaFiles.Jpeg(SyntheticMediaFiles.Tiff("2024:07:12 15:30:12")));
            var noExif = Path.Combine(dir, "IMG_20240712_153012.jpg");
            File.WriteAllBytes(noExif, SyntheticMediaFiles.Jpeg(null));
            var nothing = Path.Combine(dir, "foto.jpg");
            File.WriteAllBytes(nothing, SyntheticMediaFiles.Jpeg(null));

            var reader = new CaptureDateReader();
            reader.Read(withExif)!.Value.Source.Should().Be(CaptureDateSource.Exif, "EXIF vor Dateiname");
            reader.Read(noExif)!.Value.Source.Should().Be(CaptureDateSource.FileName);
            reader.Read(noExif)!.Value.Local.Should().Be(Expected);
            reader.Read(nothing).Should().BeNull();
            reader.Read(Path.Combine(dir, "fehlt.jpg")).Should().BeNull();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
