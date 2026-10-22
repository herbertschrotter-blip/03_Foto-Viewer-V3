using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Lumora.Services.Metadata;

/// <summary>
/// GPS-Angaben einer Datei für die Genauigkeit und den Zeitabgleich (ADR-0062).
/// </summary>
/// <param name="HasCoordinate">Gültige Breite/Länge (Tags 1–4).</param>
/// <param name="HasAltitude">GPS-Höhe (Tag 6) vorhanden.</param>
/// <param name="HasGpsTime">GPS-Uhrzeit (Tag 7) vorhanden.</param>
/// <param name="GpsTimeUtc">GPS-Datum (29) + Uhrzeit (7) als UTC, wenn beide lesbar.</param>
/// <param name="ProcessingMethod">Text aus <c>GPSProcessingMethod</c> (27), z. B. „NETWORK“, „GPS“.</param>
/// <param name="HorizontalError">Fehlerradius aus <c>GPSHPositioningError</c> (31) in Metern.</param>
/// <param name="OffsetMinutes">Abstand zu UTC aus <c>OffsetTimeOriginal</c> (36881), null ohne.</param>
/// <param name="CameraModel">„Hersteller Modell“ aus IFD0 (Make/Model), null ohne.</param>
public readonly record struct GpsExifDetails(
    bool HasCoordinate,
    bool HasAltitude,
    bool HasGpsTime,
    DateTime? GpsTimeUtc,
    string? ProcessingMethod,
    double? HorizontalError,
    int? OffsetMinutes,
    string? CameraModel);

/// <summary>
/// Liest die für ADR-0062 nötigen EXIF-Angaben aus denselben Containern wie Datum und GPS
/// (<see cref="MediaDateReader.ReadContainer{T}"/>: JPEG, TIFF/RAW, HEIC, PNG, WebP): IFD0 Make/Model, EXIF-IFD
/// <c>OffsetTimeOriginal</c> und im GPS-Verzeichnis Höhe, Zeit, Verfahren und Fehlerradius. Nur Kopf und Verzeichnisse,
/// Fehlerhaftes ergibt null bzw. fehlende Felder, nie eine Ausnahme. Videos liefern null (Video-GPS wird nicht bewertet).
/// </summary>
public static class ExifGpsDetailsReader
{
    private const ushort TagMake = 0x010F;
    private const ushort TagModel = 0x0110;
    private const ushort TagExifIfd = 0x8769;
    private const ushort TagOffsetTimeOriginal = 0x9011;
    private const ushort TagAltitude = 6;
    private const ushort TagTimeStamp = 7;
    private const ushort TagProcessingMethod = 27;
    private const ushort TagDateStamp = 29;
    private const ushort TagHPositioningError = 31;
    private const ushort TypeAscii = 2;
    private const ushort TypeUndefined = 7;
    private const ushort TypeRational = 5;
    private const ushort TypeSRational = 10;
    private const int MaxEntries = 1024;

    private readonly record struct Entry(ushort Tag, ushort Type, uint Count, uint ValueOrOffset, long ValueFieldPosition);

    /// <summary>Angaben aus der Datei oder null (nicht lesbar, Video, kein EXIF).</summary>
    public static GpsExifDetails? Read(string path)
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

    /// <summary>Angaben aus einem lesbaren, suchbaren Stream (Tests: MemoryStream).</summary>
    public static GpsExifDetails? Read(Stream stream)
        => MediaDateReader.ReadContainer<GpsExifDetails>(stream, ReadTiff, static (_, _) => null);

    /// <summary>Angaben aus dem TIFF-Block an <paramref name="tiffStart"/>.</summary>
    public static GpsExifDetails? ReadTiff(Stream stream, long tiffStart)
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

            var ifd0 = ReadIfd(stream, tiffStart, U32(header[4..], little), little);
            if (ifd0 is null)
            {
                return null;
            }

            var make = ifd0.TryGetValue(TagMake, out var makeEntry) ? ReadText(stream, tiffStart, makeEntry, little) : null;
            var model = ifd0.TryGetValue(TagModel, out var modelEntry) ? ReadText(stream, tiffStart, modelEntry, little) : null;

            int? offset = null;
            if (ifd0.TryGetValue(TagExifIfd, out var exifPointer) && ReadIfd(stream, tiffStart, exifPointer.ValueOrOffset, little) is { } exif
                && exif.TryGetValue(TagOffsetTimeOriginal, out var offsetEntry))
            {
                offset = ExifTiffReader.ParseOffset(ReadText(stream, tiffStart, offsetEntry, little));
            }

            var coordinate = ExifGpsReader.ReadCoordinate(stream, tiffStart);
            var hasAltitude = false;
            var hasTime = false;
            DateTime? gpsTime = null;
            string? method = null;
            double? error = null;
            if (ifd0.TryGetValue(ExifGpsReader.TagGpsIfd, out var gpsPointer) && ReadIfd(stream, tiffStart, gpsPointer.ValueOrOffset, little) is { } gps)
            {
                hasAltitude = gps.TryGetValue(TagAltitude, out var alt) && ReadRationals(stream, tiffStart, alt, little, 1) is not null;
                double[]? time = gps.TryGetValue(TagTimeStamp, out var timeEntry) ? ReadRationals(stream, tiffStart, timeEntry, little, 3) : null;
                hasTime = time is not null;
                var date = gps.TryGetValue(TagDateStamp, out var dateEntry) ? ReadText(stream, tiffStart, dateEntry, little) : null;
                gpsTime = CombineGpsTime(date, time);
                method = gps.TryGetValue(TagProcessingMethod, out var methodEntry) ? ReadProcessingMethod(stream, tiffStart, methodEntry, little) : null;
                error = gps.TryGetValue(TagHPositioningError, out var errorEntry) && ReadRationals(stream, tiffStart, errorEntry, little, 1) is { } e ? e[0] : null;
            }

