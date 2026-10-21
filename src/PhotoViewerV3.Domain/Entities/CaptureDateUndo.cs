using PhotoViewerV3.Domain.Enums;

namespace PhotoViewerV3.Domain.Entities;

/// <summary>
/// Alte Werte des Aufnahmedatums einer Datei vor „Datum setzen…“ (ADR-0046), 1:1 zu einer <see cref="FileOperation"/>
/// vom Typ <see cref="OperationType.SetCaptureDate"/>. „Rückgängig“ schreibt sie zurück. Tabelle <c>CaptureDateUndo</c>.
/// </summary>
public sealed class CaptureDateUndo
{
    /// <summary>Primärschlüssel = <see cref="FileOperation.Id"/> (Löschen der Operation löscht die Zeile mit).</summary>
    public int FileOperationId { get; set; }

    public DateTime? OldCapturedAt { get; set; }

    public CaptureDateSource? OldSource { get; set; }

    public CaptureDatePrecision OldPrecision { get; set; }

    public int? OldOffset { get; set; }
}
