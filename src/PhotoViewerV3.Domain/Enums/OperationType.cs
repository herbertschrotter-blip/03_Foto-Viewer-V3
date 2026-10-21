namespace PhotoViewerV3.Domain.Enums;

/// <summary>
/// Art einer Datei-Operation. V0.1 nur Recycle (Delete-to-Recycle-Bin).
/// Move/Rename ab V0.2, MkDir/Copy in V0.3+.
/// </summary>
public enum OperationType
{
    Recycle = 0,
    Move = 1,
    Copy = 2,
    Rename = 3,
    MkDir = 4,

    /// <summary>Ordner umbenannt (Dateisystem + Folders-Zeilen; eine Operation je enthaltener Datei, ADR-0020).</summary>
    RenameFolder = 5,

    /// <summary>
    /// Ordner in die Bibliothek übernommen (ADR-0034): eine Ordner-Operation (Quelle → Zielordner), MkDir je neu
    /// angelegtem Zielordner, Move je Datei (auch Nicht-Mediendateien), optional Recycle des Quellordners.
    /// </summary>
    AdoptFolder = 6,

    /// <summary>
    /// Aufnahmedatum im Katalog gesetzt, verschoben oder zurückgesetzt (ADR-0046) — ändert keine Datei. Je Datei eine
    /// Operation; die alten Werte stehen in <c>CaptureDateUndo</c>, „Rückgängig“ schreibt sie zurück.
    /// </summary>
    SetCaptureDate = 7,
}
