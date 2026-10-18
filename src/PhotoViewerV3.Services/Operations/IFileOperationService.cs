using PhotoViewerV3.Domain.Enums;

namespace PhotoViewerV3.Services.Operations;

public sealed record FileOperationItemResult(
    int FileId,
    string SourcePath,
    string? DestPath,
    bool Success,
    string? Error);

public sealed record FileOperationBatchResult(
    int BatchId,
    OperationType Type,
    int Succeeded,
    int Failed,
    IReadOnlyList<FileOperationItemResult> Items);

public sealed record OperationBatchInfo(
    int Id,
    OperationType Type,
    BatchStatus Status,
    DateTime CreatedAtUtc,
    DateTime? CompletedAtUtc,
    int ItemCount,
    string? Description,
    bool CanUndo);

/// <summary>
/// Datei-Operationen mit Protokoll in OperationBatches/FileOperations (ADR-0012).
/// Jede Operation hält Dateisystem und Katalog (Files, Folders, Thumbnails, FTS) konsistent
/// und sendet danach eine <c>FilesChangedMessage</c>. Fehler einzelner Dateien brechen den
/// Batch nicht ab (Teilerfolg wird pro Item gemeldet).
/// </summary>
public interface IFileOperationService
{
    /// <summary>Dateien in den Windows-Papierkorb verschieben; Katalog-Einträge werden entfernt.</summary>
    Task<FileOperationBatchResult> RecycleAsync(IReadOnlyList<int> fileIds, CancellationToken ct = default);

    /// <summary>
    /// Dateien in einen Zielordner verschieben. Liegt das Ziel innerhalb der Library-Root, wird
    /// die Folders-Zeile bei Bedarf angelegt und Files.FolderId aktualisiert; liegt es außerhalb,
    /// wird der Katalog-Eintrag entfernt. Existiert der Zielname bereits, schlägt dieses Item fehl
    /// (kein Überschreiben).
    /// </summary>
    Task<FileOperationBatchResult> MoveAsync(IReadOnlyList<int> fileIds, string targetFolderPath, CancellationToken ct = default);

    Task<FileOperationBatchResult> RenameAsync(int fileId, string newName, CancellationToken ct = default);

    /// <summary>Macht den jüngsten rückgängig machbaren Batch (Move/Rename) rückgängig; null, wenn keiner existiert.</summary>
    Task<FileOperationBatchResult?> UndoLastAsync(CancellationToken ct = default);

    Task<FileOperationBatchResult?> UndoBatchAsync(int batchId, CancellationToken ct = default);

    Task<IReadOnlyList<OperationBatchInfo>> GetRecentBatchesAsync(int take = 50, CancellationToken ct = default);
}
