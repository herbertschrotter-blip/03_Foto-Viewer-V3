using System.Buffers.Binary;

namespace PhotoViewerV3.Services.Metadata;

/// <summary>Lage des EXIF-Segments in einem JPEG (ADR-0050).</summary>
/// <param name="Offset">Byte-Position des vorhandenen APP1-„Exif“-Segments bzw. die Einfügestelle für ein neues.</param>
/// <param name="Length">Länge des vorhandenen Segments inkl. Marker (0 = keines vorhanden).</param>
public readonly record struct JpegExifLocation(long Offset, int Length)
{
    public bool Exists => Length > 0;
}

/// <summary>
/// GPS im EXIF eines JPEG verlustfrei setzen bzw. entfernen (ADR-0050). Geändert wird ausschließlich das
/// APP1-„Exif“-Segment; alle übrigen Bytes (andere Segmente, Bilddaten ab SOS) bleiben unverändert. Im Segment selbst
/// bleibt der alte TIFF-Block Byte für Byte stehen (MakerNotes mit absoluten Versätzen bleiben gültig): neue Daten werden
/// angehängt — ein neues GPS-Verzeichnis und, falls IFD0 noch keinen GPS-Zeiger (0x8825) hat, eine Kopie von IFD0 mit
/// diesem Zeiger; danach werden höchstens 4 Bytes umgestellt (Zeiger auf IFD0 im TIFF-Kopf bzw. der GPS-Zeiger). Die
/// alten Verzeichnisse bleiben als ungenutzte Bytes im Segment (wenige hundert Byte je Änderung).
/// </summary>
public static class JpegExifGpsWriter
{
    /// <summary>Größte Segmentlänge laut JPEG (Längenfeld 16 Bit, ohne Marker).</summary>
    public const int MaxSegmentPayload = 65_535;

    private const int MaxSegments = 256;
    private const ushort TypeByte = 1;
    private const ushort TypeAscii = 2;
    private const ushort TypeLong = 4;
    private const ushort TypeRational = 5;
    private static readonly byte[] ExifIdent = "Exif\0\0"u8.ToArray();

