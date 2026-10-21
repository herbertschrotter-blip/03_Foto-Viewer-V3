namespace PhotoViewerV3.Services.Library;

/// <summary>
/// „Bibliotheken verknüpfen“ als eigene Einstellung (ADR-0048, Einstellungen › Bibliotheken &amp; Eingänge). Verknüpfen führt
/// die Gruppen zweier Bibliotheken zusammen: gleichnamige Tags, Kategorien (mit Werten) und Beziehungen werden eins,
/// gleichnamige Personen nach Rückfrage (Standard: zusammenführen). Lösen gibt einer Bibliothek wieder eine eigene Gruppe
/// mit KOPIEN aller gemeinsamen Begriffe und Personen (nichts geht verloren); Gesichter und Zuordnungen bleiben bei ihrer
/// Bibliothek und zeigen auf die Kopien. Beides in EINER Transaktion, während die Scan-Sperre aller betroffenen
/// Bibliotheken gehalten wird. Ein versteckter Katalog (<see cref="PhotoViewerV3.Services.Infrastructure.ICatalogContext.IsHidden"/>) lehnt beides ab.
/// Danach kommen <see cref="LibraryGroupsChangedMessage"/> und <c>ActiveLibraryChangedMessage</c> (alles lädt neu).
/// </summary>
public interface ILibraryLinkService
{
    /// <summary>false in einem versteckten Katalog — die Oberfläche blendet die Einstellung dann aus.</summary>
    bool IsLinkingAllowed { get; }

    /// <summary>Was das Verknüpfen von <paramref name="libraryId"/> mit <paramref name="otherLibraryId"/> zusammenführen würde.</summary>
    Task<LibraryLinkPreview> PreviewLinkAsync(int libraryId, int otherLibraryId, CancellationToken ct = default);

    /// <summary>
    /// Verknüpft: die Gruppe von <paramref name="otherLibraryId"/> geht in der von <paramref name="libraryId"/> auf (Name
    /// bleibt). <paramref name="keepApartSourcePersonIds"/>: gleichnamige Personen der hinzukommenden Gruppe, die NICHT
    /// zusammengeführt werden — sie bekommen den Bibliotheksnamen als Rufnamen-Zusatz, damit Namen eindeutig bleiben.
    /// </summary>
    Task<LibraryLinkResult> LinkAsync(int libraryId, int otherLibraryId, IReadOnlyCollection<int>? keepApartSourcePersonIds = null, CancellationToken ct = default);

    /// <summary>Was das Lösen von <paramref name="libraryId"/> kopieren würde.</summary>
    Task<LibraryUnlinkPreview> PreviewUnlinkAsync(int libraryId, CancellationToken ct = default);

    /// <summary>Löst die Bibliothek aus ihrer Gruppe (eigene Gruppe mit Kopien aller gemeinsamen Begriffe und Personen).</summary>
    Task<LibraryUnlinkResult> UnlinkAsync(int libraryId, CancellationToken ct = default);
}
