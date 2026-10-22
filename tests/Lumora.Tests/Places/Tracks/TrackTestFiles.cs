using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using Lumora.Services.Places.Tracks;

namespace Lumora.Tests.Places.Tracks;

/// <summary>
/// Synthetische Testdateien für ADR-0062 — keine echten Garmin-Dateien: kleiner TIFF-Baukasten (IFD0 Make/Model, EXIF-IFD
/// mit DateTimeOriginal/OffsetTimeOriginal, GPS-IFD mit beliebigen Tags), FIT-Schreiber nach der öffentlichen
/// Protokollbeschreibung (Definitionen beider Byte-Reihenfolgen, komprimierte Zeitstempel, Entwicklerfelder), GPX/TCX-Text
/// und ZIPs (auch verschachtelt wie der Garmin-Datenexport).
/// </summary>
internal static class TrackTestFiles
{
    // ---- TIFF ----------------------------------------------------------------------------------------------------------

    /// <summary>Ein TIFF-Eintrag: Tag, Typ, Anzahl, Wert-Bytes (Little Endian).</summary>
    public sealed record Entry(ushort Tag, ushort Type, uint Count, byte[] Value);

    public static Entry Ascii(ushort tag, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text + "\0");
        return new Entry(tag, 2, (uint)bytes.Length, bytes);
    }

    public static Entry Rationals(ushort tag, params (uint N, uint D)[] values)
    {
        var bytes = new byte[values.Length * 8];
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 8), values[i].N);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan((i * 8) + 4), values[i].D);
        }

        return new Entry(tag, 5, (uint)values.Length, bytes);
    }

    public static Entry Undefined(ushort tag, byte[] bytes) => new(tag, 7, (uint)bytes.Length, bytes);

    public static Entry Byte(ushort tag, byte value) => new(tag, 1, 1, [value]);

    /// <summary>GPS-Einträge für Breite/Länge (Grad, Minuten, Sekunden × 10 000).</summary>
    public static IEnumerable<Entry> LatLon(double latitude, double longitude)
    {
        (uint, uint)[] Dms(double value)
        {
            value = Math.Abs(value);
            var d = Math.Floor(value);
            var m = Math.Floor((value - d) * 60);
            var s = Math.Round((((value - d) * 60) - m) * 60 * 10000);
            return [((uint)d, 1), ((uint)m, 1), ((uint)s, 10000)];
        }

        yield return Ascii(1, latitude < 0 ? "S" : "N");
        yield return Rationals(2, Dms(latitude));
        yield return Ascii(3, longitude < 0 ? "W" : "E");
        yield return Rationals(4, Dms(longitude));
    }

    /// <summary>TIFF-Block (Little Endian) mit IFD0 → EXIF-IFD und GPS-IFD.</summary>
    public static byte[] Tiff(IEnumerable<Entry> ifd0, IEnumerable<Entry>? exif, IEnumerable<Entry>? gps)
    {
        var ifd0List = ifd0.ToList();
        var exifList = exif?.ToList();
        var gpsList = gps?.ToList();
        if (exifList is not null)
        {
            ifd0List.Add(new Entry(0x8769, 4, 1, new byte[4]));
        }

        if (gpsList is not null)
        {
            ifd0List.Add(new Entry(0x8825, 4, 1, new byte[4]));
        }

        ifd0List = ifd0List.OrderBy(e => e.Tag).ToList();
        static int IfdSize(int count) => 2 + (count * 12) + 4;
        static int DataSize(IEnumerable<Entry> entries) => entries.Where(e => e.Value.Length > 4).Sum(e => e.Value.Length + (e.Value.Length % 2));

        var ifd0Offset = 8;
        var exifOffset = ifd0Offset + IfdSize(ifd0List.Count) + DataSize(ifd0List);
        var gpsOffset = exifOffset + (exifList is null ? 0 : IfdSize(exifList.Count) + DataSize(exifList));
        var buffer = new MemoryStream();
        buffer.Write("II"u8);
        WriteU16(buffer, 42);
        WriteU32(buffer, (uint)ifd0Offset);
        WriteIfd(buffer, ifd0List.Select(e => e.Tag switch
        {
            0x8769 => e with { Value = BitConverter.GetBytes((uint)exifOffset) },
            0x8825 => e with { Value = BitConverter.GetBytes((uint)gpsOffset) },
            _ => e,
        }).ToList(), ifd0Offset);
        if (exifList is not null)
        {
            WriteIfd(buffer, exifList.OrderBy(e => e.Tag).ToList(), exifOffset);
        }

        if (gpsList is not null)
        {
            WriteIfd(buffer, gpsList.OrderBy(e => e.Tag).ToList(), gpsOffset);
        }

        return buffer.ToArray();
    }

    private static void WriteIfd(MemoryStream buffer, List<Entry> entries, int offset)
    {
        var dataOffset = offset + 2 + (entries.Count * 12) + 4;
        WriteU16(buffer, (ushort)entries.Count);
        var data = new MemoryStream();
        foreach (var e in entries)
        {
            WriteU16(buffer, e.Tag);
            WriteU16(buffer, e.Type);
            WriteU32(buffer, e.Count);
            if (e.Value.Length <= 4)
            {
                var inline = new byte[4];
                e.Value.CopyTo(inline, 0);
                buffer.Write(inline);
            }
            else
            {
                WriteU32(buffer, (uint)(dataOffset + data.Length));
                data.Write(e.Value);
                if (e.Value.Length % 2 == 1)
                {
                    data.WriteByte(0);
                }
            }
        }

        WriteU32(buffer, 0);
        buffer.Write(data.ToArray());
    }

    /// <summary>Handy-Foto: Aufnahme „wall“ (Wanduhrzeit) und GPS nach Wunsch.</summary>
    public static byte[] PhoneTiff(
        DateTime wall,
        double latitude,
        double longitude,
        double? altitude = null,
        DateTime? gpsUtc = null,
        string? method = null,
        double? error = null,
        string? offset = null,
        string make = "Samsung",
        string model = "SM-G991B")
    {
        var exif = new List<Entry> { Ascii(0x9003, wall.ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture)) };
        if (offset is not null)
        {
            exif.Add(Ascii(0x9011, offset));
        }

        var gps = LatLon(latitude, longitude).ToList();
        if (altitude is { } alt)
        {
            gps.Add(Byte(5, 0));
            gps.Add(Rationals(6, ((uint)Math.Round(alt * 10), 10)));
        }

        if (gpsUtc is { } t)
        {
            gps.Add(Rationals(7, ((uint)t.Hour, 1), ((uint)t.Minute, 1), ((uint)t.Second, 1)));
            gps.Add(Ascii(29, t.ToString("yyyy:MM:dd", CultureInfo.InvariantCulture)));
        }

        if (method is not null)
        {
            gps.Add(Undefined(27, "ASCII\0\0\0"u8.ToArray().Concat(Encoding.ASCII.GetBytes(method)).ToArray()));
        }

        if (error is { } e)
        {
            gps.Add(Rationals(31, ((uint)Math.Round(e * 10), 10)));
        }

        return Tiff([Ascii(0x010F, make), Ascii(0x0110, model)], exif, gps);
    }

    /// <summary>Foto ohne GPS mit Datum (und optional Kamera).</summary>
    public static byte[] PlainTiff(DateTime wall, string make = "Canon", string model = "Canon EOS 80D", string? offset = null)
    {
        var exif = new List<Entry> { Ascii(0x9003, wall.ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture)) };
        if (offset is not null)
        {
            exif.Add(Ascii(0x9011, offset));
        }

        return Tiff([Ascii(0x010F, make), Ascii(0x0110, model)], exif, null);
    }

    private static void WriteU16(Stream s, ushort v)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, v);
        s.Write(b);
    }

    private static void WriteU32(Stream s, uint v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        s.Write(b);
    }

    // ---- FIT -----------------------------------------------------------------------------------------------------------

    /// <summary>Schreibt eine FIT-Aktivität: file_id, device_info, record je Punkt, session mit Sportart.</summary>
    public sealed class FitBuilder
    {
        private readonly MemoryStream _records = new();

        /// <summary>Definition (lokaler Typ, globale Nachricht, Felder (Nummer, Größe, Basistyp)).</summary>
        public FitBuilder Define(byte local, ushort global, bool bigEndian, (byte Num, byte Size, byte Type)[] fields, (byte Num, byte Size)[]? devFields = null)
        {
            _records.WriteByte((byte)(0x40 | (devFields is null ? 0 : 0x20) | local));
            _records.WriteByte(0);
            _records.WriteByte((byte)(bigEndian ? 1 : 0));
            var g = new byte[2];
            if (bigEndian)
            {
                BinaryPrimitives.WriteUInt16BigEndian(g, global);
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(g, global);
            }

            _records.Write(g);
            _records.WriteByte((byte)fields.Length);
            foreach (var (num, size, type) in fields)
            {
                _records.WriteByte(num);
                _records.WriteByte(size);
                _records.WriteByte(type);
            }

            if (devFields is not null)
            {
                _records.WriteByte((byte)devFields.Length);
                foreach (var (num, size) in devFields)
                {
                    _records.WriteByte(num);
                    _records.WriteByte(size);
                    _records.WriteByte(0);
                }
            }

            return this;
        }

        /// <summary>Datennachricht (normaler Kopf) mit fertigen Feld-Bytes.</summary>
        public FitBuilder Data(byte local, byte[] payload)
        {
            _records.WriteByte(local);
            _records.Write(payload);
            return this;
        }

        /// <summary>Datennachricht mit komprimiertem Zeitstempel (5-Bit-Versatz).</summary>
        public FitBuilder Compressed(byte local, int timeOffset, byte[] payload)
        {
            _records.WriteByte((byte)(0x80 | (local << 5) | (timeOffset & 0x1F)));
            _records.Write(payload);
            return this;
        }

        public byte[] Build()
        {
            var data = _records.ToArray();
            var file = new MemoryStream();
            file.WriteByte(14);
            file.WriteByte(0x20);
            file.Write(BitConverter.GetBytes((ushort)2132));
            file.Write(BitConverter.GetBytes((uint)data.Length));
            file.Write(".FIT"u8);
            file.Write(new byte[2]); // Kopf-CRC (vom Leser nicht geprüft)
            file.Write(data);
            file.Write(new byte[2]); // Datei-CRC
            return file.ToArray();
        }
    }

    public static uint FitTime(DateTime utc) => (uint)(new DateTimeOffset(utc).ToUnixTimeSeconds() - FitTrackReader.FitEpochUnix);

    public static int Semicircles(double degrees) => (int)Math.Round(degrees * (2147483648.0 / 180.0));

    public static byte[] Le(params object[] values)
    {
        var s = new MemoryStream();
        foreach (var v in values)
        {
            switch (v)
            {
                case uint u: s.Write(BitConverter.GetBytes(u)); break;
                case int i: s.Write(BitConverter.GetBytes(i)); break;
                case ushort us: s.Write(BitConverter.GetBytes(us)); break;
                case byte b: s.WriteByte(b); break;
                case byte[] raw: s.Write(raw); break;
                case string text:
                    var bytes = new byte[16];
                    Encoding.UTF8.GetBytes(text).CopyTo(bytes, 0);
                    s.Write(bytes);
                    break;
                default: throw new ArgumentException(v.GetType().Name);
            }
        }

        return s.ToArray();
    }

    /// <summary>
    /// Wanderung als FIT: Punkte im Abstand <paramref name="step"/> entlang einer Linie, Sportart Wandern (17), Gerät per
    /// product_name; jeder zweite Punkt mit komprimiertem Zeitstempel.
    /// </summary>
    public static byte[] FitHike(DateTime startUtc, int points, TimeSpan step, (double Lat, double Lon) from, (double Lat, double Lon) to, string device = "Fenix 7")
    {
        var fit = new FitBuilder()
            .Define(0, 0, false, [(0, 1, 0), (1, 2, 0x84), (2, 2, 0x84), (4, 4, 0x86)])
            .Data(0, Le((byte)4, (ushort)1, (ushort)3906, FitTime(startUtc)))
            .Define(1, 23, false, [(0, 1, 2), (27, 16, 7)])
            .Data(1, Le((byte)0, device))
            .Define(2, 20, false, [(253, 4, 0x86), (0, 4, 0x85), (1, 4, 0x85), (78, 4, 0x86)])
            .Define(3, 20, false, [(0, 4, 0x85), (1, 4, 0x85), (2, 2, 0x84)]);
        for (var i = 0; i < points; i++)
        {
            var f = points == 1 ? 0 : (double)i / (points - 1);
            var lat = from.Lat + ((to.Lat - from.Lat) * f);
            var lon = from.Lon + ((to.Lon - from.Lon) * f);
            var t = startUtc + (step * i);
            var altitude = 1000 + (i * 2.0);
            if (i % 2 == 1 && step <= TimeSpan.FromSeconds(31))
            {
                // komprimierter Zeitstempel: 5 Bit der Sekunden seit 1989
                fit.Compressed(3, (int)(FitTime(t) & 0x1F), Le(Semicircles(lat), Semicircles(lon), (ushort)Math.Round((altitude + 500) * 5)));
            }
            else
            {
                fit.Data(2, Le(FitTime(t), Semicircles(lat), Semicircles(lon), (uint)Math.Round((altitude + 500) * 5)));
            }
        }

        fit.Define(4, 18, false, [(253, 4, 0x86), (5, 1, 0), (6, 1, 0)]).Data(4, Le(FitTime(startUtc + (step * points)), (byte)17, (byte)0));
        return fit.Build();
    }

    // ---- GPX / TCX / ZIP -------------------------------------------------------------------------------------------------

    public static string Gpx(params (string Name, string Type, (DateTime Utc, double Lat, double Lon, double? Ele)[] Points)[] tracks)
    {
        var sb = new StringBuilder("""<?xml version="1.0" encoding="UTF-8"?><gpx version="1.1" creator="Lumora-Test" xmlns="http://www.topografix.com/GPX/1/1">""");
        foreach (var (name, type, points) in tracks)
        {
            sb.Append($"<trk><name>{name}</name><type>{type}</type><trkseg>");
            foreach (var (utc, lat, lon, ele) in points)
            {
                sb.Append(CultureInfo.InvariantCulture, $"<trkpt lat=\"{lat}\" lon=\"{lon}\">");
                if (ele is { } e)
                {
                    sb.Append(CultureInfo.InvariantCulture, $"<ele>{e}</ele>");
                }

                sb.Append(CultureInfo.InvariantCulture, $"<time>{utc:yyyy-MM-ddTHH:mm:ssZ}</time></trkpt>");
            }

            sb.Append("</trkseg></trk>");
        }

        return sb.Append("</gpx>").ToString();
    }

    public static byte[] Zip(params (string Name, byte[] Content)[] entries)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(content);
            }
        }

        return memory.ToArray();
    }
}
