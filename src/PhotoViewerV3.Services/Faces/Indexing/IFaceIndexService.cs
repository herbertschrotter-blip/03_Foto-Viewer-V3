namespace PhotoViewerV3.Services.Faces.Indexing;

/// <summary>
/// Hintergrund-Index der Gesichtserkennung (ADR-0035): analysiert die Fotos der aktiven Bibliothek, die noch keinen
/// <c>FaceScans</c>-Eintrag mit der aktuellen Modell-Version haben — neueste zuerst, eine Datei nach der anderen
/// (nie zwei Läufe gleichzeitig), mit niedriger Priorität, Schreiben stapelweise. Startet beim App-Start, nach jedem
/// Scan und bei neuen Dateien (<c>FilesChangedMessage</c> „Added“). Zustand über <see cref="FaceIndexStatusMessage"/>.
/// Alle Methoden kehren sofort zurück bzw. arbeiten asynchron — der UI-Thread wartet nie auf Modell oder Katalog.
/// </summary>
public interface IFaceIndexService
{
    FaceIndexStatus Status { get; }

    FaceIndexSettings Settings { get; }

    /// <summary>true, solange der Benutzer pausiert hat (gilt für die Sitzung).</summary>
    bool IsPaused { get; }

    /// <summary>Einstellungen lesen, Nachrichten abonnieren, Worker starten (einmal beim App-Start).</summary>
    Task StartAsync(CancellationToken ct = default);

    Task SetEnabledAsync(bool enabled, CancellationToken ct = default);

    Task SetUseGpuAsync(bool useGpu, CancellationToken ct = default);

    /// <summary>Hält nach dem aktuellen Foto an.</summary>
    void Pause();

    void Resume();

    /// <summary>Bricht den laufenden Durchgang ab; der nächste Scan, neue Dateien oder ein Neustart setzen fort.</summary>
    void CancelRun();

    /// <summary>Stößt einen Durchgang an (z. B. nach dem Modell-Download).</summary>
    void RequestRun();

    /// <summary>
    /// „Index neu aufbauen“: alle Fotos der aktiven Bibliothek werden erneut analysiert. Bestätigte Zuordnungen bleiben
    /// erhalten (Abgleich über die Box, <see cref="FaceRowMerger"/>). Liefert die Zahl der zurückgesetzten Einträge.
    /// </summary>
    Task<int> RebuildAsync(CancellationToken ct = default);
}
