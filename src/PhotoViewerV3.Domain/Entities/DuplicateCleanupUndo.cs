namespace PhotoViewerV3.Domain.Entities;

/// <summary>
/// Rückgängig-Daten zu „Übrige in den Papierkorb“ der Seite „Doppelte“ (ADR-0053, Tabelle "DuplicateCleanupUndo"). Je
/// gelöschter Datei eine Zeile im Recycle-Batch <see cref="BatchId"/>: <see cref="Snapshot"/> = Files-Zeile und alle von
/// ihr abhängigen Katalogzeilen (Tags, Kategorien, Gesichter, Vektoren … als JSON), damit „Rückgängig“ die Datei aus dem
/// Papierkorb mit derselben FileId und all ihren Daten zurückbringt; <see cref="Transfer"/> = was auf die behaltene Datei
/// <see cref="KeptFileId"/> übertragen wurde (JSON), damit das Rückgängig es wieder entfernt. Kein Fremdschlüssel: die
/// Files-Zeile der gelöschten Datei existiert ja gerade nicht mehr.
/// </summary>
public sealed class DuplicateCleanupUndo
{
    public int Id { get; set; }

    public int BatchId { get; set; }

    public int FileId { get; set; }

    public int KeptFileId { get; set; }

    public required string Snapshot { get; set; }

    public string? Transfer { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Zeitpunkt des Rückgängig (null = noch nicht zurückgenommen).</summary>
    public DateTime? UndoneAtUtc { get; set; }
}
