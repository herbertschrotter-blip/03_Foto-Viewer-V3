using System.Text;
using Lumora.Domain.Enums;
using Lumora.Services.Metadata;
using Lumora.Services.Places;
using Lumora.Tests.Metadata;

namespace Lumora.Tests.Places;

/// <summary>
/// GPS schreiben (ADR-0050): JPEG verlustfrei (nur das APP1-„Exif“-Segment ändert sich, alle anderen Bytes bleiben —
/// insbesondere die Bilddaten ab SOS), XMP-Begleitdatei anlegen/ergänzen/entfernen, Fehler ohne halbe Dateien.
/// </summary>
public sealed class GeoWriterTests : IDisposable
{
    private static readonly GeoCoordinate Graz = new(47.0707, 15.4395);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lumora-geowrite-" + Guid.NewGuid().ToString("N"));

    public GeoWriterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_dir))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_dir, recursive: true);
    }

    private static byte[] SosTail(byte[] jpeg)
    {
        for (var i = 2; i < jpeg.Length - 1; i++)
        {
            if (jpeg[i] == 0xFF && jpeg[i + 1] == 0xDA)
            {
                return jpeg[i..];
            }
        }

        throw new InvalidOperationException("kein SOS");
    }

    /// <summary>Alles außer dem EXIF-Segment ist gleich: Kopf bis zum Segment und der Rest danach.</summary>
    private static void OnlyExifChanged(byte[] before, byte[] after)
    {
        var a = JpegExifGpsWriter.Locate(new MemoryStream(before))!.Value;
        var b = JpegExifGpsWriter.Locate(new MemoryStream(after))!.Value;
        a.Offset.Should().Be(b.Offset);
        after.AsSpan(0, (int)b.Offset).SequenceEqual(before.AsSpan(0, (int)a.Offset)).Should().BeTrue("Bytes vor dem Segment unverändert");
        after.AsSpan((int)(b.Offset + b.Length)).SequenceEqual(before.AsSpan((int)(a.Offset + a.Length))).Should().BeTrue("Bytes nach dem Segment unverändert");
        SosTail(after).SequenceEqual(SosTail(before)).Should().BeTrue("Bilddaten ab SOS unverändert");
    }

    public static TheoryData<string, byte[]> Jpegs => new()
    {
        { "ohne EXIF, mit JFIF", SyntheticMediaFiles.Jpeg(null) },
        { "ohne EXIF, ohne JFIF", SyntheticMediaFiles.Jpeg(null, withJfif: false) },
        { "EXIF ohne GPS (II)", SyntheticMediaFiles.Jpeg(SyntheticMediaFiles.Tiff("2024:07:12 15:30:12", "+02:00")) },
        { "EXIF ohne GPS (MM)", SyntheticMediaFiles.Jpeg(SyntheticMediaFiles.Tiff("2024:07:12 15:30:12", bigEndian: true)) },
        { "EXIF mit GPS (II)", SyntheticMediaFiles.Jpeg(SyntheticGpsFiles.Tiff(48.2082, 16.3738, withMakerNoteLikeData: true)) },
        { "EXIF mit GPS (MM)", SyntheticMediaFiles.Jpeg(SyntheticGpsFiles.Tiff(48.2082, 16.3738, bigEndian: true)) },
    };

    [Theory]
    [MemberData(nameof(Jpegs))]
    public void JPEG_setzen_aendert_nur_das_EXIF_Segment(string name, byte[] jpeg)
    {
        _ = name;
        var path = Path.Combine(_dir, "a.jpg");
        File.WriteAllBytes(path, jpeg);

        var write = GeoFileWriter.Write(path, 0, Graz, keepModifiedTime: true);

        write.Kind.Should().Be(GeoTagWriteKind.JpegExif);
        var after = File.ReadAllBytes(path);
        OnlyExifChanged(jpeg, after);
        var read = MediaGpsReader.Read(new MemoryStream(after))!.Value.Coordinate;
        read.Latitude.Should().BeApproximately(Graz.Latitude, 1e-6);
        read.Longitude.Should().BeApproximately(Graz.Longitude, 1e-6);
        if (MediaDateReader.Read(new MemoryStream(jpeg)) is { } date)
        {
            MediaDateReader.Read(new MemoryStream(after)).Should().Be(date, "übriges EXIF (Aufnahmedatum) bleibt lesbar");
        }

        write.NewSize.Should().Be(after.Length);
        write.OldSize.Should().Be(jpeg.Length);
    }

    [Fact]
    public void JPEG_entfernen_und_wieder_setzen()
    {
        var jpeg = SyntheticMediaFiles.Jpeg(SyntheticGpsFiles.Tiff(48.2082, 16.3738));
        var path = Path.Combine(_dir, "b.jpg");
        File.WriteAllBytes(path, jpeg);

        GeoFileWriter.Write(path, 0, null, keepModifiedTime: true).Kind.Should().Be(GeoTagWriteKind.JpegExif);
        MediaGpsReader.Read(path).Should().BeNull();
        MediaDateReader.Read(path).Should().NotBeNull("Datum bleibt");

        GeoFileWriter.Write(path, 0, null, keepModifiedTime: true).Kind.Should().Be(GeoTagWriteKind.CatalogOnly, "nichts mehr zu entfernen");
        GeoFileWriter.Write(path, 0, Graz, keepModifiedTime: true);
        GeoFileWriter.Write(path, 0, new GeoCoordinate(-33.8688, 151.2093), keepModifiedTime: true);
        MediaGpsReader.Read(path)!.Value.Coordinate.Longitude.Should().BeApproximately(151.2093, 1e-6);
        SosTail(File.ReadAllBytes(path)).SequenceEqual(SosTail(jpeg)).Should().BeTrue();
    }

    [Fact]
    public void Aenderungsdatum_beibehalten_oder_nicht()
    {
        var path = Path.Combine(_dir, "c.jpg");
        File.WriteAllBytes(path, SyntheticMediaFiles.Jpeg(null));
        var old = new DateTime(2010, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, old);

        GeoFileWriter.Write(path, 0, Graz, keepModifiedTime: true);
        File.GetLastWriteTimeUtc(path).Should().Be(old);

        GeoFileWriter.Write(path, 0, new GeoCoordinate(1, 2), keepModifiedTime: false);
        File.GetLastWriteTimeUtc(path).Should().BeAfter(old.AddYears(10));
    }

    [Fact]
    public void Fehler_schreibgeschuetzt_fehlt_Archiv_kaputt_lassen_die_Datei_unveraendert()
    {
        var path = Path.Combine(_dir, "ro.jpg");
        var jpeg = SyntheticMediaFiles.Jpeg(null);
        File.WriteAllBytes(path, jpeg);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        FluentActions.Invoking(() => GeoFileWriter.Write(path, 0, Graz, true)).Should().Throw<GeoWriteException>().WithMessage("*schreibgeschützt*");
        File.ReadAllBytes(path).Should().Equal(jpeg);

        FluentActions.Invoking(() => GeoFileWriter.Write(Path.Combine(_dir, "weg.jpg"), 0, Graz, true)).Should().Throw<GeoWriteException>().WithMessage("*fehlt*");
        FluentActions.Invoking(() => GeoFileWriter.Write(path, 2, Graz, true)).Should().Throw<GeoWriteException>().WithMessage("*Archive*");

        var broken = Path.Combine(_dir, "kaputt.jpg");
        File.WriteAllBytes(broken, [0xFF, 0xD8, 0xFF, 0xE1, 0x00, 0x01, 0x00]);
        FluentActions.Invoking(() => GeoFileWriter.Write(broken, 0, Graz, true)).Should().Throw<GeoWriteException>();
        Directory.EnumerateFiles(_dir, "*.lumora.tmp").Should().BeEmpty("keine Temp-Reste");
    }

    [Fact]
    public void Gesperrte_Datei_meldet_Grund()
    {
        var path = Path.Combine(_dir, "lock.jpg");
        File.WriteAllBytes(path, SyntheticMediaFiles.Jpeg(null));
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            FluentActions.Invoking(() => GeoFileWriter.Write(path, 0, Graz, true)).Should().Throw<GeoWriteException>().WithMessage("*Verwendung*");
        }

        Directory.EnumerateFiles(_dir, "*.lumora.tmp").Should().BeEmpty();
    }

    [Fact]
    public void Zu_grosses_EXIF_wird_abgelehnt()
    {
        var huge = SyntheticMediaFiles.Tiff("2024:07:12 15:30:12").Concat(new byte[65_400]).ToArray();
        var segment = new byte[] { 0xFF, 0xE1, 0, 0 }.Concat("Exif\0\0"u8.ToArray()).Concat(huge).ToArray();
        FluentActions.Invoking(() => JpegExifGpsWriter.BuildSegment(segment, Graz)).Should().Throw<InvalidDataException>().WithMessage("*64 KB*");
    }

    [Fact]
    public void Video_nur_Katalog_andere_Bilder_XMP_Begleitdatei()
    {
        var video = Path.Combine(_dir, "v.mp4");
        File.WriteAllBytes(video, SyntheticMediaFiles.Mp4(null));
        GeoFileWriter.Write(video, 1, Graz, true).Should().Be(GeoFileWrite.CatalogOnly);

        var heic = Path.Combine(_dir, "h.heic");
        var heicBytes = SyntheticMediaFiles.Heic(SyntheticMediaFiles.Tiff("2024:07:12 15:30:12"));
        File.WriteAllBytes(heic, heicBytes);
        var write = GeoFileWriter.Write(heic, 0, Graz, true);
        write.Kind.Should().Be(GeoTagWriteKind.XmpSidecar);
        write.WrittenPath.Should().Be(heic + ".xmp");
        write.OldBytes.Should().BeNull("Begleitdatei neu");
        File.ReadAllBytes(heic).Should().Equal(heicBytes, "die HEIC-Datei selbst bleibt unangetastet");
        new GeoLocationReader().Read(heic)!.Value.Coordinate.Latitude.Should().BeApproximately(Graz.Latitude, 1e-6);
    }

    [Fact]
    public void XMP_vorhandene_Begleitdatei_ergaenzen_und_entfernen_behaelt_den_Rest()
    {
        var raw = Path.Combine(_dir, "IMG_2.cr2");
        File.WriteAllBytes(raw, SyntheticMediaFiles.Tiff("2024:07:12 15:30:12"));
        var sidecar = Path.Combine(_dir, "IMG_2.xmp"); // Lightroom-Schreibweise
        const string original = """
            <?xpacket begin="﻿" id="W5M0MpCehiHzreSzNTczkc9d"?>
            <x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
             <rdf:Description rdf:about="" xmlns:crs="http://ns.adobe.com/camera-raw-settings/1.0/" crs:Exposure2012="+0.35">
              <crs:ToneCurve><rdf:Seq><rdf:li>0, 0</rdf:li></rdf:Seq></crs:ToneCurve>
             </rdf:Description></rdf:RDF></x:xmpmeta>
            <?xpacket end="w"?>
            """;
        File.WriteAllText(sidecar, original, new UTF8Encoding(false));

        var write = GeoFileWriter.Write(raw, 0, Graz, true);

        write.WrittenPath.Should().Be(sidecar, "vorhandene Begleitdatei wird ergänzt, keine zweite angelegt");
        Encoding.UTF8.GetString(write.OldBytes!).Should().Be(original);
        var text = File.ReadAllText(sidecar);
        text.Should().Contain("crs:Exposure2012=\"+0.35\"").And.Contain("<rdf:li>0, 0</rdf:li>").And.Contain("xpacket end");
        XmpGpsSidecar.Read(sidecar)!.Value.Latitude.Should().BeApproximately(Graz.Latitude, 1e-6);
        File.Exists(raw + ".xmp").Should().BeFalse();

        GeoFileWriter.Write(raw, 0, null, true).Kind.Should().Be(GeoTagWriteKind.XmpSidecar);
        XmpGpsSidecar.Read(sidecar).Should().BeNull();
        File.ReadAllText(sidecar).Should().Contain("crs:Exposure2012");
    }

    [Fact]
    public void Entfernen_laesst_eine_Begleitdatei_ohne_GPS_unangetastet()
    {
        var raw = Path.Combine(_dir, "IMG_4.nef");
        File.WriteAllBytes(raw, SyntheticMediaFiles.Tiff("2024:07:12 15:30:12"));
        const string sidecar = "<x:xmpmeta xmlns:x='adobe:ns:meta/'><rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#'><rdf:Description rdf:about=''/></rdf:RDF></x:xmpmeta>";
        File.WriteAllText(raw + ".xmp", sidecar);

        GeoFileWriter.Write(raw, 0, null, true).Should().Be(GeoFileWrite.CatalogOnly);
        File.ReadAllText(raw + ".xmp").Should().Be(sidecar, "keine Umformatierung fremder Begleitdateien");
    }

    [Fact]
    public void Rueckgaengig_stellt_die_Aenderungszeit_mit_Bruchteilen_wieder_her()
    {
        var path = Path.Combine(_dir, "t.jpg");
        var jpeg = SyntheticMediaFiles.Jpeg(null);
        File.WriteAllBytes(path, jpeg);
        var old = new DateTime(2019, 3, 4, 5, 6, 7, DateTimeKind.Utc).AddTicks(1_234_567);
        File.SetLastWriteTimeUtc(path, old);

        var write = GeoFileWriter.Write(path, 0, Graz, keepModifiedTime: false);
        GeoFileWriter.Restore(write).Should().BeNull();

        File.ReadAllBytes(path).Should().Equal(jpeg);
        File.GetLastWriteTimeUtc(path).Should().Be(old);
    }

    [Fact]
    public void XMP_kaputte_Begleitdatei_wird_nicht_ueberschrieben()
    {
        var png = Path.Combine(_dir, "p.png");
        File.WriteAllBytes(png, SyntheticMediaFiles.Png(SyntheticMediaFiles.Tiff("2024:07:12 15:30:12")));
        File.WriteAllText(png + ".xmp", "<kein xml");
        FluentActions.Invoking(() => GeoFileWriter.Write(png, 0, Graz, true)).Should().Throw<GeoWriteException>();
        File.ReadAllText(png + ".xmp").Should().Be("<kein xml");
    }

    [Theory]
    [InlineData(48.2082, true, "48,12.492000N")]
    [InlineData(-33.8688, true, "33,52.128000S")]
    [InlineData(-70.6693, false, "70,40.158000W")]
    public void XMP_Schreibweise(double value, bool latitude, string expected)
        => XmpGpsSidecar.FormatCoordinate(value, latitude).Should().Be(expected);
}
