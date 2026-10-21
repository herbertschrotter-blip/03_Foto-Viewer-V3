namespace Lumora.Services.Albums;

/// <summary>
/// Ordner-Verknüpfungen (ADR-0054): ein Foto erscheint zusätzlich in einem weiteren Ordner, ohne kopiert zu werden (keine
/// Windows-Hardlinks). Nur innerhalb einer Bibliothek — Ordner und Datei gehören derselben Bibliothek. Verschieben und
/// Umbenennen erhalten die Verknüpfung (sie hängt an der FileId); Entfernen der Verknüpfung löscht nie das Foto.
/// Änderungen gehen als <see cref="FolderLinksChangedMessage"/> hinaus.
/// </summary>
public interface IFolderLinkService
{
    /// <summary>Verknüpft die Dateien mit dem Ordner (Dateien, die dort physisch liegen, bleiben unverändert).</summary>
    Task<FolderLinkResult> LinkAsync(int folderId, IReadOnlyList<int> fileIds, CancellationToken ct = default);

    /// <summary>
    /// Entfernt Verknüpfungen der Dateien aus dem Ordner (mit <paramref name="includeSubfolders"/> auch aus seinen
    /// Unterordnern); liefert die Zahl entfernter Verknüpfungen.
    /// </summary>
    Task<int> UnlinkAsync(int folderId, IReadOnlyList<int> fileIds, bool includeSubfolders = false, CancellationToken ct = default);

    /// <summary>Ordner (Id, Pfad), in die die Datei verknüpft ist.</summary>
    Task<IReadOnlyList<(int FolderId, string Path)>> GetLinkedFoldersAsync(int fileId, CancellationToken ct = default);
}
