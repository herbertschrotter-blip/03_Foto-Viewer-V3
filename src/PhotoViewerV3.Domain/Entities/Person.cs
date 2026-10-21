namespace PhotoViewerV3.Domain.Entities;

/// <summary>
/// Person für die Gesichtserkennung (ADR-0035, Tabelle "Persons"). Global wie Tags (nicht je Library).
/// <see cref="CoverFaceId"/> verweist ohne Fremdschlüssel auf ein Gesicht (Zyklus Persons ↔ Faces vermieden) — Leser
/// müssen ein inzwischen gelöschtes Gesicht tolerieren. <see cref="IsHidden"/> blendet die Person in Listen aus.
/// Seit 0.2.47 (ADR-0045, Migration AddPersonDetails): Vor-/Nachname, Rufname, Beziehung, Geburtsdatum (auch ungefähr)
/// und „Das bin ich“. <see cref="Name"/> bleibt als synchron gehaltener Anzeigename (<see cref="PersonNames.DisplayName"/>)
/// — Galerie-Suche, Listenspalte, Filter und Lightbox lesen weiter nur ihn.
/// </summary>
public sealed class Person
{
    public int Id { get; set; }

    /// <summary>Anzeigename: Rufname, sonst „Vorname Nachname“ (ohne Groß-/Kleinschreibung eindeutig).</summary>
    public required string Name { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public int? CoverFaceId { get; set; }
    public bool IsHidden { get; set; }

    public string FirstName { get; set; } = string.Empty;

    public string LastName { get; set; } = string.Empty;

    /// <summary>Rufname („Opa Franz“); null = keiner.</summary>
    public string? Nickname { get; set; }

    /// <summary>Beziehung aus Sicht des Benutzers (<see cref="PersonRelation"/>); null = keine.</summary>
    public int? RelationId { get; set; }

    public int? BirthYear { get; set; }

    public int? BirthMonth { get; set; }

    public int? BirthDay { get; set; }

    /// <summary>Der Benutzer selbst — höchstens eine Person (eindeutiger Teilindex).</summary>
    public bool IsMe { get; set; }
}
