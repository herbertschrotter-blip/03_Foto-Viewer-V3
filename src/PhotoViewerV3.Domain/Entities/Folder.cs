namespace PhotoViewerV3.Domain.Entities;

/// <summary>
/// Ein Ordner innerhalb einer Library. Hierarchie via ParentId (NULL = Top-Level unterhalb RootPath).
/// UNIQUE(LibraryId, Path) wird über Index erzwungen — siehe Schema in PLAN Kap. 4.
/// </summary>
public sealed class Folder
{
    public int Id { get; set; }
    public int LibraryId { get; set; }
    public required string Path { get; set; }
    public int? ParentId { get; set; }
    public required string Name { get; set; }
    public DateTime ScannedAt { get; set; }
}
