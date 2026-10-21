namespace Lumora.Services.Metadata;

/// <summary>Liest die Koordinate einer Datei (austauschbar für Tests).</summary>
public interface IGeoLocationReader
{
    /// <summary>Koordinate samt Herkunft oder null (keine Koordinate, nicht lesbar).</summary>
    GeoReading? Read(string path);
}

/// <summary>
/// Rangfolge (ADR-0050): eine XMP-Begleitdatei (<c>name.ext.xmp</c> bzw. <c>name.xmp</c>) mit GPS gewinnt — dorthin
/// schreiben Lumora (RAW, HEIC, TIFF, PNG, WebP) und Programme wie Lightroom/digiKam; sonst EXIF bzw. Video-Metadaten
/// der Datei selbst (<see cref="MediaGpsReader"/>).
/// </summary>
public sealed class GeoLocationReader : IGeoLocationReader
{
    public GeoReading? Read(string path)
    {
        if (XmpGpsSidecar.FindExisting(path) is { } sidecar && XmpGpsSidecar.Read(sidecar) is { } fromXmp)
        {
            return new GeoReading(fromXmp, Domain.Enums.GeoSource.Exif);
        }

        return MediaGpsReader.Read(path);
    }
}
