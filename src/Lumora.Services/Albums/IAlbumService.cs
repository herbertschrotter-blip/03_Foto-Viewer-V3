namespace Lumora.Services.Albums;

/// <summary>
/// Alben (ADR-0054): eigene Alben (Fotos einzeln verknüpft) und intelligente Alben (gespeicherter Galerie-Filter). Ein Foto
/// liegt physisch nur einmal; ein Album verknüpft es nur — Entfernen aus einem Album oder Löschen eines Albums löscht nie
/// ein Foto. Alben gehören der Bibliotheks-Gruppe des aktuellen Bereichs (ADR-0048) und zeigen, zählen und exportieren
/// nur Dateien dieses Bereichs. Dienst-Schnittstelle auch für spätere Storys („Story aus einem Album“): Alben lesen
/// (<see cref="GetAllAsync"/>, <see cref="GetAsync"/>) und Dateien eines Albums im Bereich (<see cref="GetFileIdsAsync"/>).
/// Änderungen gehen als <see cref="AlbumsChangedMessage"/> hinaus.
/// </summary>
public interface IAlbumService
{
    /// <summary>
    /// Alben der Gruppe des aktuellen Bereichs in Anzeigereihenfolge (SortOrder, Name), mit Zählern und Titelbild. Ohne
    /// <paramref name="evaluateSmart"/> werden intelligente Alben nicht ausgewertet (Zähler/Titelbild leer) — für Listen,
    /// die nur Namen brauchen (Galerie-Flyout), damit kein Filter über die ganze Bibliothek läuft.
    /// </summary>
    Task<IReadOnlyList<AlbumInfo>> GetAllAsync(bool evaluateSmart = true, CancellationToken ct = default);

    /// <summary>Ein Album des aktuellen Bereichs; null, wenn es nicht (mehr) existiert oder einer anderen Gruppe gehört.</summary>
    Task<AlbumInfo?> GetAsync(int albumId, CancellationToken ct = default);

    /// <summary>Album gleichen Namens (ohne Groß-/Kleinschreibung) in der Gruppe des Bereichs oder null.</summary>
    Task<AlbumInfo?> FindByNameAsync(string name, CancellationToken ct = default);

    /// <summary>Neues eigenes Album; Name getrimmt, je Gruppe eindeutig (sonst <see cref="InvalidOperationException"/>).</summary>
    Task<AlbumInfo> CreateAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Speichert einen Galerie-Filter als intelligentes Album. Gibt es den Namen schon: mit <paramref name="replaceExisting"/>
    /// wird der Filter des (intelligenten) Albums ersetzt („Bearbeiten = Filter laden und neu speichern“), sonst
    /// <see cref="InvalidOperationException"/>. Ein eigenes Album wird nie überschrieben. Ein leerer Filter wird abgelehnt.
    /// </summary>
    Task<AlbumInfo> SaveSmartAsync(string name, SmartAlbumFilter filter, bool replaceExisting = false, CancellationToken ct = default);

    Task RenameAsync(int albumId, string newName, CancellationToken ct = default);

    /// <summary>Löscht das Album samt Verknüpfungen — die Fotos bleiben.</summary>
    Task DeleteAsync(int albumId, CancellationToken ct = default);

    /// <summary>Titelbild setzen (Datei des Bereichs) bzw. mit null zurücksetzen (neueste Aufnahme).</summary>
    Task SetCoverAsync(int albumId, int? fileId, CancellationToken ct = default);

    /// <summary>
    /// Verknüpft Dateien mit einem eigenen Album (vorhandene bleiben); nur Dateien aus Bibliotheken der Gruppe des Albums.
    /// Liefert die Zahl neuer Verknüpfungen.
    /// </summary>
    Task<int> AddFilesAsync(int albumId, IReadOnlyList<int> fileIds, CancellationToken ct = default);

    /// <summary>Entfernt Verknüpfungen (nie die Fotos); liefert die Zahl entfernter Verknüpfungen.</summary>
    Task<int> RemoveFilesAsync(int albumId, IReadOnlyList<int> fileIds, CancellationToken ct = default);

    /// <summary>Dateien des Albums im aktuellen Bereich, neueste Aufnahme zuerst (eigene und intelligente Alben).</summary>
    Task<IReadOnlyList<int>> GetFileIdsAsync(int albumId, CancellationToken ct = default);

    /// <summary>Eigene Alben je Datei (Häkchen im Menü „Zu Album hinzufügen“); Dateien ohne Album fehlen im Ergebnis.</summary>
    Task<IReadOnlyDictionary<int, IReadOnlyList<int>>> GetAlbumIdsForFilesAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct = default);

    /// <summary>
    /// „Album exportieren…“: kopiert die Dateien des Albums (im Bereich) als echte Kopien in <paramref name="targetFolder"/>.
    /// Gleichnamige Dateien bekommen „ (2)“ usw., nichts wird überschrieben; fehlende Quellen werden übersprungen. Abbrechen
    /// hält nach der laufenden Datei an (eine halb kopierte Datei wird entfernt). In einer versteckten Bibliothek gesperrt
    /// (<see cref="InvalidOperationException"/>) — Kopien lägen außerhalb des Containers (ADR-0049).
    /// </summary>
    Task<AlbumExportResult> ExportAsync(int albumId, string targetFolder, IProgress<AlbumExportProgress>? progress = null, CancellationToken ct = default);
}
