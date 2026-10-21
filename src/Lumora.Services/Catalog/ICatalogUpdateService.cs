namespace Lumora.Services.Catalog;

/// <summary>
/// Schreibpfad für Benutzer-Metadaten im Katalog (Favorit, Bewertung). Jede Änderung wird als
/// <c>FilesChangedMessage</c> (Kind = Metadata) über den WeakReferenceMessenger gesendet, damit
/// Gallery und Lightbox ihre Anzeige aktualisieren.
/// </summary>
public interface ICatalogUpdateService
{
    Task SetFavoriteAsync(IReadOnlyList<int> fileIds, bool isFavorite, CancellationToken ct = default);

    /// <summary>Bewertung 1–5 Sterne; null entfernt die Bewertung.</summary>
    Task SetRatingAsync(IReadOnlyList<int> fileIds, byte? rating, CancellationToken ct = default);
}
