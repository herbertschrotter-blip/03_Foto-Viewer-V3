namespace Lumora.Services.Database.Relocation;

/// <summary>
/// Zieht Katalog-DB und Thumbnail-Cache in einen anderen Ordner um (Einstellungen → Speicherorte, ADR-0026). Der neue
/// Ort wird in <c>database-location.json</c> vermerkt und gilt ab dem nächsten Start — nach Erfolg startet die App neu.
/// </summary>
public interface IDatabaseRelocationService
{
    /// <summary>Ordner mit der aktiven <c>library.db</c>.</summary>
    string CurrentFolder { get; }

    /// <summary>Standardort (App-Wurzel, <c>%LocalAppData%\Lumora</c>).</summary>
    string DefaultFolder { get; }

    /// <summary>Prüft einen Zielordner: Pfad, Laufwerk, Schreibrecht, Platz, vorhandener Katalog. Ändert nichts außer einer Schreibprobe.</summary>
    Task<RelocationTargetCheck> CheckTargetAsync(string targetFolder, CancellationToken ct = default);

    /// <summary>
    /// Führt den Umzug aus: Scans, Datei-Batches, Ordnerüberwachung und Thumbnail-Queue ruhigstellen, DB sperren,
    /// per Backup-API kopieren, Kopie prüfen (<c>integrity_check</c>, Zeilenzahlen je Tabelle), Thumbnails kopieren,
    /// neuen Ort speichern. Bei Erfolg bleibt alles gesperrt (die App startet neu); bei Fehler oder Abbruch wird das
    /// Ziel aufgeräumt und alles auf dem alten Ort fortgesetzt. Wirft nicht — das Ergebnis trägt die Meldung.
    /// </summary>
    Task<RelocationResult> RelocateAsync(
        string targetFolder,
        RelocationMode mode,
        IProgress<RelocationProgress>? progress = null,
        CancellationToken ct = default);
}

public enum RelocationTargetState
{
    /// <summary>Leer bzw. ohne Katalog, genug Platz — Umzug möglich.</summary>
    Ready,

    /// <summary>Dort liegt bereits ein Katalog (<c>library.db</c>) oder ein Thumbnail-Cache — übernehmen oder ersetzen.</summary>
    ExistingData,

    /// <summary>Ungeeignet (siehe <see cref="RelocationTargetCheck.Problem"/>).</summary>
    Invalid,
}

public enum RelocationMode
{
    /// <summary>Katalog und Thumbnails kopieren; Ziel ohne vorhandenen Katalog.</summary>
    Move,

    /// <summary>Den im Ziel vorhandenen Katalog übernehmen; nichts kopieren, der bisherige Katalog bleibt erhalten.</summary>
    AdoptExisting,

    /// <summary>Vorhandenen Katalog im Ziel beiseitelegen (beim nächsten Start gelöscht) und durch den aktuellen ersetzen.</summary>
    ReplaceExisting,
}

/// <param name="State">Ergebnis der Prüfung.</param>
/// <param name="TargetFolder">Voll aufgelöster Zielordner.</param>
/// <param name="Problem">Grund bei <see cref="RelocationTargetState.Invalid"/>; bei vorhandenem Katalog ggf. Platzmangel fürs Ersetzen.</param>
/// <param name="RequiredBytes">Platzbedarf für DB und Thumbnails inkl. Reserve.</param>
/// <param name="AvailableBytes">Freier Platz im Ziel; null = unbekannt.</param>
/// <param name="HasEnoughSpace">Reicht der Platz für eine Kopie?</param>
/// <param name="IsCloudSynced">Ziel liegt in einem OneDrive-Ordner (Warnung, kein Hindernis).</param>
/// <param name="ThumbnailFiles">Anzahl der zu kopierenden Vorschaubilder.</param>
/// <param name="Existing">Vorhandener Katalog im Ziel, sonst null.</param>
public sealed record RelocationTargetCheck(
    RelocationTargetState State,
    string TargetFolder,
    string? Problem,
    long RequiredBytes,
    long? AvailableBytes,
    bool HasEnoughSpace,
    bool IsCloudSynced,
    int ThumbnailFiles,
    ExistingCatalogInfo? Existing);

/// <param name="HasDatabase"><c>library.db</c> vorhanden (sonst nur ein Thumbnail-Ordner).</param>
/// <param name="IsValidCatalog">Lesbar, <c>quick_check</c> ok und mit den Tabellen eines Lumora-Katalogs.</param>
/// <param name="Libraries">Anzahl Bibliotheken (gültiger Katalog).</param>
/// <param name="Files">Anzahl Dateien (gültiger Katalog).</param>
/// <param name="ModifiedAtUtc">Letzte Änderung der DB-Datei.</param>
/// <param name="Problem">Warum der Katalog nicht übernommen werden kann.</param>
public sealed record ExistingCatalogInfo(
    bool HasDatabase,
    bool IsValidCatalog,
    int Libraries,
    long Files,
    DateTime? ModifiedAtUtc,
    string? Problem);

/// <param name="Stage">Schritt für die Anzeige („Datenbank kopieren“ …).</param>
/// <param name="Done">Erledigt (z. B. kopierte Vorschaubilder).</param>
/// <param name="Total">Gesamt; 0 = unbestimmt.</param>
public sealed record RelocationProgress(string Stage, long Done = 0, long Total = 0);

/// <param name="Success">Neuer Ort gespeichert — App neu starten.</param>
/// <param name="Error">Verständliche Meldung bei Fehler oder Abbruch.</param>
/// <param name="DatabasePath">Neue (Erfolg) bzw. weiter gültige (Fehler) DB-Datei.</param>
/// <param name="ThumbnailsCopied">Kopierte Vorschaubilder.</param>
/// <param name="ThumbnailsSkipped">Nicht kopierbare Vorschaubilder (werden bei Bedarf neu erzeugt).</param>
public sealed record RelocationResult(
    bool Success,
    string? Error,
    string DatabasePath,
    int ThumbnailsCopied = 0,
    int ThumbnailsSkipped = 0);

/// <summary>Erwarteter Abbruchgrund mit verständlicher Meldung (wird zu <see cref="RelocationResult.Error"/>).</summary>
public sealed class RelocationException(string message, Exception? inner = null) : Exception(message, inner);
