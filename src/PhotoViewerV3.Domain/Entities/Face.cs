using PhotoViewerV3.Domain.Enums;

namespace PhotoViewerV3.Domain.Entities;

/// <summary>
/// Ein erkanntes Gesicht in einer Datei (ADR-0035, Tabelle "Faces"). Box und Landmarken sind relativ (0..1) zum
/// analysierten Bild NACH Anwendung der EXIF-Orientierung. <see cref="Landmarks"/> = 10 float32 little-endian
/// (x0, y0 … x4, y4: Auge links, Auge rechts, Nase, Mundwinkel links, Mundwinkel rechts, ebenfalls relativ),
/// <see cref="Embedding"/> = 512 float32 little-endian (2048 Bytes), L2-normiert (ArcFace w600k_r50) — Kodierung über
/// <c>Services.Faces.FaceEmbeddingCodec</c>. <see cref="Size"/> = Seitenlänge der Box (Wurzel aus Breite × Höhe) in
/// Pixeln des analysierten Bildes, <see cref="Sharpness"/> = Laplace-Varianz des ausgerichteten 112er-Ausschnitts,
/// <see cref="Quality"/> = 0..1 aus Score, Größe und Schärfe. Files-Zeile weg → Gesicht weg (ON DELETE CASCADE);
/// Person gelöscht → <see cref="PersonId"/> und <see cref="SuggestedPersonId"/> werden NULL.
/// </summary>
public sealed class Face
{
    public int Id { get; set; }
    public int FileId { get; set; }
    public float BoxX { get; set; }
    public float BoxY { get; set; }
    public float BoxW { get; set; }
    public float BoxH { get; set; }
    public required byte[] Landmarks { get; set; }
    public float DetScore { get; set; }
    public float Size { get; set; }
    public float Sharpness { get; set; }
    public float Quality { get; set; }
    public required byte[] Embedding { get; set; }
    public required string ModelVersion { get; set; }
    public int? PersonId { get; set; }
    public FaceState State { get; set; }
    public int? SuggestedPersonId { get; set; }
    public float? SuggestionScore { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
