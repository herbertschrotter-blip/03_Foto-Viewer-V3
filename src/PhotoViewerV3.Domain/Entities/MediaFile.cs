using PhotoViewerV3.Domain.Enums;

namespace PhotoViewerV3.Domain.Entities;

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
}
