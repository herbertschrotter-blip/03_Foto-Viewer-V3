using System.Buffers.Binary;
using System.Text;
using Lumora.Domain.Enums;

namespace Lumora.Services.Places.Tracks;

/// <summary>
/// Minimaler, eigener Leser für Garmin-FIT-Dateien (ADR-0062 — bewusst ohne das FIT SDK, dessen Lizenz die Weitergabe
/// mit der portablen Version ausschließt). Liest nach der öffentlichen Protokollbeschreibung: Dateikopf (12/14 Byte,
/// „.FIT“), Definitions- und Datennachrichten (beide Byte-Reihenfolgen, Entwicklerfelder werden übersprungen), komprimierte
/// Zeitstempel-Köpfe und verkettete Dateien. Ausgewertet werden nur <c>record</c> (20: timestamp 253, position_lat/long
/// 0/1 in Semikreisen, altitude 2 bzw. enhanced_altitude 78 mit Skala 5 und Versatz 500), <c>session</c>/<c>sport</c>
/// (Sportart), <c>file_id</c> und <c>device_info</c> (Gerät). Prüfsummen werden nicht geprüft; Kaputtes am Ende kostet nur
/// die restlichen Punkte.
/// </summary>
public static class FitTrackReader
{
    /// <summary>FIT-Zeitstempel zählen ab 1989-12-31 00:00:00 UTC.</summary>
    public static readonly DateTime FitEpoch = new(1989, 12, 31, 0, 0, 0, DateTimeKind.Utc);

    public const long FitEpochUnix = 631_065_600;

    /// <summary>Größte gelesene FIT-Datei (Schutz bei kaputten bzw. fremden Dateien).</summary>
    public const int MaxFileBytes = 64 * 1024 * 1024;

    private const double SemicircleToDegrees = 180.0 / 2147483648.0;
    private const ushort MsgFileId = 0;
    private const ushort MsgSport = 12;
    private const ushort MsgSession = 18;
    private const ushort MsgRecord = 20;
    private const ushort MsgDeviceInfo = 23;
    private const byte FieldTimestamp = 253;

    private sealed record FieldDef(byte Number, byte Size, byte BaseType);

    private sealed record Definition(ushort Global, bool BigEndian, FieldDef[] Fields, int DevBytes);

    /// <summary>„.FIT“ an Byte 8 des Kopfs.</summary>
    public static bool LooksLikeFit(ReadOnlySpan<byte> head)
        => head.Length >= 12 && head[0] is 12 or 14 && head[8] == (byte)'.' && head[9] == (byte)'F' && head[10] == (byte)'I' && head[11] == (byte)'T';

    /// <summary>Aktivität aus einer FIT-Datei; null, wenn sie keine Positionen enthält oder kein FIT ist.</summary>
    public static ParsedTrack? Read(Stream stream)
    {
        using var buffer = new MemoryStream();
        var limited = new byte[81920];
        int n;
        while ((n = stream.Read(limited, 0, limited.Length)) > 0)
        {
            buffer.Write(limited, 0, n);
            if (buffer.Length > MaxFileBytes)
            {
                throw new InvalidDataException("FIT-Datei zu groß.");
            }
        }

        return Read(buffer.ToArray());
    }

    /// <summary>Aktivität aus den Bytes einer FIT-Datei; null ohne Positionen; <see cref="InvalidDataException"/> bei keinem FIT.</summary>
    public static ParsedTrack? Read(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!LooksLikeFit(data))
        {
            throw new InvalidDataException("Keine FIT-Datei.");
        }

