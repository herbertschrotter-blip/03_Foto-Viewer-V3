namespace PhotoViewerV3.Domain.Entities;

/// <summary>
/// Merker des Hintergrund-Index (ADR-0035, Tabelle "FaceScans", Schlüssel FileId): diese Datei wurde mit
/// <see cref="ModelVersion"/> analysiert und hatte <see cref="FaceCount"/> Gesichter; <see cref="ErrorCode"/> ≠ null =
/// Analyse gescheitert (z. B. nicht dekodierbar) — sie wird mit derselben ModelVersion nicht erneut versucht.
/// Fehlt die Zeile oder weicht die ModelVersion ab, steht die Datei zur (Neu-)Analyse an. Files-Zeile weg → Zeile weg
/// (ON DELETE CASCADE).
/// </summary>
public sealed class FaceScan
{
    public int FileId { get; set; }
    public required string ModelVersion { get; set; }
    public DateTime ScannedAtUtc { get; set; }
    public int FaceCount { get; set; }
    public string? ErrorCode { get; set; }
}
