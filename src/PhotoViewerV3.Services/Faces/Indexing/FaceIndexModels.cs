namespace PhotoViewerV3.Services.Faces.Indexing;

/// <summary>Feste Werte des Hintergrund-Index (ADR-0035).</summary>
public static class FaceIndexDefaults
{
    /// <summary>Längste Kante beim Laden — Erkennung auf 640er-Letterbox, Ausschnitte für ArcFace aus diesem Bild.</summary>
    public const int LoadMaxEdge = 1280;

    /// <summary>So viele Dateien liest der Index je Abfrage aus dem Katalog.</summary>
    public const int ReadBatchSize = 64;

    /// <summary>Ergebnisse werden spätestens nach so vielen Dateien geschrieben …</summary>
    public const int WriteBatchSize = 16;

    /// <summary>… oder nach dieser Zeit.</summary>
    public static readonly TimeSpan WriteInterval = TimeSpan.FromSeconds(3);

    /// <summary>So viele Bilder werden vorab geladen (Dekodieren parallel zur Inferenz; Spike 09: 3 ≈ Bestwert mit DirectML).</summary>
    public const int Prefetch = 3;
}

/// <summary>Einstellungen der Gesichtserkennung (Settings-Tabelle, global).</summary>
public sealed record FaceIndexSettings(bool Enabled, bool UseGpu)
{
    public const string EnabledKey = "faces.enabled";
    public const string UseGpuKey = "faces.useGpu";

    /// <summary>Standard: AUS (Datenschutz, Rechenlast — der Benutzer schaltet bewusst ein), Grafikkarte an.</summary>
    public static FaceIndexSettings Default { get; } = new(false, true);
}

/// <summary>Zustand des Hintergrund-Index.</summary>
public enum FaceIndexState
{
    /// <summary>Gesichtserkennung ausgeschaltet.</summary>
    Disabled = 0,

    /// <summary>Eingeschaltet, aber das Modell fehlt (Einstellungen → herunterladen).</summary>
    ModelMissing = 1,

    /// <summary>Nichts zu tun (alle Fotos der aktiven Bibliothek analysiert) oder keine Bibliothek.</summary>
    Idle = 2,

    /// <summary>Analysiert Fotos.</summary>
    Running = 3,

    /// <summary>Vom Benutzer angehalten.</summary>
    Paused = 4,

    /// <summary>Wartet auf den Katalog (gesperrt, z. B. Umzug) und versucht es gleich erneut.</summary>
    Waiting = 5,

    /// <summary>Modell konnte nicht geladen werden o. ä.; <see cref="FaceIndexStatus.Message"/> nennt den Grund.</summary>
    Error = 6,
}

/// <summary>
/// Momentaufnahme des Index für Statusleiste und Einstellungen: <see cref="Done"/> von <see cref="Total"/> Fotos der
/// aktiven Bibliothek sind mit der aktuellen Modell-Version analysiert; <see cref="MsPerPhoto"/> = gleitender Mittelwert
/// der Dauer je Foto (Laden + Erkennen + Fingerabdruck, wie der Benutzer sie erlebt).
/// </summary>
public sealed record FaceIndexStatus(
    FaceIndexState State,
    int Done = 0,
    int Total = 0,
    double MsPerPhoto = 0,
    string? ExecutionProvider = null,
    string? Message = null,
    int? LibraryId = null,
    int FacesFound = 0)
{
    public static FaceIndexStatus Disabled { get; } = new(FaceIndexState.Disabled);

    public bool IsActive => State is FaceIndexState.Running or FaceIndexState.Paused or FaceIndexState.Waiting;
}

/// <summary>Neuer Zustand des Gesichts-Index (vom Worker-Thread; Empfänger dispatchen selbst).</summary>
public sealed record FaceIndexStatusMessage(FaceIndexStatus Status);

/// <summary>
/// Gesichter von Dateien wurden geschrieben (neu, geändert, gelöscht) — für die Personen-Seite (Welle 2). Kommt
/// nach dem Commit eines Schreib-Stapels.
/// </summary>
public sealed record FacesChangedMessage(int LibraryId, IReadOnlyList<int> FileIds);
