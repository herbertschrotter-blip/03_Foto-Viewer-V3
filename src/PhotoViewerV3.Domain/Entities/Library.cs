namespace PhotoViewerV3.Domain.Entities;

/// <summary>
/// Eine Foto-/Video-Bibliothek (entspricht einem RootPath gemäß ADR-0003).
/// V0.1: eine aktive Library, LibraryId-FKs sind vorbereitet für Multi-Library in späteren Phasen.
/// Seit 0.2.51 (ADR-0048) gehört jede Bibliothek genau einer <see cref="LibraryGroup"/> (<see cref="GroupId"/>; 0 = noch
/// keine — der Bereichsdienst legt sie beim ersten Zugriff an).
/// </summary>
public sealed class Library
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string RootPath { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastOpenedAt { get; set; }

    /// <summary>Verknüpfungsgruppe (ADR-0048); ohne Fremdschlüssel, damit Gruppenwechsel nie eine Tabelle neu aufbauen.</summary>
    public int GroupId { get; set; }
}
