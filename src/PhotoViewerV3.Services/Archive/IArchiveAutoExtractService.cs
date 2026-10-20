namespace PhotoViewerV3.Services.Archive;

/// <summary>
/// Automatisches Entpacken neu gefundener Archive (ADR-0032), Einstellung global, standardmäßig aus.
/// <list type="bullet">
/// <item>„Neu gefunden“ = ein Incremental-Scan hat das Archiv in den Katalog aufgenommen (FilesChangedMessage „Added“
/// zwischen dem Belegen der Scan-Sperre und der ScanCompletedMessage). Fresh-Scan und „Neu aufbauen“ lösen nichts aus.</item>
/// <item>Nur Archive unter Eingangs-Wurzeln (ADR-0020), nie im Archiv; nie in einem Ordner, der selbst aus einem Archiv
/// entstand (<see cref="ArchiveExtractedMessage"/>) — verschachtelte Archive behandelt allein das Runden-System des
/// <see cref="IArchiveService"/>.</item>
/// <item>Ziel ist der Standard-Zielordner neben dem Archiv; existiert er schon, bleibt das Archiv unberührt.</item>
/// <item>Ablauf nach dem Scan, nie während er die Sperre hält: je Archiv Sperre als Datei-Operation belegen und
/// entpacken, Sperre freigeben, optional Papierkorb über IFileOperationService (protokolliert), am Ende ein
/// Incremental-Scan, der die Sperre selbst holt. Die Ordnerbäume gelten bis dahin als eigene Änderungen
/// (IOwnChangeRegistry), damit die Ordnerüberwachung keinen zweiten Scan auslöst.</item>
/// </list>
/// Läufe werden nacheinander abgearbeitet; Fortschritt und Ergebnis gehen als <see cref="ArchiveAutoExtractStatusMessage"/> an den Messenger.
/// </summary>
public interface IArchiveAutoExtractService : IDisposable
{
    /// <summary>Einstellung „Neue Archive in Eingangsordnern automatisch entpacken“ (Standard: aus).</summary>
    bool IsEnabled { get; }

    /// <summary>Einstellung „Archiv danach in den Papierkorb“ (Standard: aus) — nur nach fehlerfreiem Entpacken.</summary>
    bool RecycleAfterExtract { get; }

    /// <summary>Ergebnis des zuletzt beendeten Laufs (null = noch keiner).</summary>
    ArchiveAutoExtractSummary? LastSummary { get; }

    /// <summary>Einstellungen lesen und Scan-Sperre und Nachrichten abonnieren (App-Start); mehrfacher Aufruf ist harmlos.</summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>Schalter speichern und sofort anwenden; Ausschalten lässt einen laufenden Lauf nach dem aktuellen Archiv enden.</summary>
    Task SetEnabledAsync(bool enabled, CancellationToken ct = default);

    /// <summary>Papierkorb-Schalter speichern und sofort anwenden.</summary>
    Task SetRecycleAfterExtractAsync(bool recycle, CancellationToken ct = default);

    /// <summary>Wartet, bis alle eingereihten Läufe beendet sind (Tests, Rauchtest).</summary>
    Task WhenIdleAsync();
}
