using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using PhotoViewerV3.Domain.Enums;

namespace PhotoViewerV3.Services.Metadata;

/// <summary>
/// Liest das Aufnahmedatum aus den Metadaten einer Datei (ADR-0046) — erkannt am Dateikopf, nicht an der Endung:
/// <list type="bullet">
/// <item>JPEG: APP1-Segment „Exif“ → TIFF (<see cref="ExifTiffReader"/>);</item>
/// <item>TIFF und TIFF-basierte Rohformate (DNG, CR2, NEF, ARW, ORF, RW2 …), Fuji RAF (eingebettetes JPEG);</item>
/// <item>PNG: Chunk <c>eXIf</c>; WebP: Chunk <c>EXIF</c>;</item>
/// <item>ISO-BMFF: HEIC/HEIF/AVIF über das Element „Exif“ (<c>meta/iinf</c> + <c>iloc</c>); MP4/MOV/3GP über
/// <c>moov/meta</c> „com.apple.quicktime.creationdate“ (mit Zeitzone) bzw. <c>moov/mvhd</c> (UTC, Quelle Video).</item>
/// </list>
/// Rein verwaltet (keine WIC-/WPF-Abhängigkeit, kein Codec nötig — auch HEIC ohne HEIF-Erweiterung); gelesen werden nur
/// Kopf und Verzeichnisse per Seek, bei großen Videos also nie der Medieninhalt. Unbekanntes oder Beschädigtes ergibt null.
/// </summary>
public static class MediaDateReader
{
    private const int MaxJpegSegments = 128;
    private const int MaxChunks = 4096;
    private const int MaxBoxes = 4096;
    private const int MaxBoxDepth = 6;
    private static readonly DateTime QuickTimeEpoch = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime MinVideoUtc = new(1971, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Datum aus der Datei oder null (kein Datum, unbekanntes Format, nicht lesbar).</summary>
    public static CaptureDateValue? Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, FileOptions.RandomAccess);
            return Read(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Datum aus einem lesbaren, suchbaren Stream (Tests: MemoryStream).</summary>
    public static CaptureDateValue? Read(Stream stream)
    {
        try
        {
            Span<byte> head = stackalloc byte[16];
            if (stream.Length < 16)
            {
                return null;
            }

            stream.Position = 0;
            stream.ReadExactly(head);
            if (head[0] == 0xFF && head[1] == 0xD8)
            {
                return ReadJpeg(stream, 0);
            }

            if ((head[0] == 'I' && head[1] == 'I') || (head[0] == 'M' && head[1] == 'M'))
            {
                return ExifTiffReader.Read(stream, 0);
            }

            if (head[0] == 0x89 && head[1] == 'P' && head[2] == 'N' && head[3] == 'G')
            {
                return ReadPng(stream);
            }

            if (Ascii(head[..4]) == "RIFF" && Ascii(head[8..12]) == "WEBP")
            {
                return ReadWebP(stream);
            }

            if (Ascii(head[..15]) == "FUJIFILMCCD-RAW")
            {
                return ReadRaf(stream);
            }

            var type = Ascii(head[4..8]);
            if (type is "ftyp" or "moov" or "mdat" or "wide" or "free" or "skip" or "pnot")
            {
                return ReadIsoBmff(stream);
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException or OverflowException or InvalidDataException)
        {
            return null;
        }
    }

    // ---- JPEG ------------------------------------------------------------------------------------

    private static CaptureDateValue? ReadJpeg(Stream stream, long start)
    {
        var position = start + 2;
        Span<byte> marker = stackalloc byte[4];
        Span<byte> ident = stackalloc byte[6];
        for (var i = 0; i < MaxJpegSegments; i++)
        {
            if (!ReadAt(stream, position, marker) || marker[0] != 0xFF)
            {
                return null;
            }

            var code = marker[1];
            if (code == 0xFF)
            {
                position++; // Füllbyte
                continue;
            }

            if (code is 0xD9 or 0xDA)
            {
                return null; // Bildende bzw. Bilddaten — kein EXIF davor
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

            if (code == 0xE1 && length >= 8 && ReadAt(stream, position + 4, ident) && Ascii(ident[..4]) == "Exif" && ident[4] == 0 && ident[5] == 0)
            {
                if (ExifTiffReader.Read(stream, position + 10) is { } value)
                {
                    return value;
                }
            }

            position += 2 + length;
        }

        return null;
    }

    // ---- Fuji RAF: eingebettetes JPEG (Offset an Byte 84) ---------------------------------------

    private static CaptureDateValue? ReadRaf(Stream stream)
    {
        Span<byte> offset = stackalloc byte[4];
        return ReadAt(stream, 84, offset) ? ReadJpeg(stream, BinaryPrimitives.ReadUInt32BigEndian(offset)) : null;
    }

    // ---- PNG: eXIf ------------------------------------------------------------------------------

    private static CaptureDateValue? ReadPng(Stream stream)
    {
        long position = 8;
        Span<byte> header = stackalloc byte[8];
        for (var i = 0; i < MaxChunks && ReadAt(stream, position, header); i++)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = Ascii(header[4..]);
            if (type == "eXIf")
            {
                return ExifTiffReader.Read(stream, position + 8);
            }

            if (type == "IEND")
            {
                return null;
            }

            position += 12L + length;
        }

        return null;
    }

    // ---- WebP: EXIF -----------------------------------------------------------------------------

    private static CaptureDateValue? ReadWebP(Stream stream)
    {
        long position = 12;
        Span<byte> header = stackalloc byte[8];
        Span<byte> ident = stackalloc byte[6];
        for (var i = 0; i < MaxChunks && ReadAt(stream, position, header); i++)
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            if (Ascii(header[..4]) == "EXIF")
            {
                var data = position + 8;
                // Manche Encoder setzen „Exif\0\0“ vor den TIFF-Block.
                if (ReadAt(stream, data, ident) && Ascii(ident[..4]) == "Exif" && ident[4] == 0)
                {
                    data += 6;
                }

                return ExifTiffReader.Read(stream, data);
            }

            position += 8L + size + (size & 1);
        }

        return null;
    }

    // ---- ISO-BMFF: HEIF/AVIF (Exif-Element) und MP4/MOV (mvhd, Apple creationdate) ----------------

    private readonly record struct Box(string Type, long Start, long DataStart, long End);

    private static CaptureDateValue? ReadIsoBmff(Stream stream)
    {
        Box? meta = null;
        Box? moov = null;
        foreach (var box in Boxes(stream, 0, stream.Length))
        {
            if (box.Type == "meta")
            {
                meta = box;
            }
            else if (box.Type == "moov")
            {
                moov = box;
            }
        }

        if (meta is { } m && ReadHeifExif(stream, m) is { } exif)
        {
            return exif;
        }

        return moov is { } v ? ReadMovie(stream, v) : null;
    }

    private static IEnumerable<Box> Boxes(Stream stream, long start, long end)
    {
        var position = start;
        var header = new byte[16];
        for (var i = 0; i < MaxBoxes && position + 8 <= end; i++)
        {
            if (!ReadAt(stream, position, header.AsSpan(0, 8)))
            {
                yield break;
            }

            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = Ascii(header.AsSpan(4, 4));
            var dataStart = position + 8;
            if (size == 1)
            {
                if (!ReadAt(stream, position + 8, header.AsSpan(8, 8)))
                {
                    yield break;
                }

                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8, 8));
                dataStart += 8;
            }
            else if (size == 0)
            {
                size = end - position;
            }

            if (size < dataStart - position || position + size > end)
            {
                yield break;
            }

            yield return new Box(type, position, dataStart, position + size);
            position += size;
        }
    }

    /// <summary>HEIF: Element vom Typ „Exif“ in <c>iinf</c>, seine Lage in <c>iloc</c>; Nutzdaten = 4 Byte Versatz + TIFF.</summary>
    private static CaptureDateValue? ReadHeifExif(Stream stream, Box meta)
    {
        var children = meta.DataStart + 4; // FullBox: Version + Flags
        uint? exifItem = null;
        Box? iloc = null;
        foreach (var box in Boxes(stream, children, meta.End))
        {
            if (box.Type == "iinf")
            {
                exifItem = FindExifItem(stream, box);
            }
            else if (box.Type == "iloc")
            {
                iloc = box;
            }
        }

        if (exifItem is not { } item || iloc is not { } locations || FindItemOffset(stream, locations, item) is not { } offset)
        {
            return null;
        }

        Span<byte> skip = stackalloc byte[4];
        if (!ReadAt(stream, offset, skip))
        {
            return null;
        }

        var tiff = offset + 4 + BinaryPrimitives.ReadUInt32BigEndian(skip);
        return ExifTiffReader.Read(stream, tiff);
    }

    private static uint? FindExifItem(Stream stream, Box iinf)
    {
        Span<byte> head = stackalloc byte[4];
        if (!ReadAt(stream, iinf.DataStart, head))
        {
            return null;
        }

        var entriesStart = iinf.DataStart + 4 + (head[0] == 0 ? 2 : 4);
        Span<byte> infe = stackalloc byte[12];
        Span<byte> t = stackalloc byte[4];
        foreach (var box in Boxes(stream, entriesStart, iinf.End))
        {
            if (box.Type != "infe" || !ReadAt(stream, box.DataStart, infe))
            {
                continue;
            }

            var version = infe[0];
            if (version < 2)
            {
                continue;
            }

            uint id;
            string type;
            if (version == 2)
            {
                id = BinaryPrimitives.ReadUInt16BigEndian(infe[4..]);
                type = Ascii(infe[8..12]);
            }
            else
            {
                id = BinaryPrimitives.ReadUInt32BigEndian(infe[4..]);
                if (!ReadAt(stream, box.DataStart + 10, t))
                {
                    continue;
                }

                type = Ascii(t);
            }

            if (type == "Exif")
            {
                return id;
            }
        }

        return null;
    }

    /// <summary>Dateiversatz des ersten Abschnitts eines Elements (nur Bauart 0 = Datei-Offsets).</summary>
    private static long? FindItemOffset(Stream stream, Box iloc, uint itemId)
    {
        var length = (int)Math.Min(iloc.End - iloc.DataStart, 1 << 20);
        var data = new byte[length];
        if (!ReadAt(stream, iloc.DataStart, data))
        {
            return null;
        }

        var reader = new BigEndianCursor(data);
        var version = reader.U8();
        reader.Skip(3);
        var sizes = reader.U8();
        var offsetSize = sizes >> 4;
        var lengthSize = sizes & 0x0F;
        var sizes2 = reader.U8();
        var baseOffsetSize = sizes2 >> 4;
        var indexSize = version is 1 or 2 ? sizes2 & 0x0F : 0;
        var itemCount = version < 2 ? reader.U16() : reader.U32();
        for (uint i = 0; i < itemCount; i++)
        {
            var id = version < 2 ? reader.U16() : reader.U32();
            var method = 0;
            if (version is 1 or 2)
            {
                method = reader.U16() & 0x0F;
            }

            reader.Skip(2); // data_reference_index
            var baseOffset = reader.UVar(baseOffsetSize);
            var extents = reader.U16();
            long? first = null;
            for (var e = 0; e < extents; e++)
            {
                if (indexSize > 0)
                {
                    reader.UVar(indexSize);
                }

                var extentOffset = reader.UVar(offsetSize);
                reader.UVar(lengthSize);
                first ??= (long)(baseOffset + extentOffset);
            }

            if (id == itemId)
            {
                return method == 0 ? first : null;
            }
        }

        return null;
    }

    /// <summary>MP4/MOV: Apple „creationdate“ (Ortszeit mit Zone) vor <c>mvhd</c> (UTC).</summary>
    private static CaptureDateValue? ReadMovie(Stream stream, Box moov)
    {
        DateTime? mvhdUtc = null;
        CaptureDateValue? apple = null;
        foreach (var box in Boxes(stream, moov.DataStart, moov.End))
        {
            if (box.Type == "mvhd")
            {
                mvhdUtc = ReadMvhd(stream, box);
            }
            else if (box.Type == "meta")
            {
                apple ??= ReadAppleCreationDate(stream, box);
            }
            else if (box.Type == "udta")
            {
                foreach (var child in Boxes(stream, box.DataStart, box.End))
                {
                    if (child.Type == "meta")
                    {
                        apple ??= ReadAppleCreationDate(stream, child);
                    }
                }
            }
        }

        if (apple is { } a)
        {
            return a;
        }

        return mvhdUtc is { } utc ? CaptureDateValue.FromUtc(utc, CaptureDateSource.Video) : null;
    }

    private static DateTime? ReadMvhd(Stream stream, Box mvhd)
    {
        Span<byte> data = stackalloc byte[12];
        if (!ReadAt(stream, mvhd.DataStart, data))
        {
            return null;
        }

        var seconds = data[0] == 1 ? BinaryPrimitives.ReadUInt64BigEndian(data[4..]) : BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
        if (seconds == 0 || seconds > 20_000_000_000UL)
        {
            return null;
        }

        var utc = QuickTimeEpoch.AddSeconds(seconds);
        return utc >= MinVideoUtc && CaptureDateValue.IsPlausible(utc) ? utc : null;
    }

    /// <summary>QuickTime-Metadaten: <c>keys</c> (Namen) + <c>ilst</c> (Werte je Schlüssel-Index, Atom „data“).</summary>
    private static CaptureDateValue? ReadAppleCreationDate(Stream stream, Box meta)
    {
        // QuickTime-„meta“ hat keinen Versions-Kopf (erstes Kind direkt), das ISO-„meta“ schon.
        Span<byte> probe = stackalloc byte[12];
        if (!ReadAt(stream, meta.DataStart, probe))
        {
            return null;
        }

        var children = Ascii(probe[4..8]) == "hdlr" ? meta.DataStart : meta.DataStart + 4;
        int? keyIndex = null;
        Box? ilst = null;
        foreach (var box in Boxes(stream, children, meta.End))
        {
            if (box.Type == "keys")
            {
                keyIndex = FindKeyIndex(stream, box, "com.apple.quicktime.creationdate");
            }
            else if (box.Type == "ilst")
            {
                ilst = box;
            }
        }

        if (keyIndex is not { } index || ilst is not { } list)
        {
            return null;
        }

        Span<byte> number = stackalloc byte[4];
        foreach (var item in Boxes(stream, list.DataStart, list.End))
        {
            // Der „Typ“ eines ilst-Eintrags ist hier der 1-basierte Schlüssel-Index (big endian).
            if (!ReadAt(stream, item.Start + 4, number) || BinaryPrimitives.ReadUInt32BigEndian(number) != (uint)index)
            {
                continue;
            }

            foreach (var data in Boxes(stream, item.DataStart, item.End))
            {
                if (data.Type != "data" || data.End - data.DataStart <= 8 || data.End - data.DataStart > 256)
                {
                    continue;
                }

                var bytes = new byte[data.End - data.DataStart - 8];
                if (ReadAt(stream, data.DataStart + 8, bytes) && ParseIsoDateTime(Encoding.UTF8.GetString(bytes)) is { } value)
                {
                    return value;
                }
            }
        }

        return null;
    }

    private static int? FindKeyIndex(Stream stream, Box keys, string name)
    {
        Span<byte> head = stackalloc byte[8];
        if (!ReadAt(stream, keys.DataStart, head))
        {
            return null;
        }

        var count = BinaryPrimitives.ReadUInt32BigEndian(head[4..]);
        var position = keys.DataStart + 8;
        Span<byte> entry = stackalloc byte[8];
        for (var i = 1; i <= count && i <= MaxBoxes && ReadAt(stream, position, entry); i++)
        {
            var size = BinaryPrimitives.ReadUInt32BigEndian(entry);
            if (size < 8 || position + size > keys.End)
            {
                return null;
            }

            if (size - 8 == name.Length)
            {
                var text = new byte[size - 8];
                if (ReadAt(stream, position + 8, text) && Encoding.ASCII.GetString(text) == name)
                {
                    return i;
                }
            }

            position += size;
        }

        return null;
    }

    /// <summary>„2024-07-12T15:30:12+0200“ bzw. „…+02:00“ → Ortszeit mit Zone (Quelle Video).</summary>
    public static CaptureDateValue? ParseIsoDateTime(string text)
    {
        var t = text.Trim().TrimEnd('\0');
        if (t.Length >= 24 && (t[^5] == '+' || t[^5] == '-') && char.IsAsciiDigit(t[^1]))
        {
            t = t[..^2] + ":" + t[^2..]; // +0200 → +02:00
        }

        if (!DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
        {
            return null;
        }

        var offset = (int)value.Offset.TotalMinutes;
        var result = CaptureDateValue.FromWallClock(value.DateTime, offset, CaptureDateSource.Video, CaptureDatePrecision.Exact);
        return result.Utc >= MinVideoUtc && CaptureDateValue.IsPlausible(result.Utc) ? result : null;
    }

    // ---- Hilfen ---------------------------------------------------------------------------------

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

    private static string Ascii(ReadOnlySpan<byte> bytes) => Encoding.ASCII.GetString(bytes);

    /// <summary>Big-Endian-Leser über einem kleinen Puffer (iloc); läuft er über, endet die Suche (InvalidDataException).</summary>
    private ref struct BigEndianCursor(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _position;

        public byte U8() => Take(1)[0];

        public ushort U16() => BinaryPrimitives.ReadUInt16BigEndian(Take(2));

        public uint U32() => BinaryPrimitives.ReadUInt32BigEndian(Take(4));

        public ulong UVar(int size) => size switch
        {
            0 => 0,
            4 => U32(),
            8 => BinaryPrimitives.ReadUInt64BigEndian(Take(8)),
            2 => U16(),
            _ => throw new InvalidDataException("iloc: unbekannte Feldgröße"),
        };

        public void Skip(int count) => Take(count);

        private ReadOnlySpan<byte> Take(int count)
        {
            if (_position + count > _data.Length)
            {
                throw new InvalidDataException("iloc: abgeschnitten");
            }

            var slice = _data.Slice(_position, count);
            _position += count;
            return slice;
        }
    }
}
