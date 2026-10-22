namespace Lumora.Domain.Enums;

/// <summary>
/// Genauigkeit der gelesenen Koordinaten einer Datei (ADR-0062, Spalte <c>Files.GeoAccuracy</c>). NULL in der Spalte =
/// noch nicht bewertet (das Nachtragen im Hintergrund liest die GPS-Angaben der Datei).
/// </summary>
public enum GeoAccuracy
{
    /// <summary>Nicht bewertbar: keine Koordinaten, Video, XMP-Begleitdatei oder in Lumora gesetzt.</summary>
    Unknown = 0,

    /// <summary>Echtes GPS (Höhe bzw. GPS-Zeit vorhanden, kein Hinweis auf eine Netzwerk-Schätzung).</summary>
    Exact = 1,

    /// <summary>Position geschätzt (Mobilfunk/WLAN, großer Fehlerradius oder unmöglicher Sprung) — Grund in <see cref="GeoAccuracyReason"/>.</summary>
    Estimated = 2,
}

/// <summary>Gründe für „Position geschätzt“ (Bitmaske, Spalte <c>Files.GeoAccuracyReason</c>, ADR-0062).</summary>
[Flags]
public enum GeoAccuracyReason
{
    None = 0,

    /// <summary>Nur Breite/Länge (EXIF-GPS-Tags 1–4) — ohne GPS-Höhe (6) und ohne GPS-Zeit (7/29).</summary>
    NoAltitudeNoTime = 1,

    /// <summary><c>GPSProcessingMethod</c> (27) nennt NETWORK, CELLID, WLAN bzw. WIFI.</summary>
    NetworkMethod = 2,

    /// <summary><c>GPSHPositioningError</c> (31) über 500 m.</summary>
    LargePositionError = 4,

    /// <summary>Unmöglicher Sprung (über ~150 km/h) zu zeitnahen Fotos mit echtem GPS.</summary>
    ImpossibleJump = 8,
}
