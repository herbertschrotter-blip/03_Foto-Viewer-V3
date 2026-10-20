using PhotoViewerV3.Services.Scanning;

namespace PhotoViewerV3.Services.Archive;

/// <summary>
/// Einstellungen des automatischen Entpackens einer Bibliothek (ADR-0032, je Bibliothek seit 0.2.31 / ADR-0033) in der
/// Settings-Tabelle unter <c>archives.autoExtract.enabled.{LibraryId}</c> und <c>archives.autoExtract.recycle.{LibraryId}</c>.
/// Beides standardmäßig aus: ohne Zustimmung des Benutzers legt die App keine Ordner an und verschiebt kein Archiv in den
/// Papierkorb. Die globalen Schlüssel bis 0.2.30 (<see cref="LegacyEnabledKey"/>, <see cref="LegacyRecycleKey"/>) übernimmt
/// der Store einmalig als Vorgabe für die bestehenden Bibliotheken und entfernt sie danach.
/// </summary>
public sealed record ArchiveAutoExtractSettings(bool Enabled, bool RecycleAfterExtract)
{
    /// <summary>Globaler Schalter bis 0.2.30 — nur noch für die einmalige Übernahme gelesen.</summary>
    public const string LegacyEnabledKey = "archives.autoExtract.enabled";

    /// <summary>Globaler Papierkorb-Schalter bis 0.2.30 — nur noch für die einmalige Übernahme gelesen.</summary>
    public const string LegacyRecycleKey = "archives.autoExtract.recycle";

    public static ArchiveAutoExtractSettings Default { get; } = new(false, false);

    public static string EnabledKeyFor(int libraryId) => LegacyEnabledKey + "." + libraryId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static string RecycleKeyFor(int libraryId) => LegacyRecycleKey + "." + libraryId.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Wie ein einzelnes Archiv beim automatischen Entpacken ausging.</summary>
public enum ArchiveAutoExtractItemState
{
    /// <summary>Fehlerfrei entpackt (und je nach Einstellung in den Papierkorb verschoben).</summary>
    Extracted = 0,

    /// <summary>Entpackt, aber mit Fehlern einzelner Einträge — das Archiv bleibt liegen.</summary>
    ExtractedWithErrors = 1,

    /// <summary>Nicht entpackt (passwortgeschützt, beschädigt, leer, abgebrochen) — das Archiv bleibt liegen.</summary>
    Failed = 2,

    /// <summary>Bewusst nicht angefasst (Zielordner existiert bereits, Archiv nicht mehr vorhanden).</summary>
    Skipped = 3,
}

/// <summary>Ergebnis für ein Archiv; <paramref name="Message"/> erklärt Fehler, Hinweise oder den Grund fürs Überspringen.</summary>
public sealed record ArchiveAutoExtractItem(
    int FileId,
    string ArchivePath,
    ArchiveAutoExtractItemState State,
    string? TargetFolder,
    int FilesExtracted,
    bool Recycled,
    string? Message)
{
    public string Name => Path.GetFileName(ArchivePath);
}

/// <summary>Ergebnis eines Laufs (alle neu gefundenen Archive einer Bibliothek nach einem Scan) samt Folge-Scan.</summary>
public sealed record ArchiveAutoExtractSummary(
    int LibraryId,
    IReadOnlyList<ArchiveAutoExtractItem> Items,
    ScanResult? Scan,
    string? ScanError,
    bool Canceled)
{
    public int ExtractedCount => Items.Count(i => i.State is ArchiveAutoExtractItemState.Extracted or ArchiveAutoExtractItemState.ExtractedWithErrors);

    public int RecycledCount => Items.Count(i => i.Recycled);

    public int FilesExtracted => Items.Sum(i => i.FilesExtracted);

    /// <summary>Archive, die gemeldet werden müssen: mit Fehlern entpackt, nicht entpackt oder übersprungen.</summary>
    public IReadOnlyList<ArchiveAutoExtractItem> Problems => Items.Where(i => i.State != ArchiveAutoExtractItemState.Extracted || i.Message is not null).ToList();

    public bool HasProblems => Problems.Count > 0 || ScanError is not null || Canceled;
}

/// <summary>Phase des automatischen Entpackens.</summary>
public enum ArchiveAutoExtractState
{
    /// <summary>Archiv <see cref="ArchiveAutoExtractStatus.Index"/> von <see cref="ArchiveAutoExtractStatus.Total"/> wird entpackt.</summary>
    Running = 0,

    /// <summary>Folge-Scan der Bibliothek läuft (die Dateien kommen in den Katalog).</summary>
    Scanning = 1,

    /// <summary>Lauf beendet; <see cref="ArchiveAutoExtractStatus.Summary"/> ist gesetzt.</summary>
    Finished = 2,
}

/// <summary>Momentaufnahme für die Statusleiste (<see cref="ArchiveAutoExtractStatusMessage"/>).</summary>
public sealed record ArchiveAutoExtractStatus(
    int LibraryId,
    ArchiveAutoExtractState State,
    int Index,
    int Total,
    string? CurrentArchive,
    ArchiveAutoExtractSummary? Summary = null);
