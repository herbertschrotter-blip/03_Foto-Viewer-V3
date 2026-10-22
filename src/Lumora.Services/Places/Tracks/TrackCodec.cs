using System.IO.Compression;
using System.Security.Cryptography;

namespace Lumora.Services.Places.Tracks;

/// <summary>Ein Punkt einer GPS-Spur: Zeit (Unix-Sekunden UTC), WGS84-Dezimalgrad, optional Höhe in Metern.</summary>
public readonly record struct TrackPoint(long Utc, double Latitude, double Longitude, double? Altitude = null);

/// <summary>
/// Kompakte Speicherung der Spurpunkte (ADR-0062) als BLOB statt einer Zeile je Punkt: Version (1 Byte), Flags (1 Byte,
/// Bit 0 = mit Höhe), Punktzahl, dann je Punkt die Differenz zum Vorgänger als ZigZag-Varint — Zeit in Sekunden,
/// Breite/Länge in Mikrograd (1e-6° ≈ 0,11 m), Höhe in Dezimetern — und das Ganze mit Deflate komprimiert. Eine Wanderung
/// mit 5 000 Punkten (1 s Abstand) braucht so etwa 10–20 KB statt ~160 KB roh. Rundreise auf 1e-6° bzw. 0,1 m genau.
/// </summary>
public static class TrackCodec
{
    public const byte Version = 1;
    public const double CoordinateScale = 1_000_000;
    public const double AltitudeScale = 10;

    /// <summary>Höchstzahl Punkte je Spur (Schutz gegen kaputte Dateien; 24 h im Sekundentakt ≈ 86 400).</summary>
    public const int MaxPoints = 500_000;

    /// <summary>Kodiert die Punkte (nach Zeit sortiert erwartet).</summary>
    public static byte[] Encode(IReadOnlyList<TrackPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        var hasAltitude = points.Any(p => p.Altitude is not null);
        using var raw = new MemoryStream(points.Count * 6 + 16);
        raw.WriteByte(Version);
        raw.WriteByte((byte)(hasAltitude ? 1 : 0));
        WriteVarint(raw, (ulong)points.Count);
        long prevT = 0, prevLat = 0, prevLon = 0, prevAlt = 0;
        foreach (var p in points)
        {
            var lat = (long)Math.Round(p.Latitude * CoordinateScale);
            var lon = (long)Math.Round(p.Longitude * CoordinateScale);
            WriteSigned(raw, p.Utc - prevT);
            WriteSigned(raw, lat - prevLat);
            WriteSigned(raw, lon - prevLon);
            prevT = p.Utc;
            prevLat = lat;
            prevLon = lon;
            if (hasAltitude)
            {
                // fehlende Höhe = Höhe des Vorgängers (Sprünge auf 0 würden die Kodierung aufblähen)
                var alt = p.Altitude is { } a ? (long)Math.Round(a * AltitudeScale) : prevAlt;
                WriteSigned(raw, alt - prevAlt);
                prevAlt = alt;
            }
        }

        using var packed = new MemoryStream((int)(raw.Length / 2) + 16);
        packed.WriteByte(Version);
        using (var deflate = new DeflateStream(packed, CompressionLevel.Optimal, leaveOpen: true))
        {
            raw.Position = 1; // Version steht schon unkomprimiert vorn
            raw.CopyTo(deflate);
        }

        return packed.ToArray();
    }

    /// <summary>Dekodiert die Punkte; wirft <see cref="InvalidDataException"/> bei unbekannter Version oder Kaputtem.</summary>
    public static TrackPoint[] Decode(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < 2 || data[0] != Version)
        {
            throw new InvalidDataException("Unbekanntes Spurformat.");
        }

        using var input = new MemoryStream(data, 1, data.Length - 1, writable: false);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        deflate.CopyTo(raw);
        raw.Position = 0;
        var flags = raw.ReadByte();
        if (flags < 0)
        {
            throw new InvalidDataException("Spur abgeschnitten.");
        }

        var hasAltitude = (flags & 1) != 0;
        var count = ReadVarint(raw);
        if (count > MaxPoints)
        {
            throw new InvalidDataException("Zu viele Spurpunkte.");
        }

        var points = new TrackPoint[(int)count];
        long t = 0, lat = 0, lon = 0, alt = 0;
        for (var i = 0; i < points.Length; i++)
        {
            t += ReadSigned(raw);
            lat += ReadSigned(raw);
            lon += ReadSigned(raw);
            double? altitude = null;
            if (hasAltitude)
            {
                alt += ReadSigned(raw);
                altitude = alt / AltitudeScale;
            }

            points[i] = new TrackPoint(t, lat / CoordinateScale, lon / CoordinateScale, altitude);
        }

        return points;
    }

    /// <summary>SHA-256 der kodierten Punkte ohne Kompression-Abhängigkeit: über die Rohwerte (Zeit, Mikrograd).</summary>
    public static byte[] Checksum(IReadOnlyList<TrackPoint> points)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> buffer = stackalloc byte[24];
        foreach (var p in points)
        {
            BitConverter.TryWriteBytes(buffer[..8], p.Utc);
            BitConverter.TryWriteBytes(buffer[8..16], (long)Math.Round(p.Latitude * CoordinateScale));
            BitConverter.TryWriteBytes(buffer[16..24], (long)Math.Round(p.Longitude * CoordinateScale));
            hash.AppendData(buffer);
        }

        return hash.GetHashAndReset();
    }

    private static void WriteSigned(Stream s, long value) => WriteVarint(s, (ulong)((value << 1) ^ (value >> 63)));

    private static long ReadSigned(Stream s)
    {
        var v = ReadVarint(s);
        return (long)(v >> 1) ^ -(long)(v & 1);
    }

    private static void WriteVarint(Stream s, ulong value)
    {
        while (value >= 0x80)
        {
            s.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }

        s.WriteByte((byte)value);
    }

    private static ulong ReadVarint(Stream s)
    {
        ulong result = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            var b = s.ReadByte();
            if (b < 0)
            {
                throw new InvalidDataException("Spur abgeschnitten.");
            }

            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }
        }

        throw new InvalidDataException("Ungültige Zahl in der Spur.");
    }
}
