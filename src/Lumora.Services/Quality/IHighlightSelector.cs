namespace Lumora.Services.Quality;

/// <summary>
/// Wählt aus den Dateien einer Story die besten aus: Schärfe, Belichtung, Bildwirkung, bestätigte Personen,
/// Favorit und Bewertung; Serien und Doppelte nur einmal (ADR-0057). Vertrag zwischen Foto-Güte und Storys.
/// </summary>
public interface IHighlightSelector
{
    /// <summary>Highlights in der Reihenfolge von <paramref name="fileIds"/> (Aufnahme-Reihenfolge).</summary>
    Task<IReadOnlyList<HighlightPick>> SelectAsync(IReadOnlyList<int> fileIds, HighlightOptions options, CancellationToken ct = default);

    /// <summary>Bestes Titelbild (nie unscharf oder dunkel, wenn es eine Alternative gibt); null bei leerer Liste.</summary>
    Task<int?> PickCoverAsync(IReadOnlyList<int> fileIds, CancellationToken ct = default);
}

/// <param name="Share">Anteil der Highlights (0..1).</param>
public sealed record HighlightOptions(double Share = 0.15, int Min = 6, int Max = 80);

/// <param name="Reason">Kurzer Grund für „warum ausgewählt“, z. B. „scharf · 2 Personen · Favorit“.</param>
/// <param name="SimilarCount">Weggelassene ähnliche Fotos (Serie bzw. Doppelte) zu diesem Highlight.</param>
public sealed record HighlightPick(int FileId, double Score, string Reason, int SimilarCount);
