using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Lumora.Domain.Enums;

namespace Lumora.Services.Metadata;

/// <summary>
/// Liest die GPS-Koordinate aus den Metadaten einer Datei (ADR-0050) — dieselben Container wie das Aufnahmedatum
/// (<see cref="MediaDateReader.ReadContainer{T}"/>): EXIF in JPEG/TIFF/RAW/HEIC/PNG/WebP (<see cref="ExifGpsReader"/>), bei
/// MP4/MOV/3GP das Atom <c>moov/udta/©xyz</c> (Android, ältere Kameras) bzw. Apple
/// <c>com.apple.quicktime.location.ISO6709</c> in <c>moov/meta</c> (iPhone). Gelesen werden nur Kopf und Verzeichnisse.
/// XMP-Begleitdateien liest <see cref="GeoLocationReader"/>.
/// </summary>
public static partial class MediaGpsReader
{
    private const string AppleLocationKey = "com.apple.quicktime.location.ISO6709";

    /// <summary>Koordinate aus der Datei oder null (keine, unbekanntes Format, nicht lesbar).</summary>
    public static GeoReading? Read(string path)
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

    /// <summary>Koordinate aus einem lesbaren, suchbaren Stream (Tests: MemoryStream).</summary>
    public static GeoReading? Read(Stream stream)
        => MediaDateReader.ReadContainer<GeoReading>(stream, ExifGpsReader.Read, ReadMovie);

    private static GeoReading? ReadMovie(Stream stream, MediaDateReader.Box moov)
    {
        GeoCoordinate? xyz = null;
        GeoCoordinate? apple = null;
        foreach (var box in MediaDateReader.Boxes(stream, moov.DataStart, moov.End))
        {
            if (box.Type == "meta")
            {
                apple ??= ReadAppleLocation(stream, box);
            }
            else if (box.Type == "udta")
            {
                foreach (var child in MediaDateReader.Boxes(stream, box.DataStart, box.End))
                {
                    if (child.Type == "meta")
                    {
                        apple ??= ReadAppleLocation(stream, child);
                    }
                    else if (IsXyz(stream, child))
                    {
                        xyz ??= ReadXyz(stream, child);
                    }
                }
            }
        }

        return (apple ?? xyz) is { } c ? new GeoReading(c, GeoSource.Video) : null;
    }

    /// <summary>Der Typ „©xyz“ beginnt mit dem Byte 0xA9 (kein ASCII) — deshalb roh vergleichen.</summary>
    private static bool IsXyz(Stream stream, MediaDateReader.Box box)
    {
        Span<byte> type = stackalloc byte[4];
        return MediaDateReader.ReadAt(stream, box.Start + 4, type) && type[0] == 0xA9 && type[1] == (byte)'x' && type[2] == (byte)'y' && type[3] == (byte)'z';
    }

    /// <summary>©xyz: 2 Byte Länge, 2 Byte Sprache, dann ISO-6709-Text („+48.2082+016.3738/“).</summary>
    private static GeoCoordinate? ReadXyz(Stream stream, MediaDateReader.Box box)
    {
        Span<byte> head = stackalloc byte[4];
        if (!MediaDateReader.ReadAt(stream, box.DataStart, head))
        {
            return null;
        }

        var length = BinaryPrimitives.ReadUInt16BigEndian(head);
        if (length == 0 || length > 128 || box.DataStart + 4 + length > box.End)
        {
            return null;
        }

        var text = new byte[length];
        return MediaDateReader.ReadAt(stream, box.DataStart + 4, text) ? ParseIso6709(Encoding.UTF8.GetString(text)) : null;
    }

    /// <summary>QuickTime-Metadaten (<c>keys</c> + <c>ilst</c>) mit dem Schlüssel „com.apple.quicktime.location.ISO6709“.</summary>
    private static GeoCoordinate? ReadAppleLocation(Stream stream, MediaDateReader.Box meta)
    {
        Span<byte> probe = stackalloc byte[12];
        if (!MediaDateReader.ReadAt(stream, meta.DataStart, probe))
        {
            return null;
        }

        var children = MediaDateReader.Ascii(probe[4..8]) == "hdlr" ? meta.DataStart : meta.DataStart + 4;
        int? keyIndex = null;
        MediaDateReader.Box? ilst = null;
        foreach (var box in MediaDateReader.Boxes(stream, children, meta.End))
        {
            if (box.Type == "keys")
            {
                keyIndex = MediaDateReader.FindKeyIndex(stream, box, AppleLocationKey);
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
        foreach (var item in MediaDateReader.Boxes(stream, list.DataStart, list.End))
        {
            if (!MediaDateReader.ReadAt(stream, item.Start + 4, number) || BinaryPrimitives.ReadUInt32BigEndian(number) != (uint)index)
            {
                continue;
            }

            foreach (var data in MediaDateReader.Boxes(stream, item.DataStart, item.End))
            {
                if (data.Type != "data" || data.End - data.DataStart <= 8 || data.End - data.DataStart > 256)
                {
                    continue;
                }

                var bytes = new byte[data.End - data.DataStart - 8];
                if (MediaDateReader.ReadAt(stream, data.DataStart + 8, bytes) && ParseIso6709(Encoding.UTF8.GetString(bytes)) is { } value)
                {
                    return value;
                }
            }
        }

        return null;
    }

    [GeneratedRegex(@"^([+-]\d+(?:\.\d+)?)([+-]\d+(?:\.\d+)?)([+-]\d+(?:\.\d+)?)?(?:CRS[^/]*)?/?$", RegexOptions.CultureInvariant)]
    private static partial Regex Iso6709Regex();

    /// <summary>
    /// ISO 6709 („+48.2082+016.3738+150.000/“): Breite ±DD[.D], ±DDMM[.M] oder ±DDMMSS[.S], Länge mit drei Grad-Stellen,
    /// optional Höhe; null bei Ungültigem.
    /// </summary>
    public static GeoCoordinate? ParseIso6709(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = Iso6709Regex().Match(text.Trim().TrimEnd('\0'));
        if (!match.Success)
        {
            return null;
        }

        var lat = ParsePart(match.Groups[1].Value, degreeDigits: 2);
        var lon = ParsePart(match.Groups[2].Value, degreeDigits: 3);
        if (lat is null || lon is null)
        {
            return null;
        }

        double? altitude = match.Groups[3].Success && double.TryParse(match.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var alt) ? alt : null;
        var coordinate = new GeoCoordinate(lat.Value, lon.Value, altitude);
        return coordinate.IsValid ? coordinate : null;
    }

    private static double? ParsePart(string part, int degreeDigits)
    {
        var sign = part[0] == '-' ? -1 : 1;
        var body = part[1..];
        var dot = body.IndexOf('.');
        var integerDigits = dot < 0 ? body.Length : dot;
        if (!double.TryParse(body, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return null;
        }

        double degrees;
        if (integerDigits <= degreeDigits)
        {
            degrees = number;
        }
        else if (integerDigits == degreeDigits + 2)
        {
            var d = Math.Floor(number / 100);
            degrees = d + ((number - (d * 100)) / 60);
        }
        else if (integerDigits == degreeDigits + 4)
        {
            var d = Math.Floor(number / 10000);
            var m = Math.Floor((number - (d * 10000)) / 100);
            degrees = d + (m / 60) + ((number - (d * 10000) - (m * 100)) / 3600);
        }
        else
        {
            return null;
        }

        return sign * degrees;
    }
}
