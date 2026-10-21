using System.Globalization;

namespace Lumora.Services.Library;

/// <summary>
/// Der aktuelle Bibliotheks-Bereich (ADR-0048): die aktive Bibliothek bzw. — in der Ansicht „Alle verknüpften“ — alle
/// Bibliotheken ihrer Verknüpfungsgruppe. Alles, was die App zeigt, sucht, zählt, vorschlägt oder lernt, bleibt in diesem
/// Bereich: Dateien über <c>Files.LibraryId IN (</c><see cref="LibraryIds"/><c>)</c>, Begriffe (Tags, Kategorien, Personen,
/// Beziehungen) über <c>GroupId = </c><see cref="GroupId"/>.
/// </summary>
/// <param name="LibraryId">Aktive Bibliothek (0 = keine Bibliothek). In „Alle verknüpften“ die zuletzt geöffnete der Gruppe.</param>
/// <param name="GroupId">Gruppe der aktiven Bibliothek (0 = ohne Bibliothek: Begriffe, die angelegt wurden, als es noch keine gab).</param>
/// <param name="GroupName">Anzeigename der Gruppe („Alle verknüpften (Name)“).</param>
/// <param name="LibraryIds">Bibliotheken des Bereichs (eine, alle der Gruppe, oder keine).</param>
/// <param name="GroupLibraryIds">Alle Bibliotheken der Gruppe (≥ 2 = verknüpft).</param>
/// <param name="IsAllLinked">true in der Ansicht „Alle verknüpften“ (nur bei einer Gruppe mit ≥ 2 Bibliotheken).</param>
public sealed record LibraryScopeInfo(
    int LibraryId,
    int GroupId,
    string GroupName,
    IReadOnlyList<int> LibraryIds,
    IReadOnlyList<int> GroupLibraryIds,
    bool IsAllLinked)
{
    /// <summary>Kein Bereich (keine Bibliothek).</summary>
    public static LibraryScopeInfo None { get; } = new(0, 0, string.Empty, [], [], false);

    /// <summary>Bereich genau einer Bibliothek (Tests, Hintergrundarbeit für eine bekannte Bibliothek).</summary>
    public static LibraryScopeInfo ForLibrary(int libraryId, int groupId, string groupName = "")
        => new(libraryId, groupId, groupName, [libraryId], [libraryId], false);

    public bool HasLibrary => LibraryId != 0 && LibraryIds.Count > 0;

    /// <summary>true, wenn die Gruppe mehr als eine Bibliothek hat (Eintrag „Alle verknüpften“ möglich).</summary>
    public bool IsLinked => GroupLibraryIds.Count > 1;

    /// <summary>Gehört die Bibliothek zum Bereich?</summary>
    public bool Contains(int libraryId) => LibraryIds.Contains(libraryId);

    /// <summary>Schlüssel für Zwischenspeicher (Modell, Gruppen-Vorschläge): ändert sich mit jedem anderen Bereich.</summary>
    public string Key => IsAllLinked
        ? "G" + GroupId.ToString(CultureInfo.InvariantCulture) + ":" + string.Join(",", LibraryIds)
        : "L" + LibraryId.ToString(CultureInfo.InvariantCulture) + ":" + GroupId.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Bibliotheks-Ids als SQL-Liste für <c>IN (…)</c> — nur Ganzzahlen aus dem Katalog, daher sicher einzubetten; ohne
    /// Bibliothek „-1“ (trifft nichts).
    /// </summary>
    public string SqlLibraryIdList => LibraryIds.Count == 0
        ? "-1"
        : string.Join(",", LibraryIds.Select(id => id.ToString(CultureInfo.InvariantCulture)));
}

/// <summary>Eine Verknüpfungsgruppe mit ihren Bibliotheken (Einstellungen, Umschalter).</summary>
public sealed record LibraryGroupInfo(int Id, string Name, IReadOnlyList<int> LibraryIds)
{
    public bool IsLinked => LibraryIds.Count > 1;
}

/// <summary>Gleichnamige Personen zweier Gruppen, die das Verknüpfen zusammenführen würde (Rückfrage, ADR-0048).</summary>
/// <param name="Name">Anzeigename (ohne Groß-/Kleinschreibung gleich).</param>
/// <param name="TargetPersonId">Person der Gruppe, die bleibt.</param>
/// <param name="SourcePersonId">Person der hinzukommenden Gruppe.</param>
public sealed record LibraryLinkPersonConflict(string Name, int TargetPersonId, int SourcePersonId);

/// <summary>Was Verknüpfen bzw. Lösen tun würde — für den Bestätigungsdialog.</summary>
public sealed record LibraryLinkPreview(
    int LibraryId,
    string LibraryName,
    int OtherLibraryId,
    string OtherLibraryName,
    int TagsMerged,
    int TagsMoved,
    int CategoriesMerged,
    int CategoriesMoved,
    int RelationsMerged,
    int RelationsMoved,
    int PersonsMoved,
    IReadOnlyList<LibraryLinkPersonConflict> PersonConflicts);

/// <summary>Was Lösen kopieren würde — für den Bestätigungsdialog.</summary>
public sealed record LibraryUnlinkPreview(
    int LibraryId,
    string LibraryName,
    string GroupName,
    IReadOnlyList<string> RemainingLibraryNames,
    int Tags,
    int Categories,
    int Relations,
    int Persons);

/// <summary>Ergebnis des Verknüpfens (Zähler für Log und Meldung).</summary>
public sealed record LibraryLinkResult(int GroupId, int TagsMerged, int CategoriesMerged, int RelationsMerged, int PersonsMerged, int PersonsKeptApart, int MotifTermsMerged = 0);

/// <summary>Ergebnis des Lösens.</summary>
public sealed record LibraryUnlinkResult(int NewGroupId, int TagsCopied, int CategoriesCopied, int RelationsCopied, int PersonsCopied, int MotifTermsCopied = 0);

/// <summary>
/// Verknüpfungen haben sich geändert (verknüpft, gelöst, Gruppe umbenannt) — Umschalter und Einstellungen lesen neu.
/// Begriffe, Personen und Zuordnungen können neue Ids haben: wer etwas zwischenspeichert, verwirft es (die Dienste senden
/// zusätzlich <c>ActiveLibraryChangedMessage</c>, damit Galerie, Ordner und Personen neu laden).
/// <paramref name="TermsChanged"/> = false bei einer reinen Umbenennung der Gruppe (Ids unverändert, kein Neu-Lernen).
/// </summary>
public sealed record LibraryGroupsChangedMessage(IReadOnlyList<int> LibraryIds, bool TermsChanged = true);

/// <summary>Verknüpfen ist in diesem Katalog nicht erlaubt (versteckte Bibliothek, ADR-0048).</summary>
public sealed class LibraryLinkNotAllowedException(string message) : InvalidOperationException(message);