            return new GpsExifDetails(coordinate is not null, hasAltitude, hasTime, gpsTime, method, error, offset, JoinCamera(make, model));
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>„Apple iPhone 12“ — der Hersteller entfällt, wenn das Modell ihn schon nennt („Canon EOS 80D“).</summary>
    public static string? JoinCamera(string? make, string? model)
    {
        make = Clean(make);
        model = Clean(model);
        if (model is null)
        {
            return make;
        }

        if (make is null || model.StartsWith(make, StringComparison.OrdinalIgnoreCase))
        {
            return model;
        }

        // „NIKON CORPORATION“ + „NIKON D750“ → nur das Modell
        var firstWord = make.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        return model.StartsWith(firstWord, StringComparison.OrdinalIgnoreCase) ? model : $"{make} {model}";
    }

    /// <summary>GPS-Datum „YYYY:MM:DD“ + Uhrzeit (Stunde, Minute, Sekunde als RATIONAL) → UTC; null bei Unlesbarem.</summary>
    public static DateTime? CombineGpsTime(string? date, double[]? time)
    {
        if (string.IsNullOrWhiteSpace(date) || time is not { Length: 3 })
        {
            return null;
        }

        var d = date.Trim().Replace('-', ':');
        if (!DateTime.TryParseExact(d.Length >= 10 ? d[..10] : d, "yyyy:MM:dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            || time[0] is < 0 or >= 24 || time[1] is < 0 or >= 60 || time[2] is < 0 or >= 61)
        {
            return null;
        }

        var value = DateTime.SpecifyKind(day, DateTimeKind.Utc).AddHours(Math.Floor(time[0])).AddMinutes(Math.Floor(time[1])).AddSeconds(Math.Floor(time[2]));
        return CaptureDateValue.IsPlausible(value) ? value : null;
    }

    private static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var t = text.Trim().TrimEnd('\0').Trim();
        return t.Length == 0 ? null : t.Length > 64 ? t[..64] : t;
    }

    /// <summary>GPSProcessingMethod: UNDEFINED mit 8 Byte Zeichensatz-Kennung („ASCII\0\0\0“) oder ASCII.</summary>
    private static string? ReadProcessingMethod(Stream stream, long tiffStart, Entry entry, bool little)
    {
        if (entry.Type is not (TypeUndefined or TypeAscii) || entry.Count == 0 || entry.Count > 256)
        {
            return null;
        }

        var bytes = ReadBytes(stream, tiffStart, entry, little);
        if (bytes is null)
        {
            return null;
        }

        var span = bytes.AsSpan();
        if (entry.Type == TypeUndefined && span.Length >= 8 && (span[..5].SequenceEqual("ASCII"u8) || span[..7].SequenceEqual("UNICODE"u8) || span[..8].SequenceEqual(new byte[8])))
        {
            var unicode = span[..7].SequenceEqual("UNICODE"u8);
            span = span[8..];
            return Clean(unicode ? (little ? Encoding.Unicode : Encoding.BigEndianUnicode).GetString(span) : Encoding.ASCII.GetString(span));
        }

        return Clean(Encoding.ASCII.GetString(span));
    }

    private static string? ReadText(Stream stream, long tiffStart, Entry entry, bool little)
    {
        if (entry.Type != TypeAscii || entry.Count == 0 || entry.Count > 256)
        {
            return null;
        }

        var bytes = ReadBytes(stream, tiffStart, entry, little);
        return bytes is null ? null : Clean(Encoding.ASCII.GetString(bytes));
    }

    /// <summary>Wert-Bytes eines Eintrags mit Elementgröße 1 (ASCII/UNDEFINED): bis 4 Bytes inline, sonst am Versatz.</summary>
    private static byte[]? ReadBytes(Stream stream, long tiffStart, Entry entry, bool little)
    {
        var bytes = new byte[entry.Count];
        if (entry.Count <= 4)
        {
            Span<byte> raw = stackalloc byte[4];
            if (little)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(raw, entry.ValueOrOffset);
            }
            else
            {
                BinaryPrimitives.WriteUInt32BigEndian(raw, entry.ValueOrOffset);
            }

            raw[..(int)entry.Count].CopyTo(bytes);
            return bytes;
        }

        return ReadAt(stream, tiffStart + entry.ValueOrOffset, bytes) ? bytes : null;
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
            double numerator = entry.Type == TypeSRational ? (int)U32(span, little) : U32(span, little);
            double denominator = entry.Type == TypeSRational ? (int)U32(span[4..], little) : U32(span[4..], little);
            if (denominator == 0)
            {
                return null;
            }

            result[i] = numerator / denominator;
        }

        return result;
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
