namespace PhotoViewerV3.Services.Operations;

/// <summary>
/// Abstraktion über den Windows-Papierkorb, damit <see cref="FileOperationService"/> ohne echte
/// Shell-Aufrufe testbar bleibt (Fake im Testprojekt). Produktiv: <see cref="WindowsRecycleBin"/>.
/// </summary>
public interface IRecycleBin
{
    /// <summary>
    /// Verschiebt eine Datei in den Papierkorb. Wirft bei Fehlern (Datei gesperrt, nicht vorhanden,
    /// Papierkorb nicht verfügbar) — der Aufrufer protokolliert das Item dann als Failed.
    /// </summary>
    void MoveToRecycleBin(string filePath);

    /// <summary>
    /// Verschiebt einen ganzen Ordner (samt Inhalt) in den Papierkorb — für „Ordner in die Bibliothek übernehmen“
    /// (ADR-0034), nachdem alle Dateien daraus verschoben wurden. Standard: nicht unterstützt (Fakes ohne Ordner).
    /// </summary>
    void MoveDirectoryToRecycleBin(string directoryPath)
        => throw new NotSupportedException("Dieser Papierkorb unterstützt keine Ordner.");

    /// <summary>
    /// Holt die zuletzt (nach <paramref name="deletedAfterUtc"/>) gelöschte Datei mit diesem Ursprungspfad aus dem Papierkorb
    /// an ihren Platz zurück (Rückgängig von „Übrige in den Papierkorb“, ADR-0053). Wirft, wenn sie nicht (mehr) im Papierkorb
    /// liegt oder der Platz belegt ist. Standard: nicht unterstützt.
    /// </summary>
    void RestoreFromRecycleBin(string originalPath, DateTime deletedAfterUtc)
        => throw new NotSupportedException("Dieser Papierkorb kann nichts zurückholen.");
}