        var points = new List<TrackPoint>();
        int? sport = null;
        string? sportName = null;
        string? productName = null;
        int? manufacturer = null;
        int? product = null;
        var offset = 0;
        while (offset + 12 <= data.Length && LooksLikeFit(data.AsSpan(offset)))
        {
            var headerSize = data[offset];
            if (offset + headerSize > data.Length)
            {
                break; // abgeschnittener Kopf einer verketteten Datei
            }

            var dataSize = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4)), (uint)(data.Length - offset - headerSize));
            var end = offset + headerSize + dataSize;
            ReadRecords(data, offset + headerSize, end, points, ref sport, ref sportName, ref productName, ref manufacturer, ref product);
            offset = end + 2; // CRC
        }

        var normalized = ParsedTrack.Normalize(points);
        if (normalized.Count == 0)
        {
            return null;
        }

        var sportText = sport is { } s ? TrackSportNames.FromFit(s) : null;
        if (sportName is not null && (sportText is null || sportText == "Aktivität"))
        {
            sportText = sportName;
        }

        return new ParsedTrack(normalized, sportText, DeviceName(productName, manufacturer, product), null, GpsTrackSource.Fit);
    }

    private static void ReadRecords(
        byte[] data,
        int start,
        int end,
        List<TrackPoint> points,
        ref int? sport,
        ref string? sportName,
        ref string? productName,
        ref int? manufacturer,
        ref int? product)
    {
        var definitions = new Definition?[16];
        long lastTimestamp = 0;
        var pos = start;
        while (pos < end)
        {
            var header = data[pos++];
            if ((header & 0x80) != 0)
            {
                // Komprimierter Zeitstempel: Datennachricht mit 5-Bit-Versatz zum letzten Zeitstempel
                var local = (header >> 5) & 0x03;
                var timeOffset = header & 0x1F;
                if (definitions[local] is not { } compressed)
                {
                    return;
                }

                var ts = (lastTimestamp & ~0x1FL) + timeOffset;
                if (timeOffset < (lastTimestamp & 0x1F))
                {
                    ts += 0x20;
                }

                lastTimestamp = ts;
                if (!ReadData(data, ref pos, end, compressed, ts, points, ref lastTimestamp, ref sport, ref sportName, ref productName, ref manufacturer, ref product))
                {
                    return;
                }

                continue;
            }

            var localType = header & 0x0F;
            if ((header & 0x40) != 0)
            {
                var hasDev = (header & 0x20) != 0;
                if (pos + 5 > end)
                {
                    return;
                }

                var bigEndian = data[pos + 1] == 1;
                var global = bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos + 2)) : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos + 2));
                var count = data[pos + 4];
                pos += 5;
                if (pos + (count * 3) > end)
                {
                    return;
                }

                var fields = new FieldDef[count];
                for (var i = 0; i < count; i++)
                {
                    fields[i] = new FieldDef(data[pos], data[pos + 1], data[pos + 2]);
                    pos += 3;
                }

                var devBytes = 0;
                if (hasDev)
                {
                    if (pos >= end)
                    {
                        return;
                    }

                    var devCount = data[pos++];
                    if (pos + (devCount * 3) > end)
                    {
                        return;
                    }

                    for (var i = 0; i < devCount; i++)
                    {
                        devBytes += data[pos + 1];
                        pos += 3;
                    }
                }

                definitions[localType] = new Definition(global, bigEndian, fields, devBytes);
                continue;
            }

            if (definitions[localType] is not { } definition)
            {
                return;
            }

            if (!ReadData(data, ref pos, end, definition, null, points, ref lastTimestamp, ref sport, ref sportName, ref productName, ref manufacturer, ref product))
            {
                return;
            }
        }
    }

    private static bool ReadData(
        byte[] data,
        ref int pos,
        int end,
        Definition def,
        long? compressedTimestamp,
        List<TrackPoint> points,
        ref long lastTimestamp,
        ref int? sport,
        ref string? sportName,
        ref string? productName,
        ref int? manufacturer,
        ref int? product)
    {
        long? timestamp = compressedTimestamp;
        int? lat = null, lon = null;
        double? altitude = null, enhancedAltitude = null;
        int? deviceIndex = null;
        int? fileManufacturer = null, fileProduct = null;
        int? msgSport = null;
        string? msgName = null, msgProductName = null;
        foreach (var field in def.Fields)
        {
            if (pos + field.Size > end)
            {
                return false;
            }

            var span = data.AsSpan(pos, field.Size);
            pos += field.Size;
            if (field.Number == FieldTimestamp && field.Size == 4)
            {
                var v = U32(span, def.BigEndian);
                if (v != uint.MaxValue)
                {
                    timestamp = v;
                    lastTimestamp = v;
                }

                continue;
            }

            switch (def.Global)
            {
                case MsgRecord:
                    switch (field.Number)
                    {
                        case 0 when field.Size == 4:
                            lat = S32(span, def.BigEndian) is var la && la != int.MaxValue ? la : null;
                            break;
                        case 1 when field.Size == 4:
                            lon = S32(span, def.BigEndian) is var lo && lo != int.MaxValue ? lo : null;
                            break;
                        case 2 when field.Size == 2:
                            altitude = U16(span, def.BigEndian) is var a && a != ushort.MaxValue ? (a / 5.0) - 500 : null;
                            break;
                        case 78 when field.Size == 4:
                            enhancedAltitude = U32(span, def.BigEndian) is var ea && ea != uint.MaxValue ? (ea / 5.0) - 500 : null;
                            break;
                    }

                    break;
                case MsgSession when field.Number == 5 && field.Size >= 1:
                case MsgSport when field.Number == 0 && field.Size >= 1:
                    msgSport = span[0] == 0xFF ? null : span[0];
                    break;
                case MsgSport when field.Number == 3:
                    msgName = Text(span);
                    break;
                case MsgFileId when field.Number == 1 && field.Size == 2:
                    fileManufacturer = U16(span, def.BigEndian) is var fm && fm != ushort.MaxValue ? fm : null;
                    break;
                case MsgFileId when field.Number == 2 && field.Size == 2:
                    fileProduct = U16(span, def.BigEndian) is var fp && fp != ushort.MaxValue ? fp : null;
                    break;
                case MsgDeviceInfo when field.Number == 0 && field.Size == 1:
                    deviceIndex = span[0];
                    break;
                case MsgDeviceInfo when field.Number == 27:
                    msgProductName = Text(span);
                    break;
            }
        }

        pos += def.DevBytes;
        if (pos > end)
        {
            return false;
        }

        switch (def.Global)
        {
            case MsgRecord when lat is { } la2 && lon is { } lo2 && timestamp is { } t:
                points.Add(new TrackPoint(t + FitEpochUnix, la2 * SemicircleToDegrees, lo2 * SemicircleToDegrees, enhancedAltitude ?? altitude));
                break;
            case MsgSession or MsgSport when msgSport is { } s:
                sport ??= s; // erste Session bestimmt die Sportart (Multisport: die erste Disziplin)
                if (msgName is not null)
                {
                    sportName ??= msgName;
                }

                break;
            case MsgSport when msgName is not null:
                sportName ??= msgName;
                break;
            case MsgFileId:
                manufacturer ??= fileManufacturer;
                product ??= fileProduct;
                break;
            case MsgDeviceInfo when msgProductName is not null && (deviceIndex is null or 0):
                productName ??= msgProductName;
                break;
        }

        return true;
    }

    /// <summary>
    /// Gerätename: <c>product_name</c> aus <c>device_info</c>, sonst bekannte Garmin-Produktnummern (Fenix-Reihe), sonst
    /// „Garmin-Gerät 1234“.
    /// </summary>
    public static string? DeviceName(string? productName, int? manufacturer, int? product)
    {
        if (!string.IsNullOrWhiteSpace(productName))
        {
            return productName.Trim();
        }

        if (manufacturer is 1 && product is { } p)
        {
            return GarminProducts.TryGetValue(p, out var name) ? name : $"Garmin-Gerät {p}";
        }

        return manufacturer is 1 ? "Garmin" : null;
    }

    /// <summary>Häufige Garmin-Produktnummern (FIT-Profil <c>garmin_product</c>) — nur die Fenix-Familie, sonst Nummer.</summary>
    private static readonly Dictionary<int, string> GarminProducts = new()
    {
        [1551] = "Fenix",
        [1967] = "Fenix 2",
        [2050] = "Fenix 3",
        [2413] = "Fenix 3 HR",
        [2432] = "Fenix 3 Chronos",
        [2544] = "Fenix 5S",
        [2604] = "Fenix 5X",
        [2697] = "Fenix 5",
        [3110] = "Fenix 5S Plus",
        [3111] = "Fenix 5 Plus",
        [3112] = "Fenix 5X Plus",
        [3288] = "Fenix 6S Sport",
        [3289] = "Fenix 6S",
        [3290] = "Fenix 6 Sport",
        [3291] = "Fenix 6",
        [3292] = "Fenix 6X",
        [3905] = "Fenix 7S",
        [3906] = "Fenix 7",
        [3907] = "Fenix 7X",
    };

    private static string? Text(ReadOnlySpan<byte> span)
    {
        var zero = span.IndexOf((byte)0);
        var text = Encoding.UTF8.GetString(zero >= 0 ? span[..zero] : span).Trim();
        return text.Length == 0 ? null : text.Length > 64 ? text[..64] : text;
    }

    private static ushort U16(ReadOnlySpan<byte> b, bool big) => big ? BinaryPrimitives.ReadUInt16BigEndian(b) : BinaryPrimitives.ReadUInt16LittleEndian(b);

    private static uint U32(ReadOnlySpan<byte> b, bool big) => big ? BinaryPrimitives.ReadUInt32BigEndian(b) : BinaryPrimitives.ReadUInt32LittleEndian(b);

    private static int S32(ReadOnlySpan<byte> b, bool big) => big ? BinaryPrimitives.ReadInt32BigEndian(b) : BinaryPrimitives.ReadInt32LittleEndian(b);
}
