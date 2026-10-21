using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Lumora.Services.Metadata;

/// <summary>
/// GPS in XMP-Begleitdateien (ADR-0050): Lesen (<c>exif:GPSLatitude</c>/<c>exif:GPSLongitude</c> als Attribut oder Element,
/// XMP-Schreibweise „48,12.4938N“ bzw. „48,12,29N“) und Schreiben (Attribute an der ersten <c>rdf:Description</c>,
/// übrige Inhalte bleiben). Begleitdatei = <c>name.ext.xmp</c> (digiKam, darktable) oder <c>name.xmp</c> (Lightroom);
/// neu angelegt wird immer <c>name.ext.xmp</c> — eindeutig auch bei RAW+JPEG-Paaren gleichen Namens.
/// </summary>
public static class XmpGpsSidecar
{
    public const string ExifNamespace = "http://ns.adobe.com/exif/1.0/";
    private const string RdfNamespace = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private const string AdobeMetaNamespace = "adobe:ns:meta/";
    private const int MaxSidecarBytes = 4 * 1024 * 1024;

    private static readonly XNamespace Exif = ExifNamespace;
    private static readonly XNamespace Rdf = RdfNamespace;
    private static readonly string[] GpsNames = ["GPSLatitude", "GPSLongitude", "GPSAltitude", "GPSAltitudeRef", "GPSVersionID"];

    /// <summary>Begleitdatei, die neu angelegt wird: <c>IMG_1234.CR2.xmp</c>.</summary>
    public static string PreferredPath(string mediaPath) => mediaPath + ".xmp";

    /// <summary>
    /// Vorhandene Begleitdatei (zuerst <c>name.ext.xmp</c>, dann <c>name.xmp</c>) oder null. Für JPEGs zählt nur
    /// <c>name.jpg.xmp</c>: <c>name.xmp</c> gehört bei RAW+JPEG-Paaren (Lightroom) zum RAW.
    /// </summary>
    public static string? FindExisting(string mediaPath)
    {
        var preferred = PreferredPath(mediaPath);
        if (File.Exists(preferred))
        {
            return preferred;
        }

        if (Path.GetExtension(mediaPath).ToLowerInvariant() is ".jpg" or ".jpeg" or ".jpe" or ".jfif")
        {
            return null;
        }

        var lightroom = Path.ChangeExtension(mediaPath, ".xmp");
        return !string.Equals(lightroom, mediaPath, StringComparison.OrdinalIgnoreCase) && File.Exists(lightroom) ? lightroom : null;
    }

    /// <summary>True, wenn die XMP-Daten GPS-Angaben enthalten (Attribut- oder Element-Form).</summary>
    public static bool ContainsGps(byte[] xmp)
        => Parse(xmp) is { } doc
           && doc.Descendants().Any(e => GpsNames.Any(n => e.Name == Exif + n || e.Attribute(Exif + n) is not null));

