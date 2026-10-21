namespace PhotoViewerV3.Domain.Entities;

/// <summary>
/// Beziehung einer Person aus Sicht des Benutzers („Schwester“, „Cousin mütterlicherseits“, „Taufpate“) — keine feste
/// Liste: der Benutzer tippt sie selbst, bereits verwendete werden vorgeschlagen (ADR-0045, Tabelle "PersonRelations").
/// <see cref="Name"/> ist ohne Groß-/Kleinschreibung eindeutig; die Schreibweise der ersten Verwendung bleibt.
/// </summary>
public sealed class PersonRelation
{
    public int Id { get; set; }

    public required string Name { get; set; }
}
