namespace PhotoViewerV3.Domain.Entities;

/// <summary>
/// Verknüpfung eines Fotos in einen weiteren Ordner (ADR-0054, Tabelle "FolderLinks", Schlüssel FolderId + FileId): die
/// Ordneransicht zeigt echten Inhalt plus verknüpfte Fotos, ohne die Datei zu kopieren (keine Windows-Hardlinks). Nur
/// innerhalb einer Bibliothek (Ordner und Datei derselben Bibliothek, im Schreibweg geprüft). Beide Fremdschlüssel löschen
/// mit (ON DELETE CASCADE).
/// </summary>
public sealed class FolderLink
{
    public int FolderId { get; set; }

    public int FileId { get; set; }

    public DateTime AddedAtUtc { get; set; }
}
