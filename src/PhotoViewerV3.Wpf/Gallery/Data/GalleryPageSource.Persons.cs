using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace PhotoViewerV3.Gallery.Data;

/// <summary>Verknüpfung im Personen-Filter der Galerie (0.2.38).</summary>
public enum GalleryPersonMode
{
    /// <summary>ODER: mindestens eine der gewählten Personen ist auf dem Foto.</summary>
    Any,

    /// <summary>UND: alle gewählten Personen sind auf dem Foto.</summary>
    All,

    /// <summary>Nur Fotos (keine Videos/Archive) ohne (bestätigte) Person — gewählte Personen werden ignoriert.</summary>
    NoPerson,

    /// <summary>Nur Dateien mit mindestens einem noch unbenannten Gesicht — gewählte Personen werden ignoriert.</summary>
    UnnamedFaces,

    /// <summary>
    /// Alle Dateien mit mindestens einem erkannten, nicht ignorierten Gesicht (Zustand 0, 1 oder 2) — gewählte Personen
    /// und „auch Vorschläge“ werden ignoriert (0.2.39, <c>GalleryPageSource.FaceMarks.cs</c>).
    /// </summary>
    AnyFace,
}

/// <summary>Hilfen zu <see cref="GalleryPersonMode"/> (0.2.39).</summary>
public static class GalleryPersonModeExtensions
{
    /// <summary>True für Modi, die ohne gewählte Personen wirken (ohne Person, unbenannte Gesichter, alle mit Gesichtern).</summary>
    public static bool IgnoresPersonSelection(this GalleryPersonMode mode)
        => mode is GalleryPersonMode.NoPerson or GalleryPersonMode.UnnamedFaces or GalleryPersonMode.AnyFace;
}

/// <summary>
/// Personen-Filter (0.2.38): nur bestätigte Gesichter (<c>Faces.State = 2</c> mit <c>PersonId</c>) zählen als Person;
/// mit <paramref name="IncludeSuggestions"/> zählen auch Vorschläge (<c>State = 1</c> mit <c>SuggestedPersonId</c>).
/// </summary>
/// <remarks>
/// Seit 0.2.47 (ADR-0045) zusätzlich <paramref name="RelationIds"/>: Foto zeigt eine Person mit einer der Beziehungen
/// (ODER zwischen den Beziehungen, UND mit dem Personen-Teil und allen anderen Filtern).
/// </remarks>
public sealed record GalleryPersonFilter(
    IReadOnlyList<int> PersonIds,
    GalleryPersonMode Mode,
    bool IncludeSuggestions = false,
    IReadOnlyList<int>? RelationIds = null)
{
    public bool IsActive => HasPersonPart || HasRelationPart;

    /// <summary>Der Personen-Teil (Modus bzw. gewählte Personen) wirkt.</summary>
    public bool HasPersonPart => Mode.IgnoresPersonSelection() || PersonIds.Count > 0;

    /// <summary>Der Beziehungs-Teil wirkt (0.2.47).</summary>
    public bool HasRelationPart => RelationIds is { Count: > 0 };
}

/// <summary>Eintrag des Personen-Filters: Person mit Anzahl Dateien der Bibliothek, auf denen sie bestätigt ist.</summary>
/// <param name="SearchText">Suchbegriffe für das Flyout: Anzeigename, Vor-/Nachname, Rufname, Beziehung (0.2.47).</param>
public sealed record GalleryPersonCount(int Id, string Name, bool IsHidden, int FileCount, string? SearchText = null);

/// <summary>Bestätigte Person bzw. Vorschlag auf einer Datei (Listenspalte, Kachel).</summary>
public sealed record GalleryFacePersonRow(int FileId, bool IsConfirmed, int PersonId, string Name);

/// <summary>
/// Personen in der Galerie-SQL (0.2.38, Gesichtserkennung Teil 4): Filter, Suche nach Personennamen und die Namen je
/// Datei für Liste und Kachel — direkt über <c>Faces</c>/<c>Persons</c>. Alle Prüfungen laufen über die abdeckenden
/// Indizes der Migration <c>AddFacePersonIndex</c>: State/PersonId/SuggestedPersonId liegen im Datensatz hinter dem
/// 2-KB-Fingerabdruck, ein Zugriff auf die Tabellenzeile läse dessen Überlaufseiten.
/// </summary>
public sealed partial class GalleryPageSource
{
    /// <summary>Bestätigt = Zustand 2 mit Person (eine gelöschte Person setzt PersonId auf NULL, ADR-0035).</summary>
    private const string ConfirmedCondition = "fa.State = 2 AND fa.PersonId IS NOT NULL";

