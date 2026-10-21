using System.Buffers.Binary;
using System.Text;

namespace PhotoViewerV3.Tests.Metadata;

/// <summary>
/// Baut kleine synthetische Dateien mit Aufnahmedatum für die Parser-Tests (ADR-0046): TIFF-Block mit EXIF-IFD, darum
/// JPEG (APP1), PNG (eXIf), WebP (EXIF), HEIC (meta/iinf/iloc + mdat) und MP4/MOV (mvhd, Apple keys/ilst). Keine echten
/// Bilddaten — die Leser lesen nur die Metadaten.
/// </summary>
internal static class SyntheticMediaFiles
{
    /// <summary>TIFF-Block: IFD0 mit Verweis auf EXIF-IFD; dort DateTimeOriginal (+ optional OffsetTimeOriginal).</summary>
    public static byte[] Tiff(string? dateTimeOriginal, string? offset = null, bool bigEndian = false)
    {
        var entries = new List<(ushort Tag, byte[] Value)>();
        if (dateTimeOriginal is not null)
        {
            entries.Add((0x9003, Encoding.ASCII.GetBytes(dateTimeOriginal + "\0")));
        }

        if (offset is not null)
        {
            entries.Add((0x9011, Encoding.ASCII.GetBytes(offset + "\0")));
        }

        var buffer = new List<byte>();
        void U16(ushort v)
        {
            var b = new byte[2];
            if (bigEndian) BinaryPrimitives.WriteUInt16BigEndian(b, v); else BinaryPrimitives.WriteUInt16LittleEndian(b, v);
            buffer.AddRange(b);
        }

        void U32(uint v)
        {
            var b = new byte[4];
            if (bigEndian) BinaryPrimitives.WriteUInt32BigEndian(b, v); else BinaryPrimitives.WriteUInt32LittleEndian(b, v);
            buffer.AddRange(b);
        }

        buffer.AddRange(bigEndian ? "MM"u8.ToArray() : "II"u8.ToArray());
        U16(42);
        U32(8); // IFD0 direkt dahinter

        // IFD0: ein Eintrag (ExifIFD-Zeiger), Ende-Zeiger 0
        const uint ifd0Size = 2 + 12 + 4;
        const uint exifIfdOffset = 8 + ifd0Size;
        U16(1);
        U16(0x8769);
        U16(4); // LONG
        U32(1);
        U32(exifIfdOffset);
        U32(0);

        // EXIF-IFD; Werte > 4 Byte hinter dem Verzeichnis
        var exifIfdSize = (uint)(2 + (entries.Count * 12) + 4);
        var valueOffset = exifIfdOffset + exifIfdSize;
        U16((ushort)entries.Count);
        var values = new List<byte>();
        foreach (var (tag, value) in entries)
        {
            U16(tag);
            U16(2); // ASCII
            U32((uint)value.Length);
            if (value.Length <= 4)
            {
                var inline = new byte[4];
                value.CopyTo(inline, 0);
                buffer.AddRange(inline);
            }
            else
            {
                U32(valueOffset + (uint)values.Count);
                values.AddRange(value);
            }
        }

        U32(0);
        buffer.AddRange(values);
        return buffer.ToArray();
    }

    public static byte[] Jpeg(byte[]? tiff, bool withJfif = true)
    {
        var ms = new MemoryStream();
        ms.Write([0xFF, 0xD8]);
        if (withJfif)
        {
            var jfif = "JFIF\0"u8.ToArray().Concat(new byte[9]).ToArray();
            WriteSegment(ms, 0xE0, jfif);
        }

        if (tiff is not null)
        {
            WriteSegment(ms, 0xE1, "Exif\0\0"u8.ToArray().Concat(tiff).ToArray());
        }

        WriteSegment(ms, 0xDB, new byte[67]); // Quantisierungstabelle (Platzhalter)
        ms.Write([0xFF, 0xDA, 0x00, 0x08, 1, 2, 3, 4, 5, 6]);
        ms.Write(new byte[64]);
        ms.Write([0xFF, 0xD9]);
        return ms.ToArray();
    }

