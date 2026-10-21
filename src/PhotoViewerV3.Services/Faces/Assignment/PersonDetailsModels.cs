using PhotoViewerV3.Domain.Entities;

namespace PhotoViewerV3.Services.Faces.Assignment;

// Personen-Details (0.2.47, ADR-0045): Vor-/Nachname, Rufname, Beziehung, Geburtsdatum, „Das bin ich“.

/// <summary>
/// Gespeicherte Details einer Person. <see cref="DisplayName"/> = Rufname, sonst „Vorname Nachname“ (steht synchron in
/// <c>Persons.Name</c>).
/// </summary>
public sealed record PersonDetails(
    int Id,
    string FirstName,
    string LastName,
    string? Nickname,
    int? RelationId,
    string? RelationName,
    PersonBirthDate? BirthDate,
    bool IsMe)
{
    public string DisplayName => PersonNames.DisplayName(FirstName, LastName, Nickname);

    /// <summary>Alle Suchbegriffe (Vorname, Nachname, Rufname, Beziehung, Anzeigename) — für Suchfelder in der Oberfläche.</summary>
    public string SearchText => string.Join(" ", new[] { DisplayName, FirstName, LastName, Nickname, RelationName }
        .Where(s => !string.IsNullOrWhiteSpace(s)));

    /// <summary>Als Eingabe (zum Bearbeiten).</summary>
    public PersonDetailsInput ToInput() => new(FirstName, LastName, Nickname, RelationName, BirthDate, IsMe);
}

/// <summary>
/// Eingabe beim Anlegen/Bearbeiten. <see cref="Relation"/> ist Freitext: eine vorhandene Beziehung gleichen Namens (ohne
/// Groß-/Kleinschreibung) wird verwendet, sonst neu angelegt; leer = keine Beziehung.
/// </summary>
public sealed record PersonDetailsInput(
    string FirstName,
    string LastName,
    string? Nickname = null,
    string? Relation = null,
    PersonBirthDate? BirthDate = null,
    bool IsMe = false)
{
    public string DisplayName => PersonNames.DisplayName(FirstName, LastName, Nickname);

    /// <summary>Aus einem Einzelnamen (Lightbox, alte Wege): am ersten Leerzeichen geteilt.</summary>
    public static PersonDetailsInput FromName(string name)
    {
        var (first, last) = PersonNames.Split(name);
        return new PersonDetailsInput(first, last);
    }

    /// <summary>Getrimmt; leere optionale Felder → null.</summary>
    public PersonDetailsInput Normalized() => new(
        PersonNames.Clean(FirstName),
        PersonNames.Clean(LastName),
        PersonNames.CleanOrNull(Nickname),
        PersonNames.CleanOrNull(Relation),
        BirthDate,
        IsMe);
}

/// <summary>Eine Beziehung mit der Zahl der Personen, die sie tragen (Einstellungen, Vorschlagsliste).</summary>
public sealed record PersonRelationInfo(int Id, string Name, int PersonCount);

/// <summary>Ein Beziehungsname ist schon vergeben (ohne Groß-/Kleinschreibung eindeutig).</summary>
public sealed class RelationNameTakenException(string name, int existingRelationId)
    : InvalidOperationException($"Es gibt schon eine Beziehung „{name}“.")
{
    public int ExistingRelationId { get; } = existingRelationId;
}
