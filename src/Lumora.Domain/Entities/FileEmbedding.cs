namespace Lumora.Domain.Entities;

/// <summary>
/// Bild-Vektor einer Datei für die Motiv-Erkennung (ADR-0052, Tabelle "FileEmbeddings", Schlüssel FileId): SigLIP-2-
/// Bild-Embedding (float32 little-endian, L2-normiert) zur <see cref="ModelVersion"/>. Bei Fotos aus der Datei, bei
/// Videos aus dem Vorschaubild. <see cref="ErrorCode"/> ≠ null = Analyse gescheitert (dann kein Vektor, mit derselben
/// Version kein neuer Versuch). Der Vektor bleibt, wenn Begriffe sich ändern — nur die Treffer (<see cref="FileMotif"/>)
/// werden neu berechnet. Files-Zeile weg → Zeile weg (ON DELETE CASCADE).
/// </summary>
public sealed class FileEmbedding
{
    public int FileId { get; set; }
    public required string ModelVersion { get; set; }
    public string? ErrorCode { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public byte[]? Embedding { get; set; }
}