    /// <summary>Koordinate aus der Begleitdatei oder null (keine, nicht lesbar, zu groß).</summary>
    public static GeoCoordinate? Read(string xmpPath)
    {
        try
        {
            var info = new FileInfo(xmpPath);
            if (!info.Exists || info.Length > MaxSidecarBytes)
            {
                return null;
            }

            return ReadText(File.ReadAllBytes(xmpPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Koordinate aus XMP-Bytes oder null.</summary>
    public static GeoCoordinate? ReadText(byte[] xmp)
    {
        if (Parse(xmp) is not { } doc)
        {
            return null;
        }

        string? Value(string name)
            => doc.Descendants().Select(e => (string?)e.Attribute(Exif + name) ?? (e.Name == Exif + name ? e.Value : null)).FirstOrDefault(v => v is not null);

        var lat = ParseCoordinate(Value("GPSLatitude"));
        var lon = ParseCoordinate(Value("GPSLongitude"));
        if (lat is null || lon is null)
        {
            return null;
        }

        double? altitude = null;
        if (ParseRational(Value("GPSAltitude")) is { } alt)
        {
            altitude = Value("GPSAltitudeRef")?.Trim() == "1" ? -alt : alt;
        }

        var coordinate = new GeoCoordinate(lat.Value, lon.Value, altitude);
        return coordinate.IsValid ? coordinate : null;
    }

    /// <summary>
    /// Neue Bytes der Begleitdatei: <paramref name="existing"/> (null = neu anlegen) mit gesetzter
    /// (<paramref name="value"/>) bzw. entfernter (null) Koordinate. Null, wenn nichts zu schreiben ist (keine Datei und
    /// nichts zu setzen). Wirft <see cref="InvalidDataException"/>, wenn die vorhandene Datei kein lesbares XMP ist — sie
    /// wird dann nicht überschrieben.
    /// </summary>
    public static byte[]? Upsert(byte[]? existing, GeoCoordinate? value)
    {
        if (existing is null)
        {
            return value is { } v ? Encoding.UTF8.GetBytes(NewPacket(v)) : null;
        }

        var doc = Parse(existing) ?? throw new InvalidDataException("Die XMP-Begleitdatei ist kein lesbares XMP.");
        var description = doc.Descendants(Rdf + "Description").FirstOrDefault();
        if (description is null)
        {
            var rdf = doc.Descendants(Rdf + "RDF").FirstOrDefault() ?? throw new InvalidDataException("Die XMP-Begleitdatei enthält kein rdf:RDF.");
            description = new XElement(Rdf + "Description", new XAttribute(Rdf + "about", string.Empty));
            rdf.Add(description);
        }

        // Alte GPS-Angaben in allen Beschreibungen entfernen (Attribut- und Element-Form).
        foreach (var element in doc.Descendants().ToList())
        {
            foreach (var name in GpsNames)
            {
                element.Attribute(Exif + name)?.Remove();
                if (element.Name == Exif + name)
                {
                    element.Remove();
                }
            }
        }

        if (value is { } coordinate)
        {
            if (description.GetPrefixOfNamespace(Exif) is null)
            {
                description.Add(new XAttribute(XNamespace.Xmlns + "exif", ExifNamespace));
            }

            description.SetAttributeValue(Exif + "GPSVersionID", "2.3.0.0");
            description.SetAttributeValue(Exif + "GPSLatitude", FormatCoordinate(coordinate.Latitude, latitude: true));
            description.SetAttributeValue(Exif + "GPSLongitude", FormatCoordinate(coordinate.Longitude, latitude: false));
        }

        return Serialize(doc, existing);
    }

    /// <summary>XMP-Schreibweise „DDD,MM.mmmmmmK“ (Grad, Dezimalminuten, Himmelsrichtung).</summary>
    public static string FormatCoordinate(double value, bool latitude)
    {
        var abs = Math.Abs(value);
        var degrees = Math.Floor(abs);
        var minutes = Math.Round((abs - degrees) * 60, 6);
        if (minutes >= 60)
        {
            degrees += 1;
            minutes = 0;
        }

        var reference = latitude ? (value < 0 ? 'S' : 'N') : (value < 0 ? 'W' : 'E');
        return string.Create(CultureInfo.InvariantCulture, $"{degrees:0},{minutes:0.000000}{reference}");
    }

    /// <summary>„48,12.4938N“, „48,12,29.5N“ oder Dezimalgrad („48.2082“); null bei Ungültigem.</summary>
    public static double? ParseCoordinate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var t = text.Trim();
        var sign = 1;
        var last = char.ToUpperInvariant(t[^1]);
        if (last is 'N' or 'S' or 'E' or 'W')
        {
            sign = last is 'S' or 'W' ? -1 : 1;
            t = t[..^1];
        }

        var parts = t.Split(',');
        double result;
        switch (parts.Length)
        {
            case 1 when double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var decimalDegrees):
                result = decimalDegrees;
                break;
            case 2 when double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var d2)
                        && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var m2) && m2 is >= 0 and < 60:
                result = d2 + (m2 / 60);
                break;
            case 3 when double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var d3)
                        && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var m3) && m3 is >= 0 and < 60
                        && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var s3) && s3 is >= 0 and < 60:
                result = d3 + (m3 / 60) + (s3 / 3600);
                break;
            default:
                return null;
        }

        return double.IsFinite(result) ? sign * result : null;
    }

    private static double? ParseRational(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var parts = text.Trim().Split('/');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d != 0)
        {
            return n / d;
        }

        return parts.Length == 1 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static XDocument? Parse(byte[] bytes)
    {
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreWhitespace = false };
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = XmlReader.Create(stream, settings);
            return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static byte[] Serialize(XDocument doc, byte[] original)
    {
        var hadDeclaration = Encoding.UTF8.GetString(original, 0, Math.Min(original.Length, 64)).TrimStart('﻿').StartsWith("<?xml", StringComparison.Ordinal);
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            OmitXmlDeclaration = !hadDeclaration,
            Indent = false,
            NewLineHandling = NewLineHandling.None,
        };
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, settings))
        {
            doc.Save(writer);
        }

        return stream.ToArray();
    }

    private static string NewPacket(GeoCoordinate value)
        => "<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n"
           + $"<x:xmpmeta xmlns:x=\"{AdobeMetaNamespace}\" x:xmptk=\"Lumora\">\n"
           + $" <rdf:RDF xmlns:rdf=\"{RdfNamespace}\">\n"
           + $"  <rdf:Description rdf:about=\"\" xmlns:exif=\"{ExifNamespace}\"\n"
           + "    exif:GPSVersionID=\"2.3.0.0\"\n"
           + $"    exif:GPSLatitude=\"{FormatCoordinate(value.Latitude, latitude: true)}\"\n"
           + $"    exif:GPSLongitude=\"{FormatCoordinate(value.Longitude, latitude: false)}\"/>\n"
           + " </rdf:RDF>\n"
           + "</x:xmpmeta>\n"
           + "<?xpacket end=\"w\"?>\n";
}
