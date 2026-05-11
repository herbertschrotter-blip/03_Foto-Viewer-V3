namespace PhotoViewerV3.Domain.Entities;

/// <summary>
/// Eine Foto-/Video-Bibliothek (entspricht einem RootPath gemäß ADR-0003).
/// V0.1: eine aktive Library, LibraryId-FKs sind vorbereitet für Multi-Library in späteren Phasen.
/// </summary>
public sealed class Library
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string RootPath { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastOpenedAt { get; set; }
}