    public static byte[] Png(byte[] tiff)
    {
        var ms = new MemoryStream();
        ms.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(ms, "IHDR", new byte[13]);
        WriteChunk(ms, "IDAT", new byte[20]);
        WriteChunk(ms, "eXIf", tiff); // auch hinter IDAT erlaubt
        WriteChunk(ms, "IEND", []);
        return ms.ToArray();
    }

    public static byte[] WebP(byte[] tiff, bool exifPrefix)
    {
        var chunks = new MemoryStream();
        WriteRiffChunk(chunks, "VP8X", new byte[10]);
        WriteRiffChunk(chunks, "VP8 ", new byte[31]); // ungerade Länge → Füllbyte
        WriteRiffChunk(chunks, "EXIF", exifPrefix ? "Exif\0\0"u8.ToArray().Concat(tiff).ToArray() : tiff);
        var body = chunks.ToArray();
        var ms = new MemoryStream();
        ms.Write("RIFF"u8);
        ms.Write(LE32((uint)(4 + body.Length)));
        ms.Write("WEBP"u8);
        ms.Write(body);
        return ms.ToArray();
    }

    /// <summary>HEIC: ftyp + meta(hdlr, iinf mit Element 1 „hvc1“ und 2 „Exif“, iloc) + mdat mit den Exif-Daten.</summary>
    public static byte[] Heic(byte[] tiff, int infeVersion = 2)
    {
        var exifPayload = BE32(6).Concat("Exif\0\0"u8.ToArray()).Concat(tiff).ToArray();
        var ftyp = Box("ftyp", "heic"u8.ToArray().Concat(BE32(0)).Concat("mif1heic"u8.ToArray()).ToArray());
        var hdlr = FullBox("hdlr", 0, BE32(0).Concat("pict"u8.ToArray()).Concat(new byte[13]).ToArray());

        byte[] Infe(uint id, string type)
            => infeVersion == 2
                ? FullBox("infe", 2, BE16((ushort)id).Concat(BE16(0)).Concat(Encoding.ASCII.GetBytes(type)).Concat(new byte[] { 0 }).ToArray())
                : FullBox("infe", 3, BE32(id).Concat(BE16(0)).Concat(Encoding.ASCII.GetBytes(type)).Concat(new byte[] { 0 }).ToArray());

        var iinf = FullBox("iinf", 0, BE16(2).Concat(Infe(1, "hvc1")).Concat(Infe(2, "Exif")).ToArray());

        // iloc v0: offset_size 4, length_size 4, base_offset_size 0; zwei Elemente mit je einem Abschnitt
        byte[] Iloc(uint exifOffset) => FullBox("iloc", 0, new byte[] { 0x44, 0x00 }
            .Concat(BE16(2))
            .Concat(BE16(1)).Concat(BE16(0)).Concat(BE16(1)).Concat(BE32(0)).Concat(BE32(0))
            .Concat(BE16(2)).Concat(BE16(0)).Concat(BE16(1)).Concat(BE32(exifOffset)).Concat(BE32((uint)exifPayload.Length))
            .ToArray());

        // Größe von meta hängt nicht vom Offset-Wert ab → zweimal bauen.
        var meta = FullBox("meta", 0, hdlr.Concat(iinf).Concat(Iloc(0)).ToArray());
        var exifOffset = (uint)(ftyp.Length + meta.Length + 8);
        meta = FullBox("meta", 0, hdlr.Concat(iinf).Concat(Iloc(exifOffset)).ToArray());
        var mdat = Box("mdat", exifPayload);
        return ftyp.Concat(meta).Concat(mdat).ToArray();
    }

