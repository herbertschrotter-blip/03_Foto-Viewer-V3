namespace Lumora.Services.Operations;

/// <summary>
/// „Ordner in die Bibliothek übernehmen…“ (ADR-0034): Ordner unter einem Eingang samt Unterordner-Struktur in den
/// Bibliotheksordner (Archiv-Wurzel) verschieben; leere Zwischenordner werden auf Wunsch zusammengezogen. WPF-frei —
/// der Dialog (Wpf/Folders/Adoption) liest den Quellbaum, zeigt den Plan als Vorschau und führt ihn hierüber aus.
/// </summary>
public interface IFolderAdoptionService
{
    /// <summary>Kontext des Ordners (Bibliothek, Eingang, Bibliotheksordner, zuletzt benutztes Ziel); null, wenn er unter keinem Eingang liegt.</summary>
    Task<FolderAdoptionContext?> GetContextAsync(string sourceFolder, CancellationToken ct = default);

    /// <summary>Quellbaum lesen (Thread-Pool).</summary>
    Task<AdoptionSourceSnapshot> ReadSourceAsync(string sourceFolder, CancellationToken ct = default);

    /// <summary>Plan für einen Zielordner berechnen (gegen das aktuelle Dateisystem des Ziels).</summary>
    FolderAdoptionPlan BuildPlan(AdoptionSourceSnapshot snapshot, string targetFolder, bool collapseEmptyChains);

    /// <summary>
    /// Plan in einem protokollierten Batch ausführen, Zielordner merken und — wenn gewünscht, die ganze Eingangs-Wurzel
    /// übernommen wurde und nichts zurückblieb — den Eingang austragen (nur Katalog).
    /// </summary>
    Task<FolderAdoptionOutcome> ExecuteAsync(
        FolderAdoptionContext context,
        FolderAdoptionPlan plan,
        FolderAdoptionOptions options,
        IProgress<int>? progress = null,
        CancellationToken ct = default);
}
