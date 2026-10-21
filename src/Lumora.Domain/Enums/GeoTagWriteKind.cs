namespace Lumora.Domain.Enums;

/// <summary>Was „Ort setzen…“ für eine Datei geschrieben hat (ADR-0050, Spalte <c>GeoTagUndo.WriteKind</c>).</summary>
public enum GeoTagWriteKind
{
    /// <summary>Nur der Katalog (Videos; Dateien, die nicht geschrieben werden konnten, zählen als Fehler, nicht hierzu).</summary>
    CatalogOnly = 0,

    /// <summary>JPEG: APP1-„Exif“-Segment ersetzt bzw. eingefügt, Bilddaten unverändert.</summary>
    JpegExif = 1,

    /// <summary>XMP-Begleitdatei (<c>name.ext.xmp</c>) angelegt oder ergänzt (RAW, HEIC, TIFF, PNG, WebP).</summary>
    XmpSidecar = 2,
}
