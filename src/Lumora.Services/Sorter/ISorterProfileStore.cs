namespace Lumora.Services.Sorter;

/// <summary>
/// Pattern-Profile des Sortierers (Stufe-1-Patterns und Sub-Level), persistiert als JSON in der
/// Settings-Tabelle (Keys <c>sorter.patterns</c> / <c>sorter.sublevels</c>). Import/Export nutzt das
/// V2-Dateiformat (<c>file-sorter-patterns.json</c>, <c>file-sorter-sublevels.json</c>).
/// Speichern validiert (Regex kompilierbar, Name eindeutig, Capture-Gruppe vorhanden) und wirft
/// bei Fehlern eine <see cref="SorterProfileException"/>.
/// </summary>
public interface ISorterProfileStore
{
    IReadOnlyList<SorterPattern> DefaultPatterns { get; }

    IReadOnlyList<SorterSubLevel> DefaultSubLevels { get; }

    Task<IReadOnlyList<SorterPattern>> GetPatternsAsync(CancellationToken ct = default);

    Task SavePatternsAsync(IReadOnlyList<SorterPattern> patterns, CancellationToken ct = default);

    Task<IReadOnlyList<SorterPattern>> ResetPatternsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<SorterSubLevel>> GetSubLevelsAsync(CancellationToken ct = default);

    Task SaveSubLevelsAsync(IReadOnlyList<SorterSubLevel> subLevels, CancellationToken ct = default);

    Task<IReadOnlyList<SorterSubLevel>> ResetSubLevelsAsync(CancellationToken ct = default);

    /// <summary>Fehlerliste (leer = gültig).</summary>
    IReadOnlyList<string> ValidatePatterns(IReadOnlyList<SorterPattern> patterns);

    IReadOnlyList<string> ValidateSubLevels(IReadOnlyList<SorterSubLevel> subLevels);

    Task ExportPatternsAsync(string filePath, CancellationToken ct = default);

    /// <summary>Liest eine V2-kompatible JSON-Datei, validiert, speichert und liefert die Patterns.</summary>
    Task<IReadOnlyList<SorterPattern>> ImportPatternsAsync(string filePath, CancellationToken ct = default);

    Task ExportSubLevelsAsync(string filePath, CancellationToken ct = default);

    /// <summary>Liest eine V2-kompatible JSON-Datei (Array oder einzelnes Objekt), validiert, speichert.</summary>
    Task<IReadOnlyList<SorterSubLevel>> ImportSubLevelsAsync(string filePath, CancellationToken ct = default);
}
