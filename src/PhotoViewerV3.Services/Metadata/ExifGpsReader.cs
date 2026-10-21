using System.Buffers.Binary;

namespace PhotoViewerV3.Services.Metadata;

/// <summary>
/// Liest die GPS-Koordinate aus einem TIFF-Block (ADR-0050): IFD0 → GPS-IFD (Tag 0x8825) → <c>GPSLatitudeRef</c> (1),
/// <c>GPSLatitude</c> (2), <c>GPSLongitudeRef</c> (3), <c>GPSLongitude</c> (4), optional <c>GPSAltitudeRef</c> (5) und
/// <c>GPSAltitude</c> (6). Breite/Länge als drei RATIONAL (Grad, Minuten, Sekunden; auch SRATIONAL). Arbeitet direkt
/// auf dem Stream (Seek + kleine Reads) wie <see cref="ExifTiffReader"/>; Fehlerhaftes ergibt null, nie eine Ausnahme.
/// </summary>
public static class ExifGpsReader
{
    internal const ushort TagGpsIfd = 0x8825;
    private const ushort TagLatitudeRef = 1;
    private const ushort TagLatitude = 2;
    private const ushort TagLongitudeRef = 3;
    private const ushort TagLongitude = 4;
    private const ushort TagAltitudeRef = 5;
    private const ushort TagAltitude = 6;
    private const ushort TypeByte = 1;
    private const ushort TypeAscii = 2;
    private const ushort TypeRational = 5;
    private const ushort TypeSRational = 10;
    private const int MaxEntries = 1024;

    private readonly record struct Entry(ushort Tag, ushort Type, uint Count, uint ValueOrOffset, long ValueFieldPosition);

    /// <summary>Koordinate aus dem TIFF-Block an <paramref name="tiffStart"/> (Quelle EXIF) oder null.</summary>
    public static GeoReading? Read(Stream stream, long tiffStart)
        => ReadCoordinate(stream, tiffStart) is { } c ? new GeoReading(c, Domain.Enums.GeoSource.Exif) : null;

