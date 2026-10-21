using Lumora.Services.Library;

namespace Lumora.Services.Albums;

/// <summary>
/// Wertet den Filter eines intelligenten Albums aus (ADR-0054). Die Galerie-Abfrage (alle Filterteile, Suche, Bereich)
/// lebt im Wpf-Projekt (<c>GalleryPageSource</c>); damit die Services-Schicht WPF-frei bleibt, liegt hier nur der Vertrag —
/// die Implementierung registriert das Wpf-Projekt (wie der WIC-Renderer). Ohne Registrierung kennen intelligente Alben
/// keine Dateien (Zähler „…“).
/// </summary>
public interface ISmartAlbumEvaluator
{
    /// <summary>Dateien, die den Filter erfüllen — nur im Bereich <paramref name="scope"/>, neueste Aufnahme zuerst.</summary>
    Task<IReadOnlyList<int>> GetFileIdsAsync(SmartAlbumFilter filter, LibraryScopeInfo scope, CancellationToken ct = default);

    /// <summary>Anzahl und neueste Datei (Titelbild) ohne alle Ids zu lesen.</summary>
    Task<(int Count, int? FirstFileId)> SummarizeAsync(SmartAlbumFilter filter, LibraryScopeInfo scope, CancellationToken ct = default);
}
