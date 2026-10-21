namespace Lumora.Services.Sorter;

/// <summary>
/// „Flatten &amp; Move“ (Port von V2 <c>Lib_FlattenMove.ps1</c> / <c>Lib_Routes_Flatten.ps1</c>):
/// alle Mediendateien in Unterordnern beliebiger Tiefe werden nach
/// <c>Ziel\&lt;deduplizierte Pfad-Ebenen&gt;\datei</c> geplant (Wurzel-Dateien → „_root“),
/// Namenskonflikte erhalten „_1“, „_2“, … Ausführung über den <c>IFileOperationService</c>
/// (Rename + Move je Datei im Protokoll), optional leere Quellordner löschen, Undo des letzten Laufs.
/// Gleiche Katalog-Regel wie der Sortierer: Quelle unter irgendeiner Wurzel der Bibliothek (Eingang oder Archiv), Ziel
/// unter einer Wurzel derselben Bibliothek (ADR-0024).
/// </summary>
public interface IFlattenService
{
    /// <exception cref="FolderNotInLibraryException">Der Ordner gehört zu keiner Bibliothek.</exception>
    /// <exception cref="DirectoryNotFoundException">Der Ordner existiert nicht.</exception>
    Task<FlattenAnalysis> AnalyzeAsync(string sourceFolder, FlattenAnalyzeOptions? options = null, CancellationToken ct = default);

    SorterPlan BuildPlan(FlattenAnalysis analysis, FlattenPlanOptions? options = null);

    Task<SorterExecutionResult> ExecuteAsync(SorterPlan plan, IProgress<SorterProgress>? progress = null, CancellationToken ct = default);

    Task<SorterRunRecord?> GetLastRunAsync(CancellationToken ct = default);

    Task<SorterUndoResult?> UndoLastRunAsync(IProgress<SorterProgress>? progress = null, CancellationToken ct = default);
}
