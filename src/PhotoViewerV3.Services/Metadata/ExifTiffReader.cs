using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using PhotoViewerV3.Domain.Enums;

namespace PhotoViewerV3.Services.Metadata;

/// <summary>
/// Liest das Aufnahmedatum aus einem TIFF-Block (EXIF in JPEG/PNG/WebP/HEIF bzw. TIFF-basierte Rohformate wie DNG, CR2,
/// NEF, ARW, ORF, RW2): IFD0 → EXIF-IFD (Tag 0x8769) → <c>DateTimeOriginal</c> (0x9003), dazu
/// <c>OffsetTimeOriginal</c> (0x9011). <c>DateTimeDigitized</c> und das IFD0-<c>DateTime</c> zählen bewusst NICHT: bei
/// Scans sind sie das Scan- bzw. Bearbeitungsdatum (ADR-0046). Arbeitet direkt auf dem Stream (Seek + kleine Reads),
/// damit bei großen Rohdateien nur die paar hundert Bytes der Verzeichnisse gelesen werden. Fehlerhafte oder
/// abgeschnittene Daten ergeben null, nie eine Ausnahme.
/// </summary>
public static class ExifTiffReader
{
    private const ushort TagExifIfd = 0x8769;
    private const ushort TagDateTimeOriginal = 0x9003;
    private const ushort TagOffsetTimeOriginal = 0x9011;
    private const ushort TypeAscii = 2;
    private const int MaxEntries = 1024;

    /// <summary>Datum aus dem TIFF-Block, der an <paramref name="tiffStart"/> im Stream beginnt (Byte-Order-Marke „II“/„MM“).</summary>
    public static CaptureDateValue? Read(Stream stream, long tiffStart)
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
            var exifIfd = FindEntry(stream, tiffStart, ifd0, little, TagExifIfd) is { } exifEntry ? (uint?)exifEntry.ValueOrOffset : null;
            if (exifIfd is null)
            {
                return null;
            }

            var original = ReadAscii(stream, tiffStart, exifIfd.Value, little, TagDateTimeOriginal);
            if (ParseExifDateTime(original) is not { } wall)
            {
                return null;
            }

            var offset = ParseOffset(ReadAscii(stream, tiffStart, exifIfd.Value, little, TagOffsetTimeOriginal));
            var value = CaptureDateValue.FromWallClock(wall, offset, CaptureDateSource.Exif, CaptureDatePrecision.Exact);
            return CaptureDateValue.IsPlausible(value.Utc) ? value : null;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>„YYYY:MM:DD HH:MM:SS“ (auch mit „-“ oder „/“ im Datum, ohne Sekunden); Leer- oder Nullwerte ergeben null.</summary>
    public static DateTime? ParseExifDateTime(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var t = text.Trim().TrimEnd('\0').Trim();
        if (t.Length < 16)
        {
            return null;
        }

        // Datumsteil normalisieren: 2024:07:12 / 2024-07-12 / 2024/07/12
        var normalized = new StringBuilder(t);
        for (var i = 0; i < Math.Min(10, normalized.Length); i++)
        {
            if (normalized[i] is '-' or '/')
            {
                normalized[i] = ':';
            }
        }

        if (normalized.Length > 10 && normalized[10] == 'T')
        {
            normalized[10] = ' ';
        }

        var s = normalized.ToString();
        string[] formats = ["yyyy:MM:dd HH:mm:ss", "yyyy:MM:dd HH:mm"];
        var core = s.Length >= 19 ? s[..19] : s;
        if (DateTime.TryParseExact(core, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            && value.Year >= CaptureDateValue.MinPlausible.Year)
        {
            return value;
        }

        return null;
    }

    /// <summary>„+02:00“, „-0530“, „Z“ → Minuten; sonst null.</summary>
    public static int? ParseOffset(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var t = text.Trim().TrimEnd('\0').Trim();
        if (t is "Z" or "z")
        {
            return 0;
        }

        if (t.Length < 3 || (t[0] != '+' && t[0] != '-'))
        {
            return null;
        }

        var digits = t[1..].Replace(":", string.Empty, StringComparison.Ordinal);
        if (digits.Length is not (2 or 4) || !digits.All(char.IsAsciiDigit))
        {
            return null;
        }

        var hours = int.Parse(digits[..2], CultureInfo.InvariantCulture);
        var minutes = digits.Length == 4 ? int.Parse(digits[2..], CultureInfo.InvariantCulture) : 0;
        if (hours > 14 || minutes > 59)
        {
            return null;
        }

        var total = (hours * 60) + minutes;
        return t[0] == '-' ? -total : total;
    }

    private readonly record struct Entry(ushort Tag, ushort Type, uint Count, uint ValueOrOffset, long ValueFieldPosition);

    private static Entry? FindEntry(Stream stream, long tiffStart, uint ifdOffset, bool little, ushort tag)
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

        for (var i = 0; i < count; i++)
        {
            var e = entries.AsSpan(i * 12, 12);
            if (U16(e, little) != tag)
            {
                continue;
            }

            return new Entry(tag, U16(e[2..], little), U32(e[4..], little), U32(e[8..], little), tiffStart + ifdOffset + 2 + (i * 12) + 8);
        }

        return null;
    }

    private static string? ReadAscii(Stream stream, long tiffStart, uint ifdOffset, bool little, ushort tag)
    {
        if (FindEntry(stream, tiffStart, ifdOffset, little, tag) is not { Type: TypeAscii } entry || entry.Count == 0 || entry.Count > 256)
        {
            return null;
        }

        var bytes = new byte[entry.Count];
        var position = entry.Count <= 4 ? entry.ValueFieldPosition : tiffStart + entry.ValueOrOffset;
        return ReadAt(stream, position, bytes) ? Encoding.ASCII.GetString(bytes) : null;
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
