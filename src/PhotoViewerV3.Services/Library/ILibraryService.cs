namespace PhotoViewerV3.Services.Library;

/// <summary>Read-Model einer Bibliothek für UI und Services.</summary>
public sealed record LibraryInfo(
    int Id,
    string Name,
    string RootPath,
    DateTime CreatedAt,
    DateTime? LastOpenedAt,
    int FolderCount,
    int FileCount,
    bool IsRootAvailable);

/// <summary>
/// Verwaltung der Bibliotheken (ADR-0003: eine aktive Library, Multi-Library vorbereitet).
/// "Aktiv" = die Library mit dem jüngsten LastOpenedAt. Jede Änderung der aktiven Library
/// wird als <c>ActiveLibraryChangedMessage</c> über den WeakReferenceMessenger gesendet.
/// </summary>
public interface ILibraryService
{
    Task<IReadOnlyList<LibraryInfo>> GetAllAsync(CancellationToken ct = default);

    Task<LibraryInfo?> GetAsync(int libraryId, CancellationToken ct = default);

    /// <summary>Aktive Library (jüngstes LastOpenedAt, sonst jüngstes CreatedAt) oder null, wenn keine existiert.</summary>
    Task<LibraryInfo?> GetActiveAsync(CancellationToken ct = default);

    /// <summary>Legt eine Library an. Wirft, wenn RootPath bereits einer Library zugeordnet ist.</summary>
    Task<LibraryInfo> CreateAsync(string name, string rootPath, CancellationToken ct = default);

    Task RenameAsync(int libraryId, string newName, CancellationToken ct = default);

    /// <summary>Setzt LastOpenedAt = jetzt und sendet ActiveLibraryChangedMessage.</summary>
    Task SetActiveAsync(int libraryId, CancellationToken ct = default);

    /// <summary>
    /// Löscht alle Scan-Daten der Library (Files, Folders, Thumbnails-Zeilen + Cache-Dateien,
    /// FTS-Einträge; OperationBatches bleiben), die Library-Zeile bleibt bestehen.
    /// </summary>
    Task ClearAsync(int libraryId, CancellationToken ct = default);

    /// <summary>ClearAsync + Library-Zeile löschen.</summary>
    Task DeleteAsync(int libraryId, CancellationToken ct = default);
}
