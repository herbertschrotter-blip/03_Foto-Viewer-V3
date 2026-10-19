using PhotoViewerV3.Domain.Enums;

namespace PhotoViewerV3.Services.Library;

/// <summary>Read-Model eines Wurzelordners (ADR-0020) für UI und Services.</summary>
public sealed record LibraryRootInfo(
    int Id,
    int LibraryId,
    string Path,
    LibraryRootRole Role,
    string Name,
    int SortOrder,
    bool IsAvailable)
{
    public bool IsArchive => Role == LibraryRootRole.Archive;

    public bool IsInbox => Role == LibraryRootRole.Inbox;

    /// <summary>Anzeigetext der Rolle („Archiv“ / „Eingang“).</summary>
    public string RoleText => RoleLabel(Role);

    public static string RoleLabel(LibraryRootRole role) => role == LibraryRootRole.Archive ? "Archiv" : "Eingang";
}

/// <summary>
/// Read-Model einer Bibliothek für UI und Services. <paramref name="RootPath"/> ist der Pfad der Archiv-Wurzel
/// (Anzeige-/Kompatibilitätsfeld), <paramref name="IsRootAvailable"/> deren Erreichbarkeit;
/// <paramref name="Roots"/> enthält alle Wurzeln mit Rolle und eigener Erreichbarkeit (ADR-0020).
/// </summary>
public sealed record LibraryInfo(
    int Id,
    string Name,
    string RootPath,
    DateTime CreatedAt,
    DateTime? LastOpenedAt,
    int FolderCount,
    int FileCount,
    bool IsRootAvailable,
    IReadOnlyList<LibraryRootInfo> Roots)
{
    public LibraryRootInfo? ArchiveRoot => Roots.FirstOrDefault(r => r.IsArchive);

    public IReadOnlyList<LibraryRootInfo> InboxRoots => Roots.Where(r => r.IsInbox).ToList();

    /// <summary>true, wenn mindestens eine Wurzel nicht erreichbar ist.</summary>
    public bool HasOfflineRoot => Roots.Any(r => !r.IsAvailable);
}

/// <summary>
/// Verwaltung der Bibliotheken (ADR-0003: eine aktive Library, Multi-Library vorbereitet) und ihrer
/// Wurzelordner (ADR-0020: genau eine Archiv-Wurzel, beliebig viele Eingangs-Wurzeln).
/// "Aktiv" = die Library mit dem jüngsten LastOpenedAt. Jede Änderung der aktiven Library
/// wird als <c>ActiveLibraryChangedMessage</c> über den WeakReferenceMessenger gesendet, Änderungen an den
/// Wurzeln als <c>LibraryRootsChangedMessage</c>.
/// </summary>
public interface ILibraryService
{
    Task<IReadOnlyList<LibraryInfo>> GetAllAsync(CancellationToken ct = default);

    Task<LibraryInfo?> GetAsync(int libraryId, CancellationToken ct = default);

    /// <summary>Aktive Library (jüngstes LastOpenedAt, sonst jüngstes CreatedAt) oder null, wenn keine existiert.</summary>
    Task<LibraryInfo?> GetActiveAsync(CancellationToken ct = default);

    /// <summary>Legt eine Library an; <paramref name="rootPath"/> wird ihre Archiv-Wurzel. Wirft, wenn der Pfad bereits eine Wurzel ist.</summary>
    Task<LibraryInfo> CreateAsync(string name, string rootPath, CancellationToken ct = default);

    Task RenameAsync(int libraryId, string newName, CancellationToken ct = default);

    /// <summary>Setzt LastOpenedAt = jetzt und sendet ActiveLibraryChangedMessage.</summary>
    Task SetActiveAsync(int libraryId, CancellationToken ct = default);

    /// <summary>
    /// Löscht alle Scan-Daten der Library (Files, Folders, Thumbnails-Zeilen + Cache-Dateien,
    /// FTS-Einträge; OperationBatches bleiben), die Library-Zeile und ihre Wurzeln bleiben bestehen.
    /// </summary>
    Task ClearAsync(int libraryId, CancellationToken ct = default);

    /// <summary>ClearAsync + Library-Zeile und Wurzeln löschen.</summary>
    Task DeleteAsync(int libraryId, CancellationToken ct = default);

    // ---- Wurzeln (ADR-0020) ----

    /// <summary>Wurzeln der Library in Anzeigereihenfolge (Archiv zuerst, dann SortOrder/Id).</summary>
    Task<IReadOnlyList<LibraryRootInfo>> GetRootsAsync(int libraryId, CancellationToken ct = default);

    /// <summary>
    /// Fügt eine Wurzel hinzu. Der Ordner muss existieren und darf weder gleich einer anderen Wurzel sein noch
    /// innerhalb/oberhalb einer Wurzel derselben Library liegen (sonst würden Dateien doppelt katalogisiert).
    /// Rolle Archive nur, wenn die Library noch keine Archiv-Wurzel hat.
    /// </summary>
    Task<LibraryRootInfo> AddRootAsync(int libraryId, string path, LibraryRootRole role, string? name = null, CancellationToken ct = default);

    Task RenameRootAsync(int rootId, string newName, CancellationToken ct = default);

    /// <summary>
    /// Setzt die Rolle. Wird eine Eingangs-Wurzel zum Archiv, wird die bisherige Archiv-Wurzel zum Eingang und
    /// <c>Libraries.RootPath</c> zeigt auf die neue Archiv-Wurzel. Die einzige Archiv-Wurzel kann nicht zum
    /// Eingang werden (eine Bibliothek braucht genau eine Archiv-Wurzel).
    /// </summary>
    Task SetRootRoleAsync(int rootId, LibraryRootRole role, CancellationToken ct = default);

    /// <summary>
    /// Entfernt eine Eingangs-Wurzel samt ihrer Katalogdaten (Ordner, Dateien, Thumbnails, FTS) — nur aus dem
    /// Katalog, keine Datei auf der Platte wird angefasst. Die Archiv-Wurzel kann nicht entfernt werden.
    /// </summary>
    Task RemoveRootAsync(int rootId, CancellationToken ct = default);
}
