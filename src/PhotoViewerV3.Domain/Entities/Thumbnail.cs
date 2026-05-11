using PhotoViewerV3.Domain.Enums;

namespace PhotoViewerV3.Domain.Entities;

/// <summary>
/// Thumbnail-Eintrag pro (FileId, Size, Kind). 3 Größen V0.1 (160/320/640) × Kind=Still.
/// Speicherpfad: %LocalAppData%\PhotoViewerV3\thumbs\{libraryId}\{fileId}_{size}_{kind}.jpg
/// gemäß ADR-0009 und PLAN Kap. 13. Composite-Key (FileId, Size, Kind).
/// </summary>
public sealed class Thumbnail
{
    public int FileId { get; set; }
    public int Size { get; set; }
    public ThumbnailKind Kind { get; set; }
    public required string Format { get; set; } // "jpg" / "png" / "webp" (V0.1 nur "jpg")
    public required string CachePath { get; set; }
    public DateTime SourceModifiedAtUtc { get; set; }
    public DateTime? GeneratedAtUtc { get; set; }
    public long ByteSize { get; set; }
    public ThumbnailStatus Status { get; set; }
    public int AttemptCount { get; set; }
    public DateTime? LastAttemptAtUtc { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}
