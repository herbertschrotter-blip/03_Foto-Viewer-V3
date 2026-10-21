namespace Lumora.Services.Library;

/// <summary>
/// Bereichsdienst der Bibliotheks-Trennung (ADR-0048). Liefert den aktuellen Bereich (aktive Bibliothek bzw. „Alle
/// verknüpften“ ihrer Gruppe) frisch aus dem Katalog — kein Zwischenstand, der veralten könnte; die Abfrage liest nur die
/// kleine Tabelle Libraries. Alle Dienste und Abfragen, die zeigen, suchen, zählen, vorschlagen oder lernen, holen sich
/// hier ihre Bibliotheks-Ids und ihre Gruppe. Eine Bibliothek ohne Gruppe (GroupId 0, z. B. direkt angelegt) bekommt beim
/// ersten Zugriff ihre eigene; Begriffe ohne Gruppe (angelegt, als es noch keine Bibliothek gab) übernimmt sie dabei.
/// </summary>
public interface ILibraryScope
{
    /// <summary>Aktueller Bereich; <see cref="LibraryScopeInfo.None"/> ohne Bibliothek.</summary>
    Task<LibraryScopeInfo> GetCurrentAsync(CancellationToken ct = default);

    /// <summary>Bereich genau dieser einen Bibliothek (mit ihrer Gruppe) — für Arbeit an einer bekannten Bibliothek.</summary>
    Task<LibraryScopeInfo> GetForLibraryAsync(int libraryId, CancellationToken ct = default);

    /// <summary>Alle Gruppen mit ihren Bibliotheken (Bibliotheken ohne Gruppe bekommen vorher eine).</summary>
    Task<IReadOnlyList<LibraryGroupInfo>> GetGroupsAsync(CancellationToken ct = default);

    /// <summary>
    /// Ansicht „Alle verknüpften“ der Gruppe öffnen: ist die aktive Bibliothek nicht in der Gruppe, wird die zuletzt
    /// geöffnete der Gruppe aktiv. Sendet <c>ActiveLibraryChangedMessage</c> (alles lädt neu). Wirft bei einer Gruppe mit
    /// weniger als zwei Bibliotheken und in einem versteckten Katalog. Zurück zu einer einzelnen Bibliothek geht es über
    /// <see cref="ILibraryService.SetActiveAsync"/> (setzt die Ansicht zurück).
    /// </summary>
    Task ShowAllLinkedAsync(int groupId, CancellationToken ct = default);

    /// <summary>Gruppe umbenennen (Anzeige „Alle verknüpften (Name)“); sendet <see cref="LibraryGroupsChangedMessage"/>.</summary>
    Task RenameGroupAsync(int groupId, string name, CancellationToken ct = default);
}
