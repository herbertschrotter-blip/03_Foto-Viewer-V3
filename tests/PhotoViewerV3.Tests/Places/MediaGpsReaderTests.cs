using PhotoViewerV3.Domain.Enums;
using PhotoViewerV3.Services.Metadata;
using PhotoViewerV3.Tests.Metadata;

namespace PhotoViewerV3.Tests.Places;

/// <summary>GPS lesen je Format (ADR-0050): EXIF in JPEG/TIFF/PNG/WebP/HEIC, Videos (©xyz, Apple ISO 6709), XMP-Begleitdatei.</summary>
public sealed class MediaGpsReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pv3-gps-" + Guid.NewGuid().ToString("N"));

    public MediaGpsReaderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static void ShouldBe(GeoReading? reading, double lat, double lon, GeoSource source = GeoSource.Exif)
    {
        reading.Should().NotBeNull();
        reading!.Value.Source.Should().Be(source);
        reading.Value.Coordinate.Latitude.Should().BeApproximately(lat, 1e-6);
        reading.Value.Coordinate.Longitude.Should().BeApproximately(lon, 1e-6);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void JPEG_beide_Byte_Reihenfolgen(bool bigEndian)
        => ShouldBe(MediaGpsReader.Read(new MemoryStream(SyntheticMediaFiles.Jpeg(SyntheticGpsFiles.Tiff(48.2082, 16.3738, bigEndian)))), 48.2082, 16.3738);

    [Fact]
    public void Sued_und_West_sind_negativ_Hoehe_unter_Null()
    {
        var reading = MediaGpsReader.Read(new MemoryStream(SyntheticMediaFiles.Jpeg(SyntheticGpsFiles.Tiff(-33.8688, -70.6693, altitude: -12.5))));
        ShouldBe(reading, -33.8688, -70.6693);
        reading!.Value.Coordinate.Altitude.Should().BeApproximately(-12.5, 1e-9);
    }

    [Fact]
    public void TIFF_RAW_PNG_WebP_HEIC()
    {
        var tiff = SyntheticGpsFiles.Tiff(41.9028, 12.4964);
        ShouldBe(MediaGpsReader.Read(new MemoryStream(tiff)), 41.9028, 12.4964);
        ShouldBe(MediaGpsReader.Read(new MemoryStream(SyntheticMediaFiles.Png(tiff))), 41.9028, 12.4964);
        ShouldBe(MediaGpsReader.Read(new MemoryStream(SyntheticMediaFiles.WebP(tiff, exifPrefix: true))), 41.9028, 12.4964);
        ShouldBe(MediaGpsReader.Read(new MemoryStream(SyntheticMediaFiles.Heic(tiff))), 41.9028, 12.4964);
    }

    [Fact]
    public void Ohne_GPS_oder_Null_Island_gibt_null()
    {
        MediaGpsReader.Read(new MemoryStream(SyntheticMediaFiles.Jpeg(SyntheticMediaFiles.Tiff("2024:07:12 15:30:12")))).Should().BeNull();
        MediaGpsReader.Read(new MemoryStream(SyntheticMediaFiles.Jpeg(null))).Should().BeNull();
        MediaGpsReader.Read(new MemoryStream(SyntheticMediaFiles.Jpeg(SyntheticGpsFiles.Tiff(0, 0)))).Should().BeNull("0/0 schreiben Kameras ohne Empfang");
        MediaGpsReader.Read(new MemoryStream(new byte[100])).Should().BeNull();
    }

    [Fact]
    public void Video_xyz_und_Apple_ISO6709()
    {
        ShouldBe(MediaGpsReader.Read(new MemoryStream(SyntheticGpsFiles.Mp4WithXyz("+48.2082+016.3738/"))), 48.2082, 16.3738, GeoSource.Video);
        ShouldBe(MediaGpsReader.Read(new MemoryStream(SyntheticGpsFiles.MovWithAppleLocation("+37.3349-122.0090+012.345/"))), 37.3349, -122.0090, GeoSource.Video);
        MediaGpsReader.Read(new MemoryStream(SyntheticMediaFiles.Mp4(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)))).Should().BeNull();
    }

    [Theory]
    [InlineData("+48.2082+016.3738/", 48.2082, 16.3738)]
    [InlineData("-33.8688+151.2093+058.0/", -33.8688, 151.2093)]
    [InlineData("+4812.492+01622.428/", 48.2082, 16.3738)]
    [InlineData("+481229.52+0162225.68/", 48.2082, 16.3738)]
    [InlineData("+48.2082+016.3738CRSWGS_84/", 48.2082, 16.3738)]
    public void ISO6709_Varianten(string text, double lat, double lon)
    {
        var c = MediaGpsReader.ParseIso6709(text);
        c.Should().NotBeNull();
        c!.Value.Latitude.Should().BeApproximately(lat, 1e-6);
        c.Value.Longitude.Should().BeApproximately(lon, 1e-6);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Wien")]
    [InlineData("+95.0+016.0/")]
    public void ISO6709_Ungueltiges(string text) => MediaGpsReader.ParseIso6709(text).Should().BeNull();

    [Fact]
    public void XMP_Begleitdatei_gewinnt_vor_EXIF_auch_in_Lightroom_Schreibweise()
    {
        var raw = Path.Combine(_dir, "IMG_1.dng");
        File.WriteAllBytes(raw, SyntheticGpsFiles.Tiff(41.9028, 12.4964));
        var reader = new GeoLocationReader();
        ShouldBe(reader.Read(raw), 41.9028, 12.4964);

        File.WriteAllText(Path.Combine(_dir, "IMG_1.xmp"), """
            <x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
              <rdf:Description rdf:about="" xmlns:exif="http://ns.adobe.com/exif/1.0/">
                <exif:GPSLatitude>48,12.492N</exif:GPSLatitude>
                <exif:GPSLongitude>16,22.428E</exif:GPSLongitude>
              </rdf:Description></rdf:RDF></x:xmpmeta>
            """);
        ShouldBe(reader.Read(raw), 48.2082, 16.3738);

        File.WriteAllText(raw + ".xmp", """
            <x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
              <rdf:Description rdf:about="" xmlns:exif="http://ns.adobe.com/exif/1.0/" exif:GPSLatitude="47,4.242S" exif:GPSLongitude="15,26.37W"/>
            </rdf:RDF></x:xmpmeta>
            """);
        ShouldBe(reader.Read(raw), -47.0707, -15.4395); // name.ext.xmp vor name.xmp
    }

    [Fact]
    public void JPEG_ignoriert_die_Lightroom_Begleitdatei_des_RAW_Paars()
    {
        var jpeg = Path.Combine(_dir, "IMG_3.jpg");
        File.WriteAllBytes(jpeg, SyntheticMediaFiles.Jpeg(SyntheticGpsFiles.Tiff(48.2082, 16.3738)));
        File.WriteAllText(Path.Combine(_dir, "IMG_3.xmp"), """
            <x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
              <rdf:Description rdf:about="" xmlns:exif="http://ns.adobe.com/exif/1.0/" exif:GPSLatitude="1,0N" exif:GPSLongitude="2,0E"/>
            </rdf:RDF></x:xmpmeta>
            """);

        ShouldBe(new GeoLocationReader().Read(jpeg), 48.2082, 16.3738);
        XmpGpsSidecar.FindExisting(jpeg).Should().BeNull("IMG_3.xmp gehört zum RAW");
    }

    [Theory]
    [InlineData("48,12.492N", 48.2082)]
    [InlineData("48,12,29.52N", 48.2082)]
    [InlineData("16,22.428W", -16.3738)]
    [InlineData("48.2082", 48.2082)]
    public void XMP_Koordinaten_Schreibweisen(string text, double expected)
        => XmpGpsSidecar.ParseCoordinate(text)!.Value.Should().BeApproximately(expected, 1e-6);
}
