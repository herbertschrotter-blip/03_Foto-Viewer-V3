namespace Lumora.Services.Quality;

/// <summary>Zustand des Hintergrund-Index der Foto-Güte (Statusleiste).</summary>
/// <param name="IsRunning">Ein Durchgang läuft.</param>
/// <param name="Phase">„bewertet“ (Schärfe/Belichtung) bzw. „Bildwirkung“.</param>
/// <param name="Done">Bewertete Dateien im Bereich.</param>
/// <param name="Total">Bewertbare Dateien im Bereich.</param>
public sealed record FileQualityIndexStatus(bool IsRunning, string Phase, int Done, int Total, string? Message = null)
{
    public const string PixelPhase = "bewertet";

    public const string AestheticPhase = "Bildwirkung";

    public static FileQualityIndexStatus Idle { get; } = new(false, string.Empty, 0, 0);
}

/// <summary>Ergebnis eines Durchgangs (Tests, Log).</summary>
/// <param name="Rated">Neu bewertete Dateien (Schärfe/Belichtung).</param>
/// <param name="Aesthetic">Neu gerechnete Bildwirkungen.</param>
/// <param name="Skipped">Gerade nicht lesbar (fehlt, nur online, Wurzel nicht erreichbar).</param>
/// <param name="Failed">Nicht dekodierbar (gemerkt, mit derselben Version kein neuer Versuch).</param>
public sealed record FileQualityIndexReport(int Rated, int Aesthetic, int Skipped, int Failed);

/// <summary>Fortschritt des Hintergrund-Index (vom Worker-Thread, gedrosselt; Empfänger dispatchen selbst).</summary>
public sealed record FileQualityIndexStatusMessage(FileQualityIndexStatus Status);

/// <summary>Neue Güte-Werte in diesen Bibliotheken (Storys können Highlights neu wählen).</summary>
public sealed record FileQualityChangedMessage(IReadOnlyList<int> LibraryIds);
