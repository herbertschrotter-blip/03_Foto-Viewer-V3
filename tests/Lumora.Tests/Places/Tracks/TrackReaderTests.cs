using System.Text;
using Lumora.Domain.Enums;
using Lumora.Services.Places.Tracks;
using static Lumora.Tests.Places.Tracks.TrackTestFiles;

namespace Lumora.Tests.Places.Tracks;

/// <summary>Spur-Kodierung (Rundreise, Größe) und die Leser für FIT, GPX und TCX (ADR-0062) — nur synthetische Dateien.</summary>
public sealed class TrackReaderTests
{
    private static readonly DateTime Start = new(2020, 3, 15, 12, 55, 0, DateTimeKind.Utc);

    [Fact]
    public void Kodierung_Rundreise_auf_Mikrograd_und_Dezimeter_genau()
    {
        var points = Enumerable.Range(0, 500)
            .Select(i => new TrackPoint(1_584_280_000 + i, 46.912345 + (i * 0.0000123), 15.0123456 - (i * 0.0000071), i % 7 == 0 ? null : 1853.27 + (i * 0.3)))
            .ToList();
        var decoded = TrackCodec.Decode(TrackCodec.Encode(points));
        decoded.Should().HaveCount(500);
        for (var i = 0; i < points.Count; i++)
        {
            decoded[i].Utc.Should().Be(points[i].Utc);
            decoded[i].Latitude.Should().BeApproximately(points[i].Latitude, 0.6e-6);
            decoded[i].Longitude.Should().BeApproximately(points[i].Longitude, 0.6e-6);
            if (points[i].Altitude is { } alt)
            {
                decoded[i].Altitude!.Value.Should().BeApproximately(alt, 0.051);
            }
        }

        var noAltitude = TrackCodec.Decode(TrackCodec.Encode([new TrackPoint(5, 1, 2)]));
        noAltitude.Single().Altitude.Should().BeNull();
        TrackCodec.Decode(TrackCodec.Encode([])).Should().BeEmpty();
        var act = () => TrackCodec.Decode([9, 1, 2]);
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Kodierung_ist_kompakt_und_die_Pruefsumme_stabil()
    {
        // Wanderung: 5 000 Punkte im Sekundentakt, ~1,4 m je Sekunde, Höhe ändert sich langsam
        var points = Enumerable.Range(0, 5000)
            .Select(i => new TrackPoint(1_584_280_000 + i, 46.9 + (i * 0.0000090), 15.0 + (i * 0.0000120) + (Math.Sin(i / 50.0) * 0.00002), 1000 + (i * 0.17)))
            .ToList();
        var bytes = TrackCodec.Encode(points);
        bytes.Length.Should().BeLessThan(40_000, "≈ 160 KB roh als double/long — kompakt je Aktivität statt Zeilen je Punkt");
        TrackCodec.Checksum(points).Should().Equal(TrackCodec.Checksum(TrackCodec.Decode(bytes)), "die Prüfsumme hängt nur an Zeit und Mikrograd");
        TrackCodec.Checksum(points).Should().NotEqual(TrackCodec.Checksum(points.Skip(1).ToList()));
    }

    [Fact]
    public void FIT_mit_komprimierten_Zeitstempeln_Hoehe_Sportart_und_Geraet()
    {
        var fit = FitHike(Start, 40, TimeSpan.FromSeconds(5), (46.95, 14.95), (46.96, 14.97));
        var track = FitTrackReader.Read(new MemoryStream(fit))!;

        track.Source.Should().Be(GpsTrackSource.Fit);
        track.Points.Should().HaveCount(40);
        track.Points[0].Utc.Should().Be(new DateTimeOffset(Start).ToUnixTimeSeconds());
        track.Points[^1].Utc.Should().Be(new DateTimeOffset(Start).ToUnixTimeSeconds() + (39 * 5), "auch komprimierte Zeitstempel zählen richtig");
        track.Points[0].Latitude.Should().BeApproximately(46.95, 1e-6);
        track.Points[^1].Longitude.Should().BeApproximately(14.97, 1e-6);
        track.Points[1].Altitude.Should().BeApproximately(1002, 0.2, "altitude (Skala 5, Versatz 500)");
        track.Points[2].Altitude.Should().BeApproximately(1004, 0.2, "enhanced_altitude");
        track.Sport.Should().Be("Wandern");
        track.Device.Should().Be("Fenix 7");
    }

    [Fact]
    public void FIT_mit_Big_Endian_Entwicklerfeldern_Produktnummer_und_ohne_Positionen()
    {
        var bigEndian = new FitBuilder()
            .Define(0, 0, false, [(1, 2, 0x84), (2, 2, 0x84)])
            .Data(0, Le((ushort)1, (ushort)2697))
            .Define(1, 20, true, [(253, 4, 0x86), (0, 4, 0x85), (1, 4, 0x85)], devFields: [(0, 3)]);
        foreach (var i in Enumerable.Range(0, 3))
        {
            var payload = new byte[15];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(payload, FitTime(Start.AddSeconds(i)));
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(4), Semicircles(47.0 + (i * 0.001)));
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(8), Semicircles(15.0));
            payload[12] = 0xAA; // Entwicklerfeld, 3 Byte, wird übersprungen
            bigEndian.Data(1, payload);
        }