    /// <summary>MP4: ftyp + (optional großes mdat davor) + moov/mvhd mit Erstellzeit (Sekunden seit 1904, UTC).</summary>
    public static byte[] Mp4(DateTime? creationUtc, bool moovAfterLargeMdat = false, bool version1 = false, string? appleCreationDate = null)
    {
        var ftyp = Box("ftyp", "isom"u8.ToArray().Concat(BE32(512)).Concat("isomiso2mp41"u8.ToArray()).ToArray());
        var seconds = creationUtc is { } c ? (ulong)(c - new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds : 0UL;
        var mvhdBody = version1
            ? BE64(seconds).Concat(BE64(seconds)).Concat(BE32(1000)).Concat(BE64(5000)).Concat(new byte[80]).ToArray()
            : BE32((uint)seconds).Concat(BE32((uint)seconds)).Concat(BE32(1000)).Concat(BE32(5000)).Concat(new byte[80]).ToArray();
        var mvhd = FullBox("mvhd", (byte)(version1 ? 1 : 0), mvhdBody);
        var moovBody = mvhd.AsEnumerable();
        if (appleCreationDate is not null)
        {
            const string key = "com.apple.quicktime.creationdate";
            var keys = FullBox("keys", 0, BE32(2)
                .Concat(BE32(8 + 10)).Concat("mdta"u8.ToArray()).Concat("com.make01"u8.ToArray())
                .Concat(BE32((uint)(8 + key.Length))).Concat("mdta"u8.ToArray()).Concat(Encoding.ASCII.GetBytes(key))
                .ToArray());
            var data = Box("data", BE32(1).Concat(BE32(0)).Concat(Encoding.UTF8.GetBytes(appleCreationDate)).ToArray());
            var item = BE32((uint)(8 + data.Length)).Concat(BE32(2)).Concat(data).ToArray(); // Typ = Schlüssel-Index 2
            var ilst = Box("ilst", item);
            var hdlr = FullBox("hdlr", 0, BE32(0).Concat("mdta"u8.ToArray()).Concat(new byte[13]).ToArray());
            var meta = Box("meta", hdlr.Concat(keys).Concat(ilst).ToArray()); // QuickTime: ohne Versions-Kopf
            moovBody = moovBody.Concat(meta);
        }

        var moov = Box("moov", moovBody.ToArray());
        var mdat = Box("mdat", new byte[moovAfterLargeMdat ? 1_048_576 : 64]);
        return moovAfterLargeMdat ? ftyp.Concat(mdat).Concat(moov).ToArray() : ftyp.Concat(moov).Concat(mdat).ToArray();
    }

    private static void WriteSegment(Stream s, byte marker, byte[] data)
    {
        s.Write([0xFF, marker]);
        s.Write(BE16((ushort)(data.Length + 2)));
        s.Write(data);
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        s.Write(BE32((uint)data.Length));
        s.Write(Encoding.ASCII.GetBytes(type));
        s.Write(data);
        s.Write(new byte[4]); // CRC (nicht geprüft)
    }

    private static void WriteRiffChunk(Stream s, string fourcc, byte[] data)
    {
        s.Write(Encoding.ASCII.GetBytes(fourcc));
        s.Write(LE32((uint)data.Length));
        s.Write(data);
        if ((data.Length & 1) == 1)
        {
            s.WriteByte(0);
        }
    }

    private static byte[] Box(string type, byte[] body)
        => BE32((uint)(8 + body.Length)).Concat(Encoding.ASCII.GetBytes(type)).Concat(body).ToArray();

    private static byte[] FullBox(string type, byte version, byte[] body)
        => Box(type, new byte[] { version, 0, 0, 0 }.Concat(body).ToArray());

    private static byte[] BE16(ushort v)
    {
        var b = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, v);
        return b;
    }

    private static byte[] BE32(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        return b;
    }

    private static byte[] BE64(ulong v)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(b, v);
        return b;
    }

    private static byte[] LE32(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        return b;
    }
}
