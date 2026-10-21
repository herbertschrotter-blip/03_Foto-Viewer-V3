namespace PhotoViewerV3.Domain.Enums;

/// <summary>
/// Herkunft der Koordinaten einer Datei (ADR-0050, Spalte <c>Files.GeoSource</c>). NULL in der Spalte = noch nicht gelesen
/// (das Nachtragen im Hintergrund liest die Datei).
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
}