    /// <summary>Koordinate aus dem TIFF-Block an <paramref name="tiffStart"/> oder null.</summary>
    public static GeoCoordinate? ReadCoordinate(Stream stream, long tiffStart)
    {
        try
        {
            Span<byte> header = stackalloc byte[8];
            if (!ReadAt(stream, tiffStart, header))
            {
                return null;
            }

            bool little;
            if (header[0] == (byte)'I' && header[1] == (byte)'I')
            {
                little = true;
            }
            else if (header[0] == (byte)'M' && header[1] == (byte)'M')
            {
                little = false;
            }
            else
            {
                return null;
            }

            var ifd0 = U32(header[4..], little);
            var ifd0Entries = ReadIfd(stream, tiffStart, ifd0, little);
            if (ifd0Entries is null || !ifd0Entries.TryGetValue(TagGpsIfd, out var gpsPointer))
            {
                return null;
            }

            var gps = ReadIfd(stream, tiffStart, gpsPointer.ValueOrOffset, little);
            if (gps is null
                || !gps.TryGetValue(TagLatitude, out var latEntry)
                || !gps.TryGetValue(TagLongitude, out var lonEntry))
            {
                return null;
            }

            var lat = ReadDms(stream, tiffStart, latEntry, little);
            var lon = ReadDms(stream, tiffStart, lonEntry, little);
            if (lat is null || lon is null)
            {
                return null;
            }

            var latRef = gps.TryGetValue(TagLatitudeRef, out var lr) ? RefChar(lr, little) : 'N';
            var lonRef = gps.TryGetValue(TagLongitudeRef, out var lo) ? RefChar(lo, little) : 'E';
            var latitude = latRef is 'S' or 's' ? -lat.Value : lat.Value;
            var longitude = lonRef is 'W' or 'w' ? -lon.Value : lon.Value;

            double? altitude = null;
            if (gps.TryGetValue(TagAltitude, out var altEntry) && ReadRationals(stream, tiffStart, altEntry, little, 1) is { } alt)
            {
                var below = gps.TryGetValue(TagAltitudeRef, out var altRef) && altRef.Type == TypeByte && (altRef.ValueOrOffset & (little ? 0xFFu : 0xFF000000u)) != 0;
                altitude = below ? -alt[0] : alt[0];
            }

            var coordinate = new GeoCoordinate(latitude, longitude, altitude);
            return coordinate.IsValid ? coordinate : null;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>Grad + Minuten/60 + Sekunden/3600 aus drei (S)RATIONAL; null bei Nenner 0 oder unplausiblen Werten.</summary>
    private static double? ReadDms(Stream stream, long tiffStart, Entry entry, bool little)
    {
        if (ReadRationals(stream, tiffStart, entry, little, 3) is not { } v)
        {
            return null;
        }

        if (v[0] < 0 || v[1] < 0 || v[1] >= 60.0001 || v[2] < 0 || v[2] >= 60.0001)
        {
            return null;
        }

        return v[0] + (v[1] / 60.0) + (v[2] / 3600.0);
    }

    private static double[]? ReadRationals(Stream stream, long tiffStart, Entry entry, bool little, int count)
    {
        if (entry.Type is not (TypeRational or TypeSRational) || entry.Count < count)
        {
            return null;
        }

        var bytes = new byte[count * 8];
        if (!ReadAt(stream, tiffStart + entry.ValueOrOffset, bytes))
        {
            return null;
        }

        var result = new double[count];
        for (var i = 0; i < count; i++)
        {
            var span = bytes.AsSpan(i * 8, 8);
            double numerator, denominator;
            if (entry.Type == TypeSRational)
            {
                numerator = (int)U32(span, little);
                denominator = (int)U32(span[4..], little);
            }
            else
            {
                numerator = U32(span, little);
                denominator = U32(span[4..], little);
            }

            if (denominator == 0)
            {
                return null;
            }

            result[i] = numerator / denominator;
        }

        return result;
    }

    /// <summary>Erstes Zeichen eines ASCII-Werts (Referenz „N“/„S“/„E“/„W“, steht inline im Wertfeld).</summary>
    private static char RefChar(Entry entry, bool little)
    {
        if (entry.Type != TypeAscii || entry.Count == 0)
        {
            return '\0';
        }

        Span<byte> raw = stackalloc byte[4];
        if (little)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(raw, entry.ValueOrOffset);
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(raw, entry.ValueOrOffset);
        }

        return (char)raw[0];
    }

    private static Dictionary<ushort, Entry>? ReadIfd(Stream stream, long tiffStart, uint ifdOffset, bool little)
    {
        Span<byte> countBytes = stackalloc byte[2];
        if (ifdOffset < 8 || !ReadAt(stream, tiffStart + ifdOffset, countBytes))
        {
            return null;
        }

        var count = U16(countBytes, little);
        if (count == 0 || count > MaxEntries)
        {
            return null;
        }

        var entries = new byte[count * 12];
        if (!ReadAt(stream, tiffStart + ifdOffset + 2, entries))
        {
            return null;
        }

        var result = new Dictionary<ushort, Entry>(count);
        for (var i = 0; i < count; i++)
        {
            var e = entries.AsSpan(i * 12, 12);
            var tag = U16(e, little);
            result.TryAdd(tag, new Entry(tag, U16(e[2..], little), U32(e[4..], little), U32(e[8..], little), tiffStart + ifdOffset + 2 + (i * 12) + 8));
        }

        return result;
    }

    private static bool ReadAt(Stream stream, long position, Span<byte> buffer)
    {
        if (position < 0 || position + buffer.Length > stream.Length)
        {
            return false;
        }

        stream.Position = position;
        stream.ReadExactly(buffer);
        return true;
    }

    private static ushort U16(ReadOnlySpan<byte> b, bool little)
        => little ? BinaryPrimitives.ReadUInt16LittleEndian(b) : BinaryPrimitives.ReadUInt16BigEndian(b);

    private static uint U32(ReadOnlySpan<byte> b, bool little)
        => little ? BinaryPrimitives.ReadUInt32LittleEndian(b) : BinaryPrimitives.ReadUInt32BigEndian(b);
}