    /// <summary>Vorschlag = Zustand 1 mit vorgeschlagener Person.</summary>
    private const string SuggestedCondition = "fa.State = 1 AND fa.SuggestedPersonId IS NOT NULL";

    /// <summary>
    /// Such-Zweig für Personennamen (Teilstring, NOCASE-Spalte, LIKE wie Tags): Personen-Ids aus der kleinen
    /// Persons-Tabelle, bestätigte Gesichter über <c>IX_Faces_Person_State_File</c>.
    /// </summary>
    /// <remarks>Seit 0.2.47 (ADR-0045) auch Vorname, Nachname, Rufname und Beziehung — Persons ist klein, LIKE '%…%' ohne Index.</remarks>
    private const string PersonSearchUnion = """
        UNION
        SELECT fp.FileId FROM Faces fp
        WHERE fp.PersonId IN (SELECT pp.Id FROM Persons pp
                              WHERE pp.Name LIKE $tagLike ESCAPE '\' OR pp.FirstName LIKE $tagLike ESCAPE '\'
                                 OR pp.LastName LIKE $tagLike ESCAPE '\' OR pp.Nickname LIKE $tagLike ESCAPE '\'
                                 OR pp.RelationId IN (SELECT pr.Id FROM PersonRelations pr WHERE pr.Name LIKE $tagLike ESCAPE '\'))
          AND fp.State = 2
        """;

    /// <summary>
    /// Personen mit der Zahl der Dateien dieser Bibliothek, auf denen sie bestätigt sind (Filter-Dropdown), nach Name.
    /// Je Person eine Index-Suche über <c>IX_Faces_Person_State_File</c> plus Primärschlüssel-Zugriff auf Files.
    /// </summary>
    public Task<IReadOnlyList<GalleryPersonCount>> GetPersonCountsAsync(int libraryId, CancellationToken ct)
        => GetPersonCountsAsync([libraryId], ct);

