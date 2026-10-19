using PhotoViewerV3.Services.Scanning;

namespace PhotoViewerV3.Services.Watch;

/// <summary>Zustand der Ordnerüberwachung (für Statusleiste und Einstellungen).</summary>
public enum WatchState
{
    /// <summary>Überwachung ausgeschaltet oder keine aktive Bibliothek.</summary>
    Disabled = 0,

    /// <summary>Wurzeln werden überwacht, keine offenen Änderungen.</summary>
    Idle = 1,

    /// <summary>Mindestens eine Wurzel ist nicht erreichbar; der Dienst versucht es periodisch erneut.</summary>
    RootUnavailable = 2,

    /// <summary>Änderungen erkannt; der Incremental-Scan startet nach Ablauf der Ruhezeit (<see cref="WatchStatus.ScanDueAtUtc"/>).</summary>
    ChangesPending = 3,

    /// <summary>Der automatische Incremental-Scan läuft.</summary>
    Scanning = 4,

    /// <summary>
    /// Automatik angehalten: der letzte automatische Scan endete mit <see cref="ScanOutcome.MassMissingBlocked"/> oder
    /// <see cref="ScanOutcome.LibraryOffline"/>. Kein Wiederholungs-Scan, nur Hinweis — ein erfolgreicher manueller
    /// Scan („Aktualisieren“ auf der Bibliotheksseite), ein Bibliothekswechsel oder das Umschalten der Einstellung hebt
    /// die Sperre auf.
    /// </summary>
    Blocked = 5,
}

/// <summary>
/// Momentaufnahme der Ordnerüberwachung. <paramref name="LastAutoScan"/> ist bei <see cref="WatchState.Idle"/> das
/// Ergebnis des soeben abgeschlossenen automatischen Scans (sonst null); <paramref name="Reason"/> erklärt
/// <see cref="WatchState.Blocked"/> und <see cref="WatchState.RootUnavailable"/>.
/// </summary>
public sealed record WatchStatus(
    int? LibraryId,
    WatchState State,
    DateTime? ScanDueAtUtc = null,
    string? Reason = null,
    ScanResult? LastAutoScan = null)
{
    public static WatchStatus Off { get; } = new(null, WatchState.Disabled);
}

/// <summary>
/// Zustandswechsel der Ordnerüberwachung (Statusleiste). Transport wie alle Nachrichten: WeakReferenceMessenger.Default;
/// kommt von Watcher-, Timer- oder Scan-Threads — UI-Empfänger dispatchen selbst.
/// </summary>
public sealed record WatchStatusChangedMessage(WatchStatus Status);

/// <summary>
/// Ordnerüberwachung der aktiven Bibliothek: ein FileSystemWatcher je Wurzel sammelt Dateiereignisse (nur
/// Medienformate aus dem Formatregister) und löst nach einer Ruhezeit ohne weitere Ereignisse einen
/// Incremental-Scan über <see cref="IScanService"/> aus — nie parallel zu einem laufenden Scan. Einstellungen
/// (Schalter, Ruhezeit) liegen in der Settings-Tabelle und wirken sofort.
/// </summary>
public interface ILibraryWatchService : IDisposable
{
    /// <summary>Einstellung „Bibliotheksordner überwachen“ (Standard: an).</summary>
    bool IsEnabled { get; }

    /// <summary>Ruhezeit nach dem letzten Ereignis bis zum Scan (1–60 s, Standard 3 s).</summary>
    TimeSpan QuietPeriod { get; }

    /// <summary>Aktueller Zustand (thread-sicher lesbar).</summary>
    WatchStatus Status { get; }

    /// <summary>Einstellungen aus der DB lesen, Nachrichten abonnieren und die aktive Bibliothek überwachen (App-Start).</summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>Überwachung und laufenden automatischen Scan beenden (Einstellung bleibt unverändert).</summary>
    Task StopAsync();

    /// <summary>Schalter speichern und sofort anwenden.</summary>
    Task SetEnabledAsync(bool enabled, CancellationToken ct = default);

    /// <summary>Ruhezeit speichern (auf 1–60 s begrenzt) und sofort anwenden.</summary>
    Task SetQuietPeriodAsync(TimeSpan quietPeriod, CancellationToken ct = default);
}
