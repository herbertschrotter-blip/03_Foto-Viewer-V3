namespace PhotoViewerV3.Services.Motifs;

/// <summary>Feste Werte der Motiv-Erkennung (ADR-0052).</summary>
public static class MotifDefaultsValues
{
    /// <summary>
    /// Standard-Schwelle (Wahrscheinlichkeit 0..1). SigLIP-Wahrscheinlichkeiten für kurze Begriffe sind klein: in der
    /// Stichprobe (200 Handyfotos) lag die höchste je Foto im Median bei 0,007; mit 0,01 bekommen 85 von 200 Fotos ein
    /// Motiv, Fotos ohne Gesicht fast nie ein Personen-Motiv (3 von 126). Spike 14.
    /// </summary>
    public const double Threshold = 0.01;

    /// <summary>Auswahl in den Einstellungen.</summary>
    public static IReadOnlyList<double> ThresholdChoices { get; } = [0.003, 0.005, 0.01, 0.02, 0.03, 0.05, 0.1];

    /// <summary>Längste Kante beim Laden des Originals (≥ 2 × 224, damit die PIL-Skalierung wirklich glättet).</summary>
    public const int LoadMaxEdge = 448;

    public const int ReadBatchSize = 64;
    public const int WriteBatchSize = 32;
    public static readonly TimeSpan WriteInterval = TimeSpan.FromSeconds(3);
    public const int Prefetch = 3;
}

/// <summary>Einstellungen der Motiv-Erkennung (Settings-Tabelle, global).</summary>
public sealed record MotifSettings(bool Enabled, bool UseGpu, double DefaultThreshold)
{
    public const string EnabledKey = "motifs.enabled";
    public const string UseGpuKey = "motifs.useGpu";
    public const string ThresholdKey = "motifs.threshold";

    /// <summary>
    /// Standard: AN (läuft erst, wenn der Benutzer das Modell bewusst heruntergeladen hat), Grafikkarte an, Schwelle
    /// <see cref="MotifDefaultsValues.Threshold"/>.
    /// </summary>
    public static MotifSettings Default { get; } = new(true, true, MotifDefaultsValues.Threshold);
}

/// <summary>Zustand des Hintergrund-Index der Motive.</summary>
public enum MotifIndexState
{
    Disabled = 0,
    ModelMissing = 1,
    Idle = 2,
    Running = 3,
    Paused = 4,
    Waiting = 5,
    Error = 6,
}

/// <summary>
/// Momentaufnahme des Motiv-Index für Statusleiste und Einstellungen: <see cref="Done"/> von <see cref="Total"/> Dateien
/// (Fotos und Videos mit Vorschaubild) der aktiven Bibliothek haben einen Bild-Vektor der aktuellen Version.
/// </summary>
public sealed record MotifIndexStatus(
    MotifIndexState State,
    int Done = 0,
    int Total = 0,
    double MsPerPhoto = 0,
    string? ExecutionProvider = null,
    string? Message = null,
    int? LibraryId = null)
{
    public static MotifIndexStatus Disabled { get; } = new(MotifIndexState.Disabled);

    public bool IsActive => State is MotifIndexState.Running or MotifIndexState.Paused or MotifIndexState.Waiting;
}

/// <summary>Neuer Zustand des Motiv-Index (vom Worker-Thread; Empfänger dispatchen selbst).</summary>
public sealed record MotifIndexStatusMessage(MotifIndexStatus Status);

/// <summary>Treffer von Dateien haben sich geändert (neue Vektoren oder neu berechnet); leere Liste = alle der Bibliothek.</summary>
public sealed record MotifsChangedMessage(int LibraryId, IReadOnlyList<int> FileIds);

/// <summary>Begriffe einer Bibliothek wurden angelegt, umbenannt, gelöscht oder ihre Schwelle geändert.</summary>
public sealed record MotifTermsChangedMessage(int LibraryId);

/// <summary>Ein Begriff mit Anzahl Treffern im Bereich.</summary>
/// <param name="Threshold">Eigene Schwelle oder null (= globale).</param>
/// <param name="HasVector">Text-Vektor zur aktuellen Modell-Version vorhanden.</param>
public sealed record MotifTermInfo(int Id, int LibraryId, string Name, string? Prompt, double? Threshold, int FileCount, bool HasVector);

/// <summary>Treffer einer Datei (Lightbox, Liste).</summary>
public sealed record FileMotifInfo(int FileId, int TermId, string Name, float Score);

/// <summary>Ergebnis der Freitext-Bildsuche.</summary>
public sealed record MotifSearchHit(int FileId, float Score);

/// <summary>Begriff ist ungültig (leer, zu lang, schon vorhanden) — Text für den Benutzer.</summary>
public sealed class MotifTermException(string message) : Exception(message);
