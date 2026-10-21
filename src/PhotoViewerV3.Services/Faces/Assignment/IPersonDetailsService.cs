namespace PhotoViewerV3.Services.Faces.Assignment;

/// <summary>
/// Personen-Details und Beziehungen (0.2.47, ADR-0045). Umgesetzt vom <see cref="FaceRecognitionService"/> (derselbe
/// Single-Writer, dieselbe <see cref="PersonsChangedMessage"/> nach jeder Änderung). Der Anzeigename (Rufname, sonst
/// „Vorname Nachname“) ist ohne Groß-/Kleinschreibung eindeutig und steht synchron in <c>Persons.Name</c>.
/// </summary>
public interface IPersonDetailsService
{
    /// <summary>Details einer Person; null, wenn es sie nicht gibt.</summary>
    Task<PersonDetails?> GetPersonDetailsAsync(int personId, CancellationToken ct = default);

    /// <summary>Details aller Personen, nach Anzeigename.</summary>
    Task<IReadOnlyList<PersonDetails>> GetAllPersonDetailsAsync(CancellationToken ct = default);

    /// <summary>
    /// Legt eine Person an. Wirft <see cref="PersonNameTakenException"/>, wenn der Anzeigename vergeben ist, und
    /// <see cref="ArgumentException"/> bei leerem Anzeigenamen. „Das bin ich“ nimmt die Markierung anderen Personen.
    /// </summary>
    Task<int> CreatePersonAsync(PersonDetailsInput input, CancellationToken ct = default);

    /// <summary>Speichert alle Details (Anzeigename wird nachgezogen); Ausnahmen wie <see cref="CreatePersonAsync"/>.</summary>
    Task UpdatePersonDetailsAsync(int personId, PersonDetailsInput input, CancellationToken ct = default);

    /// <summary>Alle Beziehungen mit Anzahl Personen, nach Name.</summary>
    Task<IReadOnlyList<PersonRelationInfo>> GetRelationsAsync(CancellationToken ct = default);

    /// <summary>
    /// Benennt eine Beziehung um (wirkt bei allen Personen). Wirft <see cref="RelationNameTakenException"/>, wenn eine
    /// ANDERE Beziehung so heißt — dann ist <see cref="MergeRelationsAsync"/> der Weg.
    /// </summary>
    Task RenameRelationAsync(int relationId, string name, CancellationToken ct = default);

    /// <summary>Alle Personen von <paramref name="sourceId"/> bekommen <paramref name="targetId"/>; die Quelle wird gelöscht.</summary>
    Task MergeRelationsAsync(int sourceId, int targetId, CancellationToken ct = default);

    /// <summary>Löscht die Beziehung; ihre Personen behalten eine leere Beziehung.</summary>
    Task DeleteRelationAsync(int relationId, CancellationToken ct = default);
}
