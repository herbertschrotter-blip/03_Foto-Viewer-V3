namespace PhotoViewerV3.Services.Operations;

/// <summary>
/// „Ins Archiv…“ (ADR-0020): fragt den Zielordner unterhalb der Archiv-Wurzel ab (Dialog in der Wpf-Schicht) und
/// verschiebt die Dateien über <see cref="IFileOperationService.MoveAsync"/>. WPF-frei, damit Galerie und Lightbox
/// die Aktion ohne gegenseitige Abhängigkeit auslösen können (Strg+M). Die Implementierung lebt im Wpf-Projekt
/// (Gallery/Archive) und wird dort registriert.
/// </summary>
public interface IArchiveMoveService
{
    /// <summary>
    /// Zeigt den Zieldialog für die Dateien und verschiebt sie. null, wenn der Benutzer abgebrochen hat oder keine
    /// der Dateien zu einer Bibliothek mit Archiv-Wurzel gehört.
    /// </summary>
    Task<FileOperationBatchResult?> ArchiveAsync(IReadOnlyList<int> fileIds, CancellationToken ct = default);
}