        bigEndian.Define(2, 18, false, [(5, 1, 0)]).Data(2, [1]);
        var track = FitTrackReader.Read(bigEndian.Build())!;
        track.Points.Should().HaveCount(3);
        track.Points[2].Latitude.Should().BeApproximately(47.002, 1e-6);
        track.Device.Should().Be("Fenix 5", "bekannte Garmin-Produktnummer");
        track.Sport.Should().Be("Laufen");

        var indoor = new FitBuilder()
            .Define(0, 20, false, [(253, 4, 0x86), (7, 2, 0x84)])
            .Data(0, Le(FitTime(Start), (ushort)1234))
            .Build();
        FitTrackReader.Read(indoor).Should().BeNull("Laufband: keine Positionen");
        var act = () => FitTrackReader.Read(Encoding.ASCII.GetBytes("keine FIT-Datei, nur Text"));
        act.Should().Throw<InvalidDataException>();
        FitTrackReader.DeviceName(null, 1, 99999).Should().Be("Garmin-Gerät 99999");
    }

    [Fact]
    public void GPX_mehrere_Spuren_Namen_Typ_Hoehe_und_Punkte_ohne_Zeit()
    {
        var gpx = Gpx(
            ("Wanderung Weinebene", "hiking", [(Start, 46.95, 14.95, 1500), (Start.AddSeconds(10), 46.951, 14.951, 1502)]),
            ("Abfahrt", "cycling", [(Start.AddHours(2), 46.9, 14.9, null)]));
        gpx = gpx.Replace("</trkseg></trk></gpx>", "<trkpt lat=\"46.8\" lon=\"14.8\"><ele>3</ele></trkpt></trkseg></trk></gpx>", StringComparison.Ordinal);
        var tracks = XmlTrackReader.ReadGpx(new MemoryStream(Encoding.UTF8.GetBytes(gpx)));

        tracks.Should().HaveCount(2);
        tracks[0].Name.Should().Be("Wanderung Weinebene");
        tracks[0].Sport.Should().Be("Wandern");
        tracks[0].Points.Should().HaveCount(2);
        tracks[0].Points[1].Altitude.Should().Be(1502);
        tracks[0].Source.Should().Be(GpsTrackSource.Gpx);
        tracks[1].Sport.Should().Be("Radfahren");
        tracks[1].Points.Should().ContainSingle("ein Punkt ohne Zeit zählt nicht");
        XmlTrackReader.ParseTime("2020-03-15T13:56:10+01:00").Should().Be(new DateTimeOffset(2020, 3, 15, 12, 56, 10, TimeSpan.Zero).ToUnixTimeSeconds());
    }

    [Fact]
    public void TCX_mit_Sportart_und_Geraet_und_ohne_DTD()
    {
        const string tcx = """
            <?xml version="1.0"?>
            <TrainingCenterDatabase xmlns="http://www.garmin.com/xmlschemas/TrainingCenterDatabase/v2">
              <Activities><Activity Sport="Biking"><Id>2020-03-15T12:55:00Z</Id>
                <Lap><Track>
                  <Trackpoint><Time>2020-03-15T12:55:00Z</Time><Position><LatitudeDegrees>47.0</LatitudeDegrees><LongitudeDegrees>15.0</LongitudeDegrees></Position><AltitudeMeters>400</AltitudeMeters></Trackpoint>
                  <Trackpoint><Time>2020-03-15T12:55:05Z</Time><HeartRateBpm><Value>120</Value></HeartRateBpm></Trackpoint>
                  <Trackpoint><Time>2020-03-15T12:55:10Z</Time><Position><LatitudeDegrees>47.001</LatitudeDegrees><LongitudeDegrees>15.002</LongitudeDegrees></Position></Trackpoint>
                </Track></Lap>
                <Creator><Name>Edge 530</Name></Creator>
              </Activity></Activities>
            </TrainingCenterDatabase>
            """;
        var track = XmlTrackReader.ReadTcx(new MemoryStream(Encoding.UTF8.GetBytes(tcx))).Single();
        track.Points.Should().HaveCount(2, "Punkte ohne Position zählen nicht");
        track.Sport.Should().Be("Radfahren");
        track.Device.Should().Be("Edge 530");
        track.Source.Should().Be(GpsTrackSource.Tcx);

        const string evil = """<?xml version="1.0"?><!DOCTYPE gpx [<!ENTITY x "y">]><gpx><trk><trkseg><trkpt lat="1" lon="1"><time>&x;</time></trkpt></trkseg></trk></gpx>""";
        var act = () => XmlTrackReader.ReadGpx(new MemoryStream(Encoding.UTF8.GetBytes(evil)));
        act.Should().Throw<System.Xml.XmlException>("keine DTD-Verarbeitung");
    }
}
