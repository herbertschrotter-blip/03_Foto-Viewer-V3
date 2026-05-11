using PhotoViewerV3.Domain.Enums;

namespace PhotoViewerV3.Domain.Entities;

/// <summary>
/// Einzelne Operation innerhalb eines OperationBatch. SourcePath wird auch persistiert,
/// damit Recovery / Undo unabhängig vom MediaFile-Eintrag funktioniert (auch wenn der Eintrag
/// inzwischen gelöscht wurde).
/// </summary>
public sealed class FileOperation
{
    public int Id { get; set; }
    public int BatchId { get; set; }
    public int? FileId { get; set; } // nullable: bei MkDir gibt es noch keinen MediaFile-Eintrag
    public OperationType Action { get; set; }
    public required string SourcePath { get; set; }
    public string? DestPath { get; set; }
    public FileOperationStatus Status { get; set; }
    public string? Error { get; set; }
    public DateTime? ExecutedAtUtc { get; set; }
    public DateTime? UndoneAtUtc { get; set; }
}
