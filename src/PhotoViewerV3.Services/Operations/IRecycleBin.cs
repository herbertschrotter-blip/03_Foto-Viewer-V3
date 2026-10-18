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
}