    /// <summary>
    /// Sucht das erste APP1-„Exif“-Segment vor den Bilddaten. Ohne Segment: Einfügestelle direkt nach SOI bzw. nach einem
    /// unmittelbar folgenden APP0 (JFIF). Null, wenn die Datei kein lesbares JPEG ist.
    /// </summary>
    public static JpegExifLocation? Locate(Stream stream)
    {
        Span<byte> marker = stackalloc byte[4];
        Span<byte> ident = stackalloc byte[6];
        if (stream.Length < 4 || !ReadAt(stream, 0, marker[..2]) || marker[0] != 0xFF || marker[1] != 0xD8)
        {
            return null;
        }

        long position = 2;
        long insertAt = 2;
        var first = true;
        for (var i = 0; i < MaxSegments; i++)
        {
            if (!ReadAt(stream, position, marker) || marker[0] != 0xFF)
            {
                return null;
            }

            var code = marker[1];
            if (code == 0xFF)
            {
                position++;
                continue;
            }

            if (code is 0xDA or 0xD9)
            {
                return new JpegExifLocation(insertAt, 0); // Bilddaten erreicht, kein EXIF
            }

            if (code is 0x01 or (>= 0xD0 and <= 0xD7))
            {
                position += 2;
                continue;
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(marker[2..]);
            if (length < 2)
            {
                return null;
            }

            if (code == 0xE1 && length >= 8 && ReadAt(stream, position + 4, ident) && ident.SequenceEqual(ExifIdent))
            {
                return new JpegExifLocation(position, length + 2);
            }

            if (first && code == 0xE0)
            {
                insertAt = position + 2 + length; // nach APP0 (JFIF) einfügen
            }

            first = false;
            position += 2 + length;
        }

        return null;
    }

    /// <summary>
    /// Neues Segment (inkl. Marker FF E1 und Länge) mit gesetzter Koordinate (<paramref name="value"/>) bzw. ohne GPS
    /// (<paramref name="value"/> null). <paramref name="oldSegment"/> null = es gibt noch kein EXIF-Segment. Liefert null,
    /// wenn nichts zu ändern ist (Entfernen ohne GPS). Wirft <see cref="InvalidDataException"/> bei unlesbarem EXIF oder
    /// wenn das Segment die JPEG-Grenze von 64 KB überschritte.
    /// </summary>
    public static byte[]? BuildSegment(byte[]? oldSegment, GeoCoordinate? value)
    {
        if (oldSegment is null)
        {
            return value is { } v ? Wrap(NewTiff(v)) : null;
        }

        if (oldSegment.Length < 10 + 8 || oldSegment[0] != 0xFF || oldSegment[1] != 0xE1 || !oldSegment.AsSpan(4, 6).SequenceEqual(ExifIdent))
        {
            throw new InvalidDataException("Kein EXIF-Segment.");
        }

        var tiff = oldSegment.AsSpan(10).ToArray();
        bool little;
        if (tiff[0] == 'I' && tiff[1] == 'I')
        {
            little = true;
        }
        else if (tiff[0] == 'M' && tiff[1] == 'M')
        {
            little = false;
        }
        else
        {
            throw new InvalidDataException("EXIF: unbekannte Byte-Reihenfolge.");
        }

        var ifd0Offset = U32(tiff.AsSpan(4), little);
        if (ifd0Offset < 8 || ifd0Offset + 2 > tiff.Length)
        {
            throw new InvalidDataException("EXIF: IFD0 außerhalb des Blocks.");
        }

        var count = U16(tiff.AsSpan((int)ifd0Offset), little);
        var entriesEnd = (long)ifd0Offset + 2 + (count * 12L);
        if (count == 0 || entriesEnd + 4 > tiff.Length)
        {
            throw new InvalidDataException("EXIF: IFD0 abgeschnitten.");
        }

        var entries = new List<byte[]>(count + 1);
        var gpsIndex = -1;
        for (var i = 0; i < count; i++)
        {
            var entry = tiff.AsSpan((int)ifd0Offset + 2 + (i * 12), 12).ToArray();
            if (U16(entry, little) == ExifGpsReader.TagGpsIfd)
            {
                gpsIndex = i;
            }

            entries.Add(entry);
        }

        var nextIfd = tiff.AsSpan((int)entriesEnd, 4).ToArray();
        using var output = new MemoryStream(tiff.Length + 256);
        output.Write(tiff);
        Pad(output);

        if (value is null)
        {
            if (gpsIndex < 0)
            {
                return null; // kein GPS — nichts zu entfernen
            }

            entries.RemoveAt(gpsIndex);
            var newIfd0 = (uint)output.Position;
            WriteIfd(output, entries, nextIfd, little);
            var bytes = output.ToArray();
            W32(bytes.AsSpan(4), newIfd0, little);
            return Wrap(bytes);
        }

        if (gpsIndex >= 0)
        {
            // GPS-Zeiger vorhanden: neues GPS-Verzeichnis anhängen, Zeiger in IFD0 umstellen (4 Bytes).
            var gpsOffset = (uint)output.Position;
            WriteGpsIfd(output, value.Value, little);
            var bytes = output.ToArray();
            W32(bytes.AsSpan((int)ifd0Offset + 2 + (gpsIndex * 12) + 8), gpsOffset, little);
            return Wrap(bytes);
        }
        else
        {
            // Kein GPS-Zeiger: IFD0 mit zusätzlichem Eintrag (nach Tag sortiert) anhängen, Kopf auf die Kopie zeigen lassen.
            var newIfd0 = (uint)output.Position;
            var gpsOffset = newIfd0 + 2 + ((uint)(entries.Count + 1) * 12) + 4;
            var gpsEntry = new byte[12];
            W16(gpsEntry, ExifGpsReader.TagGpsIfd, little);
            W16(gpsEntry.AsSpan(2), TypeLong, little);
            W32(gpsEntry.AsSpan(4), 1, little);
            W32(gpsEntry.AsSpan(8), gpsOffset, little);
            var insertAt = entries.FindIndex(e => U16(e, little) > ExifGpsReader.TagGpsIfd);
            entries.Insert(insertAt < 0 ? entries.Count : insertAt, gpsEntry);
            WriteIfd(output, entries, nextIfd, little);
            WriteGpsIfd(output, value.Value, little);
            var bytes = output.ToArray();
            W32(bytes.AsSpan(4), newIfd0, little);
            return Wrap(bytes);
        }
    }

    /// <summary>Neuer TIFF-Block (Big Endian) nur mit IFD0 → GPS-Verzeichnis.</summary>
    private static byte[] NewTiff(GeoCoordinate value)
    {
        const bool little = false;
        using var output = new MemoryStream(160);
        output.Write("MM\0*"u8);
        var header = new byte[4];
        W32(header, 8, little);
        output.Write(header);
        var gpsOffset = 8u + 2 + 12 + 4;
        var gpsEntry = new byte[12];
        W16(gpsEntry, ExifGpsReader.TagGpsIfd, little);
        W16(gpsEntry.AsSpan(2), TypeLong, little);
        W32(gpsEntry.AsSpan(4), 1, little);
        W32(gpsEntry.AsSpan(8), gpsOffset, little);
        WriteIfd(output, [gpsEntry], new byte[4], little);
        WriteGpsIfd(output, value, little);
        return output.ToArray();
    }

    /// <summary>
    /// GPS-Verzeichnis mit Version 2.3, Breite und Länge (Grad, Minuten, Sekunden in Tausendsteln) samt Referenz; die
    /// beiden RATIONAL-Werte folgen direkt dahinter. Versätze relativ zum TIFF-Anfang = Stream-Position.
    /// </summary>
    private static void WriteGpsIfd(MemoryStream output, GeoCoordinate value, bool little)
    {
        var start = (uint)output.Position;
        const int entryCount = 5;
        var dataStart = start + 2 + (entryCount * 12) + 4;
        var entries = new List<byte[]>
        {
            Entry(0, TypeByte, 4, [2, 3, 0, 0], little),
            Entry(1, TypeAscii, 2, [(byte)(value.Latitude < 0 ? 'S' : 'N'), 0, 0, 0], little),
            EntryOffset(2, TypeRational, 3, dataStart, little),
            Entry(3, TypeAscii, 2, [(byte)(value.Longitude < 0 ? 'W' : 'E'), 0, 0, 0], little),
            EntryOffset(4, TypeRational, 3, dataStart + 24, little),
        };
        WriteIfd(output, entries, new byte[4], little);
        WriteDms(output, Math.Abs(value.Latitude), little);
        WriteDms(output, Math.Abs(value.Longitude), little);
    }

    private static void WriteDms(MemoryStream output, double degrees, bool little)
    {
        var milliArcSeconds = (long)Math.Round(degrees * 3_600_000.0);
        var d = (uint)(milliArcSeconds / 3_600_000);
        var m = (uint)(milliArcSeconds % 3_600_000 / 60_000);
        var s = (uint)(milliArcSeconds % 60_000);
        Span<byte> buffer = stackalloc byte[24];
        W32(buffer, d, little);
        W32(buffer[4..], 1, little);
        W32(buffer[8..], m, little);
        W32(buffer[12..], 1, little);
        W32(buffer[16..], s, little);
        W32(buffer[20..], 1000, little);
        output.Write(buffer);
    }

    private static byte[] Entry(ushort tag, ushort type, uint count, byte[] inlineValue, bool little)
    {
        var entry = new byte[12];
        W16(entry, tag, little);
        W16(entry.AsSpan(2), type, little);
        W32(entry.AsSpan(4), count, little);
        inlineValue.AsSpan(0, 4).CopyTo(entry.AsSpan(8)); // Inline-Werte stehen linksbündig in Byte-Reihenfolge
        return entry;
    }

    private static byte[] EntryOffset(ushort tag, ushort type, uint count, uint offset, bool little)
    {
        var entry = new byte[12];
        W16(entry, tag, little);
        W16(entry.AsSpan(2), type, little);
        W32(entry.AsSpan(4), count, little);
        W32(entry.AsSpan(8), offset, little);
        return entry;
    }

    /// <summary>Verzeichnis: Anzahl (2 Byte), Einträge (je 12 Byte, schon in der Byte-Reihenfolge des Blocks), Zeiger auf das nächste.</summary>
    private static void WriteIfd(MemoryStream output, IReadOnlyList<byte[]> entries, byte[] nextIfd, bool little)
    {
        Span<byte> count = stackalloc byte[2];
        W16(count, (ushort)entries.Count, little);
        output.Write(count);
        foreach (var entry in entries)
        {
            output.Write(entry);
        }

        output.Write(nextIfd);
    }

    private static void Pad(MemoryStream output)
    {
        if (output.Position % 2 == 1)
        {
            output.WriteByte(0);
        }
    }

    /// <summary>FF E1 + Länge + „Exif\0\0“ + TIFF; wirft, wenn das Segment zu groß würde.</summary>
    private static byte[] Wrap(byte[] tiff)
    {
        var payload = 2 + ExifIdent.Length + tiff.Length;
        if (payload > MaxSegmentPayload)
        {
            throw new InvalidDataException("Der EXIF-Block würde größer als 64 KB (JPEG-Grenze).");
        }

        var segment = new byte[2 + payload];
        segment[0] = 0xFF;
        segment[1] = 0xE1;
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), (ushort)payload);
        ExifIdent.CopyTo(segment, 4);
        tiff.CopyTo(segment, 10);
        return segment;
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

    private static void W16(Span<byte> b, ushort value, bool little)
    {
        if (little)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(b, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt16BigEndian(b, value);
        }
    }

    private static void W32(Span<byte> b, uint value, bool little)
    {
        if (little)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(b, value);
        }
    }
}
