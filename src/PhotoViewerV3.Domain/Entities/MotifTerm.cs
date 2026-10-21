namespace PhotoViewerV3.Domain.Entities;

/// <summary>
/// Motiv-Begriff (ADR-0052, Tabelle "MotifTerms"), z. B. „Sonnenuntergang“ oder „Hund“: wonach die Motiv-Erkennung die
/// Fotos einer Bibliothek durchsucht. Begriffe gehören wie Tags einer Bibliotheks-Gruppe (<see cref="GroupId"/>,
/// ADR-0048, seit 0.2.56) und sind dort ohne Groß-/Kleinschreibung eindeutig. <see cref="LibraryId"/> ist nur noch der
/// Anker für den Fremdschlüssel (eine Bibliothek der Gruppe; wird beim Lösen/Entfernen nachgezogen) und wird für den
/// Bereich nie gelesen. <see cref="Prompt"/> ist der Text, den das Sprachmodell liest (null = <see cref="Name"/>);
/// <see cref="Threshold"/> die Schwelle dieses Begriffs (null = globale Schwelle). <see cref="TextEmbedding"/> ist der
/// Text-Vektor (float32, L2-normiert) zur <see cref="TextModelVersion"/> — fehlt er oder passt die Version nicht, rechnet
/// der Dienst ihn neu. Letzte Bibliothek der Gruppe gelöscht → Begriffe weg (ON DELETE CASCADE über den Anker).
/// </summary>
public sealed class MotifTerm
{
    public int Id { get; set; }
    public int LibraryId { get; set; }

    /// <summary>Bibliotheks-Gruppe (ADR-0048); 0 nur übergangsweise für eine Bibliothek ohne Gruppe.</summary>
    public int GroupId { get; set; }
    public required string Name { get; set; }
    public string? Prompt { get; set; }
    public double? Threshold { get; set; }
    public int SortOrder { get; set; }
    public byte[]? TextEmbedding { get; set; }
    public string? TextModelVersion { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
