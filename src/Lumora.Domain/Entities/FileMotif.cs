namespace Lumora.Domain.Entities;

/// <summary>
/// Treffer der Motiv-Erkennung (ADR-0052, Tabelle "FileMotifs", Schlüssel FileId + TermId): die Datei zeigt den
/// Begriff mit der Wahrscheinlichkeit <see cref="Score"/> (0..1, SigLIP-Sigmoid) über der Schwelle des Begriffs. Nur
/// Treffer werden gespeichert. Datei oder Begriff gelöscht → Zeile weg (ON DELETE CASCADE).
/// </summary>
public sealed class FileMotif
{
    public int FileId { get; set; }
    public int TermId { get; set; }
    public float Score { get; set; }
}