    /// <summary>
    /// Wie oben für einen Bereich (ADR-0048): nur Personen der Gruppe der Bibliotheken, gezählt über alle Bibliotheken
    /// des Bereichs — Personen einer anderen Bibliothek erscheinen nie (auch nicht mit 0).
    /// </summary>
    public Task<IReadOnlyList<GalleryPersonCount>> GetPersonCountsAsync(IReadOnlyList<int> libraryIds, CancellationToken ct)
        => RunAsync<IReadOnlyList<GalleryPersonCount>>(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT p.Id, p.Name, p.IsHidden,
                       (SELECT COUNT(DISTINCT fa.FileId)
                          FROM Faces fa JOIN Files f ON f.Id = fa.FileId
                         WHERE fa.PersonId = p.Id AND fa.State = 2 AND {ScopeCondition(libraryIds, "f.LibraryId")}) AS FileCount,
                       p.FirstName, p.LastName, p.Nickname, r.Name
                FROM Persons p LEFT JOIN PersonRelations r ON r.Id = p.RelationId
                WHERE p.GroupId = {GroupOfScopeSql(libraryIds)}
                ORDER BY p.Name COLLATE NOCASE, p.Id;
                """;
            cmd.Parameters.AddWithValue("$libId", libraryIds.Count > 0 ? libraryIds[0] : -1);
            var result = new List<GalleryPersonCount>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                // Suchbegriffe: Anzeigename, Vorname, Nachname, Rufname, Beziehung (0.2.47)
                var search = string.Join(" ", new[] { 1, 4, 5, 6, 7 }
                    .Where(i => !reader.IsDBNull(i)).Select(reader.GetString).Where(t => t.Length > 0));
                result.Add(new GalleryPersonCount(reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2) == 1, reader.GetInt32(3), search));
            }

            return result;
        }, ct);

    /// <summary>True, wenn in der Bibliothek schon Gesichter erkannt wurden (Personen-Filter einblenden).</summary>
    public Task<bool> HasFacesAsync(int libraryId, CancellationToken ct)
        => HasFacesAsync([libraryId], ct);

    /// <summary>Wie oben für einen Bereich (ADR-0048).</summary>
    public Task<bool> HasFacesAsync(IReadOnlyList<int> libraryIds, CancellationToken ct)
        => RunAsync(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT EXISTS (SELECT 1 FROM Faces fa JOIN Files f ON f.Id = fa.FileId WHERE {ScopeCondition(libraryIds, "f.LibraryId")});";
            cmd.Parameters.AddWithValue("$libId", libraryIds.Count > 0 ? libraryIds[0] : -1);
            return Convert.ToInt64(cmd.ExecuteScalar()) == 1;
        }, ct);

    /// <summary>
    /// Bestätigte Personen und Vorschläge je Datei (Listenspalte „Personen“, Kachel-Symbol) — eine Abfrage je 500
    /// Dateien über <c>IX_Faces_File_State_Person</c>. Eine Person, die auf einem Foto bestätigt ist, erscheint dort
    /// nicht zusätzlich als Vorschlag; mehrere Gesichter derselben Person zählen einmal. Seit 0.2.39 liefert
    /// <see cref="GetFaceMarksForFilesAsync"/> aus derselben Abfrage zusätzlich die unbenannten Gesichter je Datei.
    /// </summary>
    public Task<IReadOnlyList<GalleryFacePersonRow>> GetPersonsForFilesAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct)
        => RunAsync(conn => ReadFaceMarks(conn, fileIds, ct).Persons, ct);

    /// <summary>
    /// Personen, Vorschläge und Anzahl unbenannter Gesichter je Datei — eine Abfrage je 500 Dateien über
    /// <c>IX_Faces_File_State_Person</c> (Zustand 0, 1, 2; ignorierte Gesichter werden nicht gelesen).
    /// </summary>
    private static GalleryFaceMarks ReadFaceMarks(SqliteConnection conn, IReadOnlyCollection<int> fileIds, CancellationToken ct)
    {
        var result = new List<GalleryFacePersonRow>();
        var unnamed = new Dictionary<int, int>();
        foreach (var chunk in fileIds.Distinct().Chunk(InChunkSize))
        {
            ct.ThrowIfCancellationRequested();
            using var cmd = conn.CreateCommand();
            var inList = BindInList(cmd, chunk, "$pf");
            cmd.CommandText = $"""
                SELECT fa.FileId, fa.State, fa.PersonId, p.Name, fa.SuggestedPersonId, s.Name
                FROM Faces fa
                LEFT JOIN Persons p ON p.Id = fa.PersonId
                LEFT JOIN Persons s ON s.Id = fa.SuggestedPersonId
                WHERE fa.FileId IN ({inList}) AND fa.State IN (0, 1, 2);
                """;
            var confirmed = new HashSet<(int, int)>();
            var suggested = new List<(int FileId, int PersonId, string Name)>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var fileId = reader.GetInt32(0);
                var state = reader.GetInt32(1);
                if (state != 2 || reader.IsDBNull(2))
                {
                    // Unbenannt wie im Filter „unbenannte Gesichter“: unbestätigt, Vorschlag, bestätigt ohne (gelöschte) Person.
                    unnamed[fileId] = unnamed.GetValueOrDefault(fileId) + 1;
                }

                if (state == 2 && !reader.IsDBNull(2) && !reader.IsDBNull(3))
                {
                    if (confirmed.Add((fileId, reader.GetInt32(2))))
                    {
                        result.Add(new GalleryFacePersonRow(fileId, true, reader.GetInt32(2), reader.GetString(3)));
                    }
                }
                else if (state == 1 && !reader.IsDBNull(4) && !reader.IsDBNull(5))
                {
                    suggested.Add((fileId, reader.GetInt32(4), reader.GetString(5)));
                }
            }

            var seenSuggestions = new HashSet<(int, int)>();
            foreach (var (fileId, personId, name) in suggested)
            {
                if (!confirmed.Contains((fileId, personId)) && seenSuggestions.Add((fileId, personId)))
                {
                    result.Add(new GalleryFacePersonRow(fileId, false, personId, name));
                }
            }
        }

        return new GalleryFaceMarks(result, unnamed);
    }

    /// <summary>
    /// Personen-Filter als Bedingung auf <c>f.Id</c>. ODER/UND: Gesichter der gewählten Personen über
    /// <c>IX_Faces_Person_State_File</c> (Vorschläge über <c>IX_Faces_SuggestedPerson_State_File</c>); „ohne Person“ und
    /// „unbenannte Gesichter“: je Datei eine abdeckende Index-Suche in <c>IX_Faces_File_State_Person</c>.
    /// Parameter $person{i} und $personCount.
    /// </summary>
    private static string BuildPersonClause(GalleryQuery query)
        => query.PersonFilter is not { IsActive: true } filter
            ? string.Empty
            : (BuildPersonPartClause(filter) + " " + BuildRelationClause(filter)).Trim(); // Beziehung: GalleryPageSource.Relations.cs

    private static string BuildPersonPartClause(GalleryPersonFilter filter)
    {
        if (!filter.HasPersonPart)
        {
            return string.Empty;
        }

        switch (filter.Mode)
        {
            case GalleryPersonMode.NoPerson:
                // Nur Fotos: Videos und Archive analysiert die Gesichtserkennung nicht (ADR-0035).
                return filter.IncludeSuggestions
                    ? $"AND f.MediaType = 0 AND NOT EXISTS (SELECT 1 FROM Faces fa WHERE fa.FileId = f.Id AND (({ConfirmedCondition}) OR ({SuggestedCondition})))"
                    : $"AND f.MediaType = 0 AND NOT EXISTS (SELECT 1 FROM Faces fa WHERE fa.FileId = f.Id AND {ConfirmedCondition})";
            case GalleryPersonMode.UnnamedFaces:
                // Unbenannt = weder bestätigt noch ignoriert; mit „auch Vorschläge“ gilt ein Vorschlag schon als Name.
                return filter.IncludeSuggestions
                    ? "AND EXISTS (SELECT 1 FROM Faces fa WHERE fa.FileId = f.Id AND (fa.State = 0 OR (fa.State = 1 AND fa.SuggestedPersonId IS NULL) OR (fa.State = 2 AND fa.PersonId IS NULL)))"
                    : "AND EXISTS (SELECT 1 FROM Faces fa WHERE fa.FileId = f.Id AND (fa.State IN (0, 1) OR (fa.State = 2 AND fa.PersonId IS NULL)))";
            case GalleryPersonMode.AnyFace:
                return AnyFaceClause; // 0.2.39, GalleryPageSource.FaceMarks.cs
        }

        var inList = string.Join(",", Enumerable.Range(0, filter.PersonIds.Count).Select(i => "$person" + i));
        var confirmed = $"SELECT fa.FileId{(filter.Mode == GalleryPersonMode.All ? ", fa.PersonId AS P" : string.Empty)} FROM Faces fa WHERE fa.PersonId IN ({inList}) AND fa.State = 2";
        var suggested = $"SELECT fs.FileId{(filter.Mode == GalleryPersonMode.All ? ", fs.SuggestedPersonId AS P" : string.Empty)} FROM Faces fs WHERE fs.SuggestedPersonId IN ({inList}) AND fs.State = 1";
        if (filter.Mode == GalleryPersonMode.All)
        {
            var source = filter.IncludeSuggestions ? $"{confirmed} UNION ALL {suggested}" : confirmed;
            return $"AND f.Id IN (SELECT x.FileId FROM ({source}) x GROUP BY x.FileId HAVING COUNT(DISTINCT x.P) = $personCount)";
        }

        return filter.IncludeSuggestions
            ? $"AND f.Id IN ({confirmed} UNION {suggested})"
            : $"AND f.Id IN ({confirmed})";
    }

    private static void BindPersonParams(SqliteCommand cmd, GalleryQuery query)
    {
        if (query.PersonFilter is not { IsActive: true } filter)
        {
            return;
        }

        BindRelationParams(cmd, filter); // 0.2.47, GalleryPageSource.Relations.cs
        if (filter.Mode.IgnoresPersonSelection() || filter.PersonIds.Count == 0)
        {
            return;
        }

        for (var i = 0; i < filter.PersonIds.Count; i++)
        {
            cmd.Parameters.AddWithValue("$person" + i, filter.PersonIds[i]);
        }

        if (filter.Mode == GalleryPersonMode.All)
        {
            cmd.Parameters.AddWithValue("$personCount", filter.PersonIds.Distinct().Count());
        }
    }

    private static string BindInList(SqliteCommand cmd, IReadOnlyList<int> ids, string prefix)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < ids.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            var name = prefix + i;
            sb.Append(name);
            cmd.Parameters.AddWithValue(name, ids[i]);
        }

        return sb.ToString();
    }
}
