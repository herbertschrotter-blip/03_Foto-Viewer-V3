namespace Lumora.Services.Archive;

/// <summary>
/// Automatisches Entpacken neu gefundener Archive (ADR-0032), Einstellung je Bibliothek (ADR-0033), standardmäßig aus.
/// <list type="bullet">
/// <item>„Neu gefunden“ = ein Incremental-Scan hat das Archiv in den Katalog aufgenommen (FilesChangedMessage „Added“
/// zwischen dem Belegen der Scan-Sperre und der ScanCompletedMessage). Fresh-Scan und „Neu aufbauen“ lösen nichts aus.</item>
/// <item>Nur in Bibliotheken mit eingeschalteter Einstellung; dort nur Archive unter Eingangs-Wurzeln (ADR-0020), nie im
/// Archiv; nie in einem Ordner, der selbst aus einem Archiv entstand (<see cref="ArchiveExtractedMessage"/>) — verschachtelte
/// Archive behandelt allein das Runden-System des <see cref="IArchiveService"/>.</item>
/// <item>Ziel ist der Standard-Zielordner neben dem Archiv; existiert er schon, bleibt das Archiv unberührt.</item>
/// <item>Ablauf nach dem Scan, nie während er die Sperre hält: je Archiv Sperre als Datei-Operation belegen und
/// entpacken, Sperre freigeben, optional Papierkorb über IFileOperationService (protokolliert), am Ende ein
/// Incremental-Scan, der die Sperre selbst holt. Die Ordnerbäume gelten bis dahin als eigene Änderungen
/// (IOwnChangeRegistry), damit die Ordnerüberwachung keinen zweiten Scan auslöst.</item>
/// <item>Archive, die schon vor dem Einschalten im Katalog standen, bleiben liegen, bis der Benutzer sie ausdrücklich
/// nachholt (<see cref="ExtractExistingAsync"/>).</item>
/// </list>
/// Läufe werden nacheinander abgearbeitet; Fortschritt und Ergebnis gehen als <see cref="ArchiveAutoExtractStatusMessage"/> an den Messenger.
/// </summary>
public interface IArchiveAutoExtractService : IDisposable
{
    /// <summary>Einstellungen der Bibliothek (Standard: beides aus); gilt erst nach <see cref="StartAsync"/>.</summary>
    ArchiveAutoExtractSettings GetSettings(int libraryId);

    /// <summary>Ergebnis des zuletzt beendeten Laufs (null = noch keiner).</summary>
    ArchiveAutoExtractSummary? LastSummary { get; }

    /// <summary>
    /// Globale Einstellung bis 0.2.30 einmalig als Vorgabe für bestehende Bibliotheken übernehmen, Einstellungen lesen,
    /// Scan-Sperre und Nachrichten abonnieren (App-Start); mehrfacher Aufruf ist harmlos.
    /// </summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>Schalter der Bibliothek speichern und sofort anwenden; Ausschalten lässt einen laufenden Lauf nach dem aktuellen Archiv enden.</summary>
    Task SetEnabledAsync(int libraryId, bool enabled, CancellationToken ct = default);

    /// <summary>Papierkorb-Schalter der Bibliothek speichern und sofort anwenden.</summary>
    Task SetRecycleAfterExtractAsync(int libraryId, bool recycle, CancellationToken ct = default);

    /// <summary>
    /// Archive, die schon im Katalog stehen und der Lauf <see cref="ExtractExistingAsync"/> anfassen würde: unter
    /// Eingangs-Wurzeln, nicht fehlend, Zielordner existiert noch nicht, nicht in einem entpackten Ordner dieser Sitzung.
    /// </summary>
    Task<int> CountExistingAsync(int libraryId, CancellationToken ct = default);

    /// <summary>
    /// „Vorhandene Archive in Eingängen jetzt entpacken“: reiht einen Lauf über die Archive aus <see cref="CountExistingAsync"/>
    /// ein (gleicher Ablauf und gleiche Einstellungen wie nach einem Scan) und liefert deren Zahl. Nur bei eingeschalteter
    /// Einstellung der Bibliothek, sonst 0 ohne Lauf.
    /// </summary>
    Task<int> ExtractExistingAsync(int libraryId, CancellationToken ct = default);

    /// <summary>Wartet, bis alle eingereihten Läufe beendet sind (Tests, Rauchtest).</summary>
    Task WhenIdleAsync();
}
