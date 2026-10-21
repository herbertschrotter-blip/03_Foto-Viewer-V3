using Lumora.Domain.Enums;

namespace Lumora.Domain.Entities;

/// <summary>
/// Rückgängig-Speicher für „Ort setzen…“/„Ort entfernen“ (ADR-0050), 1:1 zu einer <see cref="FileOperation"/> vom Typ
/// <see cref="OperationType.SetGeoLocation"/>: alte Katalogwerte, neue Koordinaten (für „zuletzt verwendete Orte“) und —
/// falls die App eine Datei geschrieben hat — die alten Bytes (EXIF-APP1-Segment des JPEG bzw. alte XMP-Begleitdatei)
/// samt Prüfsumme des geschriebenen Stands. Tabelle <c>GeoTagUndo</c>.
/// </summary>
public sealed class GeoTagUndo
{
    /// <summary>Primärschlüssel = <see cref="FileOperation.Id"/> (Löschen der Operation löscht die Zeile mit).</summary>
    public int FileOperationId { get; set; }

    public double? OldLatitude { get; set; }

    public double? OldLongitude { get; set; }

    public GeoSource? OldGeoSource { get; set; }

    public int? OldPlaceId { get; set; }

    /// <summary>Gesetzte Koordinaten (null bei „Ort entfernen“).</summary>
    public double? NewLatitude { get; set; }

    public double? NewLongitude { get; set; }

    /// <summary>Was geschrieben wurde: <see cref="GeoTagWriteKind"/>.</summary>
    public GeoTagWriteKind WriteKind { get; set; }

    /// <summary>Pfad der geschriebenen Datei (JPEG bzw. XMP-Begleitdatei); null bei „nur Katalog“.</summary>
    public string? WrittenPath { get; set; }

    /// <summary>
    /// Alte Bytes: JPEG = komplettes APP1-„Exif“-Segment inkl. Marker (leer = es gab keines); XMP = alte Begleitdatei
    /// (null = es gab keine).
    /// </summary>
    public byte[]? OldBytes { get; set; }

    /// <summary>SHA-256 des geschriebenen Stands (JPEG: neues Segment, XMP: ganze Datei) — Rückgängig nur, wenn unverändert.</summary>
    public byte[]? WrittenHash { get; set; }

    /// <summary>Byte-Position des Segments im JPEG (Rückgängig ersetzt dort).</summary>
    public long? SegmentOffset { get; set; }

    /// <summary>
    /// Änderungszeit der Datei vor dem Schreiben als UTC-Ticks (100 ns, volle Genauigkeit — sonst sähen Sync- und
    /// Backup-Programme nach Rückgängig eine geänderte Datei); Rückgängig stellt sie wieder her.
    /// </summary>
    public long? OldModifiedTicks { get; set; }

    /// <summary>Größe der Datei vor dem Schreiben.</summary>
    public long? OldSize { get; set; }
}
