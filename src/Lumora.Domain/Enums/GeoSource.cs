namespace Lumora.Domain.Enums;

/// <summary>
/// Herkunft der Koordinaten einer Datei (ADR-0050, Spalte <c>Files.GeoSource</c>). NULL in der Spalte = noch nicht gelesen
/// (das Nachtragen im Hintergrund liest die Datei). Die Werte ab <see cref="Manual"/> hat der Benutzer in Lumora gesetzt
/// bzw. bestätigt — sie überleben Inhaltsänderungen und „Neu aufbauen“ und werden nie automatisch überschrieben oder
/// erneut vorgeschlagen (ADR-0062).
/// </summary>
public enum GeoSource
{
    /// <summary>Gelesen, keine Koordinaten (bzw. manuell entfernt, dann mit <see cref="Manual"/>).</summary>
    None = 0,

    /// <summary>EXIF/GPS der Datei oder XMP-Begleitdatei (<c>exif:GPSLatitude</c>).</summary>
    Exif = 1,

    /// <summary>Video-Metadaten (MP4/MOV <c>©xyz</c> bzw. Apple <c>com.apple.quicktime.location.ISO6709</c>).</summary>
    Video = 2,

    /// <summary>In Lumora gesetzt („Ort setzen…“) oder entfernt (dann ohne Koordinaten).</summary>
    Manual = 3,

    /// <summary>Vorschlag aus einer GPS-Spur (Garmin-Aktivität, GPX/TCX) übernommen (ADR-0062).</summary>
    Track = 4,

    /// <summary>Vorschlag aus zeitnahen Fotos mit echtem GPS übernommen (ADR-0062).</summary>
    Neighbors = 5,
}
