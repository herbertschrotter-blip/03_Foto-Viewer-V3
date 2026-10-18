namespace PhotoViewerV3.Services.Sorter;

/// <summary>
/// Pattern-basierter File Sorter (Port von V2 <c>Lib_FileSorter.ps1</c>): Analyse eines Ordners
/// (nicht rekursiv) → Gruppen, In-Memory-Nachbearbeitung, Plan (Quelle → Ziel) und Ausführung über
/// den <c>IFileOperationService</c> mit Lauf-Protokoll und „Rückgängig (letzter Lauf)“.
/// Arbeitet nur in Ordnern einer Bibliothek: Dateien werden über den Katalog (Pfad → FileId) verschoben;
/// Dateien ohne Katalogeintrag werden markiert und übersprungen.
/// </summary>
public interface IFileSorterService
{
    /// <exception cref="FolderNotInLibraryException">Der Ordner gehört zu keiner Bibliothek.</exception>
    /// <exception cref="DirectoryNotFoundException">Der Ordner existiert nicht.</exception>
    Task<SorterAnalysis> AnalyzeAsync(string folderPath, SorterAnalyzeOptions? options = null, CancellationToken ct = default);

    /// <summary>Datei in eine andere (ggf. neue) Gruppe verschieben — nur im Analyse-Ergebnis.</summary>
    void MoveFile(SorterAnalysis analysis, string fileName, string targetKey);

    /// <summary>Alle Dateien der Quellgruppe in die Zielgruppe; die Quellgruppe verschwindet.</summary>
    void MergeGroups(SorterAnalysis analysis, string sourceKey, string targetKey);

    /// <summary>Ausgewählte Dateien einer Gruppe in eine neue Gruppe abspalten.</summary>
    void SplitGroup(SorterAnalysis analysis, string sourceKey, IReadOnlyCollection<string> fileNames, string newKey);

    /// <summary>
    /// Plan aus Gruppen + Mappings (Gruppenschlüssel → Zielordnername; fehlende Schlüssel werden
    /// übersprungen). Sub-Gruppen verwenden ihren eigenen <see cref="SorterGroup.TargetFolder"/>.
    /// </summary>
    SorterPlan BuildPlan(SorterAnalysis analysis, IReadOnlyDictionary<string, string> mappings, SorterPlanOptions? options = null);

    Task<SorterExecutionResult> ExecuteAsync(SorterPlan plan, IProgress<SorterProgress>? progress = null, CancellationToken ct = default);

    /// <summary>Letzter Sortier-Lauf (auch wenn bereits rückgängig gemacht) oder null.</summary>
    Task<SorterRunRecord?> GetLastRunAsync(CancellationToken ct = default);

    /// <summary>Macht den letzten Lauf rückgängig (alle Batches); null, wenn es nichts rückgängig zu machen gibt.</summary>
    Task<SorterUndoResult?> UndoLastRunAsync(IProgress<SorterProgress>? progress = null, CancellationToken ct = default);
}
