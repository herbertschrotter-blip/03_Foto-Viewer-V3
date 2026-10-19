namespace PhotoViewerV3.Services.Tags;

/// <summary>
/// Tags verwalten (anlegen, umbenennen, färben, sortieren, löschen) und Dateien zuweisen (ADR-0021).
/// Änderungen an der Tag-Liste werden als <see cref="TagsChangedMessage"/> gesendet, Zuweisungen als
/// <c>FilesChangedMessage</c> (Kind = Metadata) je betroffener Library — Galerie und Lightbox aktualisieren
/// damit ihre Anzeige wie bei Favorit/Bewertung.
/// </summary>
public interface ITagService
{
    /// <summary>Alle Tags in Anzeigereihenfolge (SortOrder, dann Name).</summary>
    Task<IReadOnlyList<TagInfo>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Legt ein Tag an. Der Name wird getrimmt und muss eindeutig sein (ohne Groß-/Kleinschreibung), sonst
    /// <see cref="InvalidOperationException"/>; <paramref name="color"/> null = nächste Palettenfarbe.
    /// </summary>
    Task<TagInfo> CreateAsync(string name, string? color = null, CancellationToken ct = default);

    Task RenameAsync(int tagId, string newName, CancellationToken ct = default);

    Task SetColorAsync(int tagId, string color, CancellationToken ct = default);

    /// <summary>Setzt die Reihenfolge; Tags, die nicht in der Liste stehen, folgen dahinter in bisheriger Ordnung.</summary>
    Task ReorderAsync(IReadOnlyList<int> tagIdsInOrder, CancellationToken ct = default);

    /// <summary>Löscht das Tag samt Zuweisungen (ON DELETE CASCADE); liefert die Zahl der entfernten Zuweisungen.</summary>
    Task<int> DeleteAsync(int tagId, CancellationToken ct = default);

    /// <summary>Zahl der zugewiesenen Dateien je TagId (Tags ohne Zuweisung fehlen im Ergebnis).</summary>
    Task<IReadOnlyDictionary<int, int>> GetFileCountsAsync(CancellationToken ct = default);

    /// <summary>Weist die Tags den Dateien zu (bestehende Zuweisungen bleiben); liefert die Zahl neuer Zuweisungen.</summary>
    Task<int> AssignAsync(IReadOnlyList<int> fileIds, IReadOnlyList<int> tagIds, CancellationToken ct = default);

    /// <summary>Entfernt die Tags von den Dateien; liefert die Zahl entfernter Zuweisungen.</summary>
    Task<int> RemoveAsync(IReadOnlyList<int> fileIds, IReadOnlyList<int> tagIds, CancellationToken ct = default);

    /// <summary>
    /// Entfernt alle Zuweisungen der Dateien (ohne Nachricht — für Aufrufer, die die Files-Zeilen selbst entfernen).
    /// Dank ON DELETE CASCADE in <c>FileTags</c> normalerweise nicht nötig; Hilfsmethode für Aufräumpfade ohne FK-Erzwingung.
    /// </summary>
    Task<int> RemoveForFilesAsync(IReadOnlyList<int> fileIds, CancellationToken ct = default);

    /// <summary>TagIds je Datei in Anzeigereihenfolge (Batch für eine Galerie-Page); Dateien ohne Tag fehlen im Ergebnis.</summary>
    Task<IReadOnlyDictionary<int, IReadOnlyList<int>>> GetTagIdsForFilesAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct = default);
}
