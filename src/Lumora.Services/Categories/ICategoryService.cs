namespace Lumora.Services.Categories;

/// <summary>
/// Frei konfigurierbare Kategorien mit festen Werten verwalten (Einstellungen → Kategorien) und Dateien manuell einen
/// Wert je Kategorie zuweisen (ADR-0027). Stammdaten-Änderungen → <see cref="CategoriesChangedMessage"/>, Zuweisungen →
/// <c>FilesChangedMessage</c> (Metadata) je Library. Fachfehler (leerer/doppelter Name, unbekannte Id) werfen
/// <see cref="ArgumentException"/> bzw. <see cref="InvalidOperationException"/> mit verständlicher Meldung.
/// </summary>
public interface ICategoryService
{
    /// <summary>Alle Kategorien samt Werten in Anzeigereihenfolge (SortOrder, dann Name).</summary>
    Task<IReadOnlyList<CategoryInfo>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Legt eine Kategorie an (Name getrimmt, eindeutig ohne Groß-/Kleinschreibung), optional gleich mit Werten.</summary>
    Task<CategoryInfo> CreateAsync(string name, IReadOnlyList<string>? values = null, CancellationToken ct = default);

    Task RenameAsync(int categoryId, string newName, CancellationToken ct = default);

    /// <summary>Setzt die Reihenfolge der Kategorien; nicht genannte folgen dahinter in bisheriger Ordnung.</summary>
    Task ReorderAsync(IReadOnlyList<int> categoryIdsInOrder, CancellationToken ct = default);

    /// <summary>Löscht die Kategorie samt Werten und Zuweisungen; liefert die Zahl entfernter Zuweisungen.</summary>
    Task<int> DeleteAsync(int categoryId, CancellationToken ct = default);

    /// <summary>Fügt einen Wert hinzu (am Ende; Name je Kategorie eindeutig).</summary>
    Task<CategoryValueInfo> AddValueAsync(int categoryId, string name, CancellationToken ct = default);

    Task RenameValueAsync(int valueId, string newName, CancellationToken ct = default);

    /// <summary>Setzt die Reihenfolge der Werte einer Kategorie; nicht genannte folgen dahinter.</summary>
    Task ReorderValuesAsync(int categoryId, IReadOnlyList<int> valueIdsInOrder, CancellationToken ct = default);

    /// <summary>Löscht einen Wert; Dateien mit diesem Wert haben danach keinen Wert in der Kategorie. Liefert die Zahl entfernter Zuweisungen.</summary>
    Task<int> DeleteValueAsync(int valueId, CancellationToken ct = default);

    /// <summary>Zahl der Dateien je ValueId (Werte ohne Zuweisung fehlen im Ergebnis).</summary>
    Task<IReadOnlyDictionary<int, int>> GetFileCountsAsync(CancellationToken ct = default);

    /// <summary>
    /// Setzt bei allen Dateien den Wert der Kategorie (ersetzt einen vorhandenen); <paramref name="valueId"/> null
    /// entfernt den Wert. Liefert die Zahl geänderter Zuweisungen.
    /// </summary>
    Task<int> SetValueAsync(IReadOnlyList<int> fileIds, int categoryId, int? valueId, CancellationToken ct = default);

    /// <summary>Werte je Datei (Batch für eine Galerie-Page): FileId → (CategoryId → ValueId); Dateien ohne Wert fehlen.</summary>
    Task<IReadOnlyDictionary<int, IReadOnlyDictionary<int, int>>> GetValuesForFilesAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct = default);
}
