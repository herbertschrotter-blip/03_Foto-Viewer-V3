using System.Buffers.Binary;
using System.Text;

namespace Lumora.Tests.Places;

/// <summary>
/// Synthetische Dateien mit GPS für die Orte-Tests (ADR-0050): TIFF-Block mit IFD0 → EXIF-IFD (Aufnahmedatum) und
/// GPS-IFD (Breite/Länge als drei RATIONAL samt Referenz, optional Höhe), außerdem MP4/MOV mit <c>udta/©xyz</c> bzw. Apple
/// <c>com.apple.quicktime.location.ISO6709</c>. Container (JPEG, PNG, WebP, HEIC) aus <c>SyntheticMediaFiles</c>.
/// </summary>
internal static class SyntheticGpsFiles
{
    /// <summary>TIFF-Block mit GPS (und einem Datum im EXIF-IFD, damit IFD0 zwei Einträge hat).</summary>
    public static byte[] Tiff(double latitude, double longitude, bool bigEndian = false, double? altitude = null, bool withMakerNoteLikeData = false)
    {
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

        void Rational(uint n, uint d)
        {
            U32(n);
            U32(d);
        }

        buffer.AddRange(bigEndian ? "MM"u8.ToArray() : "II"u8.ToArray());
        U16(42);
        U32(8);

        // IFD0: ExifIFD (0x8769), GPS (0x8825)
        const uint ifd0 = 8;
        const uint ifd0Size = 2 + (2 * 12) + 4;
        const uint exifIfd = ifd0 + ifd0Size; // 1 Eintrag: DateTimeOriginal (20 Byte ASCII)
        const uint exifIfdSize = 2 + 12 + 4;
        const uint dateValue = exifIfd + exifIfdSize;
        const uint gpsIfd = dateValue + 20;
        var gpsCount = (ushort)(altitude is null ? 4 : 6);
        var gpsIfdSize = (uint)(2 + (gpsCount * 12) + 4);
        var latValue = gpsIfd + gpsIfdSize;
        var lonValue = latValue + 24;
        var altValue = lonValue + 24;

        U16(2);
        U16(0x8769); U16(4); U32(1); U32(exifIfd);
        U16(0x8825); U16(4); U32(1); U32(gpsIfd);
        U32(0);

        U16(1);
        U16(0x9003); U16(2); U32(20); U32(dateValue);
        U32(0);
        buffer.AddRange(Encoding.ASCII.GetBytes("2024:07:12 15:30:12\0"));

        U16(gpsCount);
        U16(1); U16(2); U32(2); buffer.AddRange(new[] { (byte)(latitude < 0 ? 'S' : 'N'), (byte)0, (byte)0, (byte)0 });
        U16(2); U16(5); U32(3); U32(latValue);
        U16(3); U16(2); U32(2); buffer.AddRange(new[] { (byte)(longitude < 0 ? 'W' : 'E'), (byte)0, (byte)0, (byte)0 });
        U16(4); U16(5); U32(3); U32(lonValue);
        if (altitude is { } alt)
        {
            U16(5); U16(1); U32(1); buffer.AddRange(new byte[] { (byte)(alt < 0 ? 1 : 0), 0, 0, 0 });
            U16(6); U16(5); U32(1); U32(altValue);
        }

        U32(0);
        foreach (var value in new[] { Math.Abs(latitude), Math.Abs(longitude) })
        {
            var d = Math.Floor(value);
            var m = Math.Floor((value - d) * 60);
            var s = Math.Round((((value - d) * 60) - m) * 60 * 10000);
            Rational((uint)d, 1);
            Rational((uint)m, 1);
            Rational((uint)s, 10000);
        }

        if (altitude is { } a)
        {
            Rational((uint)Math.Round(Math.Abs(a) * 10), 10);
        }

        if (withMakerNoteLikeData)
        {
            buffer.AddRange(Enumerable.Range(0, 512).Select(i => (byte)(i * 7)));
        }

        return buffer.ToArray();
    }

    /// <summary>MP4 mit <c>moov/udta/©xyz</c> („+48.2082+016.3738/“).</summary>
    public static byte[] Mp4WithXyz(string iso6709)
    {
        var text = Encoding.UTF8.GetBytes(iso6709);
        var xyzBody = BE16((ushort)text.Length).Concat(BE16(0x15C7)).Concat(text).ToArray();
        var xyz = BE32((uint)(8 + xyzBody.Length)).Concat(new byte[] { 0xA9, (byte)'x', (byte)'y', (byte)'z' }).Concat(xyzBody).ToArray();
        var udta = Box("udta", xyz);
        return Movie(udta);
    }

    /// <summary>MOV mit Apple-Schlüssel <c>com.apple.quicktime.location.ISO6709</c> in <c>moov/meta</c>.</summary>
    public static byte[] MovWithAppleLocation(string iso6709)
    {
        const string key = "com.apple.quicktime.location.ISO6709";
        var keys = FullBox("keys", 0, BE32(1)
            .Concat(BE32((uint)(8 + key.Length))).Concat("mdta"u8.ToArray()).Concat(Encoding.ASCII.GetBytes(key))
            .ToArray());
        var data = Box("data", BE32(1).Concat(BE32(0)).Concat(Encoding.UTF8.GetBytes(iso6709)).ToArray());
        var item = BE32((uint)(8 + data.Length)).Concat(BE32(1)).Concat(data).ToArray();
        var ilst = Box("ilst", item);
        var hdlr = FullBox("hdlr", 0, BE32(0).Concat("mdta"u8.ToArray()).Concat(new byte[13]).ToArray());
        var meta = Box("meta", hdlr.Concat(keys).Concat(ilst).ToArray());
        return Movie(meta);
    }

    private static byte[] Movie(byte[] extra)
    {
        var ftyp = Box("ftyp", "isom"u8.ToArray().Concat(BE32(512)).Concat("isomiso2mp41"u8.ToArray()).ToArray());
        var mvhd = FullBox("mvhd", 0, BE32(0).Concat(BE32(0)).Concat(BE32(1000)).Concat(BE32(5000)).Concat(new byte[80]).ToArray());
        var moov = Box("moov", mvhd.Concat(extra).ToArray());
        return ftyp.Concat(moov).Concat(Box("mdat", new byte[64])).ToArray();
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
}
