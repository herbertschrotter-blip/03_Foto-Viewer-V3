using Lumora.Domain.Enums;

namespace Lumora.Domain.Entities;

/// <summary>
/// Eine Datei (Foto oder Video) innerhalb einer Library. Heißt absichtlich nicht "File",
/// um Konflikte mit System.IO.File zu vermeiden. Mapped auf SQLite-Tabelle "Files".
/// Schema siehe PLAN Kap. 4.
/// </summary>
public sealed class MediaFile
{
    public int Id { get; set; }
    public int LibraryId { get; set; }
    public int FolderId { get; set; }
    public required string Name { get; set; }
    public required string Extension { get; set; }
    public long Size { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ModifiedAtUtc { get; set; }
    public MediaType MediaType { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public int? DurationMs { get; set; }
    public string? Codec { get; set; }
    public bool IsFavorite { get; set; }
    public byte? Rating { get; set; }
    public bool Missing { get; set; }

    // ---- Aufnahmedatum (ADR-0046) ----

    /// <summary>Aufnahmezeitpunkt als UTC (null = unbekannt bzw. noch nicht gelesen; Sortierung fällt auf <see cref="ModifiedAtUtc"/> zurück).</summary>
    public DateTime? CapturedAt { get; set; }

    /// <summary>Herkunft; null = noch nicht gelesen (Nachtragen im Hintergrund).</summary>
    public CaptureDateSource? CapturedAtSource { get; set; }

    public CaptureDatePrecision CapturedAtPrecision { get; set; }

    /// <summary>Abstand der Ortszeit der Aufnahme zu UTC in Minuten (Anzeige als Wanduhrzeit „wie fotografiert“).</summary>
    public int? CapturedAtOffset { get; set; }

    // ---- Orte (ADR-0050) ----

    /// <summary>Breitengrad WGS84 in Dezimalgrad (null = keine Koordinaten bzw. noch nicht gelesen).</summary>
    public double? Latitude { get; set; }

    /// <summary>Längengrad WGS84 in Dezimalgrad.</summary>
    public double? Longitude { get; set; }

    /// <summary>Herkunft der Koordinaten; null = noch nicht gelesen (Nachtragen im Hintergrund).</summary>
    public GeoSource? GeoSource { get; set; }

    /// <summary>Nächster Ort aus den eingebetteten GeoNames-Daten (GeoNames-Id), null ohne Koordinaten bzw. ohne Ort in der Nähe.</summary>
    public int? PlaceId { get; set; }

    // ---- Ungenaue Orte und GPS-Spuren (ADR-0062) ----

    /// <summary>Genauigkeit der gelesenen Koordinaten; null = noch nicht bewertet (Nachtragen im Hintergrund).</summary>
    public GeoAccuracy? GeoAccuracy { get; set; }

    /// <summary>Gründe für „geschätzt“ (Bitmaske <see cref="Enums.GeoAccuracyReason"/>), null ohne.</summary>
    public GeoAccuracyReason? GeoAccuracyReason { get; set; }

    /// <summary>GPS-Zeitstempel der Datei (EXIF-GPS-Tags 29 + 7) als UTC, null ohne.</summary>
    public DateTime? GpsTimeUtc { get; set; }

    /// <summary>Abstand zu UTC laut <c>OffsetTimeOriginal</c> (EXIF 36881) in Minuten, null wenn die Datei keinen nennt.</summary>
    public int? ExifTimeOffset { get; set; }

    /// <summary>Kamera („Hersteller Modell“ aus IFD0 Make/Model) für die Kamera-Abweichung des Zeitabgleichs, null ohne.</summary>
    public string? CameraModel { get; set; }

    /// <summary>1 = Vorschlag für den Ort abgelehnt (wird nicht mehr vorgeschlagen), 0 sonst.</summary>
    public int GeoSuggestionState { get; set; }
}
