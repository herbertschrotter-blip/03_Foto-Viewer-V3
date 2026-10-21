namespace PhotoViewerV3.Domain.Entities;

/// <summary>
/// Person für die Gesichtserkennung (ADR-0035, Tabelle "Persons"). Global wie Tags (nicht je Library).
/// <see cref="CoverFaceId"/> verweist ohne Fremdschlüssel auf ein Gesicht (Zyklus Persons ↔ Faces vermieden) — Leser
/// müssen ein inzwischen gelöschtes Gesicht tolerieren. <see cref="IsHidden"/> blendet die Person in Listen aus.
/// </summary>
public sealed class Person
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public int? CoverFaceId { get; set; }
    public bool IsHidden { get; set; }
}
