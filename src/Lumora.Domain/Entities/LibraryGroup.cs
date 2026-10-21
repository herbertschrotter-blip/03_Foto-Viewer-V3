namespace Lumora.Domain.Entities;

/// <summary>
/// Verknüpfungsgruppe von Bibliotheken (ADR-0048, Tabelle "LibraryGroups"). Jede Bibliothek gehört genau einer Gruppe
/// (<see cref="Library.GroupId"/>); ohne Verknüpfung ist das eine Gruppe nur mit ihr selbst. Benutzerdefinierte Begriffe —
/// Tags, Kategorien (mit Werten), Personen und Beziehungen — gehören genau einer Gruppe (<c>GroupId</c>) und sind nur in
/// den Bibliotheken dieser Gruppe sichtbar. Verknüpfen führt zwei Gruppen zusammen, Lösen gibt einer Bibliothek wieder eine
/// eigene Gruppe (mit Kopien aller gemeinsamen Begriffe). <see cref="Name"/> erscheint im Umschalter als
/// „Alle verknüpften (Name)“.
/// </summary>
public sealed class LibraryGroup
{
    public int Id { get; set; }

    public required string Name { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
