using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace PhotoViewerV3.Gallery.Data;

/// <summary>Verknüpfung im Motiv-Filter (ADR-0052).</summary>
public enum GalleryMotifMode
{
    /// <summary>Eines der gewählten Motive ist auf dem Foto.</summary>
    Any,

    /// <summary>Alle gewählten Motive sind auf dem Foto.</summary>
    All,
}

/// <summary>Motiv-Filter: gewählte Begriffe (MotifTerms.Id) mit ODER bzw. UND.</summary>
public sealed record GalleryMotifFilter(IReadOnlyList<int> TermIds, GalleryMotifMode Mode = GalleryMotifMode.Any)
{
    public bool IsActive => TermIds.Count > 0;
}

/// <summary>Eintrag des Motiv-Filters: Begriff mit Anzahl Dateien (Treffer) im Bereich.</summary>
public sealed record GalleryMotifCount(int Id, string Name, int FileCount);

/// <summary>Treffer einer Datei für Liste und Kachel.</summary>
public sealed record GalleryFileMotifRow(int FileId, string Name, float Score);

/// <summary>Motiv-Teil der Galerie-Abfrage (ADR-0052); als eigener Teil des Records, damit die Positionsparameter bleiben.</summary>
public sealed partial record GalleryQuery
{
    /// <summary>Präfix im Suchfeld, das nur Motive durchsucht („Motiv: Hund“).</summary>
    public const string MotifSearchPrefix = "Motiv:";

    public GalleryMotifFilter? MotifFilter { get; init; }

    /// <summary>
    /// Freitext-Bildsuche („Hund am Strand“): die FileIds der ähnlichsten Bild-Vektoren (vorab über den Text-Vektor
    /// ermittelt, höchstens ein paar Hundert); leer = keine Treffer, null = keine Bildinhalt-Suche.
    /// </summary>
    public IReadOnlyList<int>? ContentSearchIds { get; init; }

    /// <summary>Motiv-Filter oder Bildinhalt-Suche wirkt.</summary>
    public bool HasMotifFilter => MotifFilter is { IsActive: true } || ContentSearchIds is not null;

    /// <summary>Suchtext nach „Motiv:“ (getrimmt) oder null, wenn die Suche nicht mit dem Präfix beginnt.</summary>
    public string? MotifOnlySearchTerm
        => SearchTerm is { } term && term.TrimStart().StartsWith(MotifSearchPrefix, StringComparison.OrdinalIgnoreCase)
            ? term.TrimStart()[MotifSearchPrefix.Length..].Trim() is { Length: > 0 } rest ? rest : null
            : null;
}

/// <summary>
/// Motive in der Galerie-SQL (ADR-0052): Filter (ODER/UND über <c>IX_FileMotifs_Term_File</c>), Suche nach
/// Begriffsnamen als weiterer UNION-Zweig (nur Begriffe des Bereichs) bzw. „Motiv: …“ als reine Motiv-Suche, Zähler je
/// Begriff und Treffer je Datei für Liste und Kachel. Begriffe gehören der Gruppe der Bibliothek (ADR-0048, seit 0.2.56
/// <c>MotifTerms.GroupId</c>); <c>$libId</c> ist immer die aktive Bibliothek des Bereichs und damit in der Gruppe.
/// </summary>
public sealed partial class GalleryPageSource
{
    /// <summary>Such-Zweig: Dateien, deren Motiv-Begriff (dieses Bereichs) den Suchtext enthält.</summary>
    private const string MotifSearchUnion = """
        UNION
        SELECT fm.FileId FROM FileMotifs fm
        WHERE fm.TermId IN (SELECT mt.Id FROM MotifTerms mt WHERE mt.GroupId = (SELECT ml.GroupId FROM Libraries ml WHERE ml.Id = $libId) AND mt.Name LIKE $tagLike ESCAPE '\')
        """;

    /// <summary>Quelle für „Motiv: Hund“: nur die Motiv-Treffer, kein Dateiname/Tag/Person.</summary>
    private const string MotifOnlySource = """
        (SELECT fm.FileId AS Id FROM FileMotifs fm
         WHERE fm.TermId IN (SELECT mt.Id FROM MotifTerms mt WHERE mt.GroupId = (SELECT ml.GroupId FROM Libraries ml WHERE ml.Id = $libId) AND mt.Name LIKE $motifLike ESCAPE '\')
        ) hit
        CROSS JOIN Files f ON f.Id = hit.Id
        """;

    private static string BuildMotifClause(GalleryQuery query)
    {
        var content = query.ContentSearchIds switch
        {
            null => string.Empty,
            { Count: 0 } => "AND 0",
            { } ids => $"AND f.Id IN ({string.Join(",", Enumerable.Range(0, ids.Count).Select(i => "$content" + i))})",
        };
        if (query.MotifFilter is not { IsActive: true } filter)
        {
            return content;
        }

        var inList = string.Join(",", Enumerable.Range(0, filter.TermIds.Count).Select(i => "$motif" + i));
        return (filter.Mode == GalleryMotifMode.All
            ? $"AND f.Id IN (SELECT fm.FileId FROM FileMotifs fm WHERE fm.TermId IN ({inList}) GROUP BY fm.FileId HAVING COUNT(DISTINCT fm.TermId) = $motifCount)"
            : $"AND f.Id IN (SELECT fm.FileId FROM FileMotifs fm WHERE fm.TermId IN ({inList}))") + " " + content;
    }

    private static void BindMotifParams(SqliteCommand cmd, GalleryQuery query)
    {
        if (query.MotifOnlySearchTerm is { } motifTerm)
        {
            cmd.Parameters.AddWithValue("$motifLike", BuildTagLikePattern(motifTerm));
        }

        if (query.ContentSearchIds is { } contentIds)
        {
            for (var i = 0; i < contentIds.Count; i++)
            {
                cmd.Parameters.AddWithValue("$content" + i, contentIds[i]);
            }
        }

        if (query.MotifFilter is not { IsActive: true } filter)
        {
            return;
        }

        for (var i = 0; i < filter.TermIds.Count; i++)
        {
            cmd.Parameters.AddWithValue("$motif" + i, filter.TermIds[i]);
        }

        if (filter.Mode == GalleryMotifMode.All)
        {
            cmd.Parameters.AddWithValue("$motifCount", filter.TermIds.Distinct().Count());
        }
    }

    /// <summary>Begriffe der Bibliothek (ihrer Gruppe) mit Anzahl Dateien dieser Bibliothek, in der Reihenfolge der Verwaltung.</summary>
    public Task<IReadOnlyList<GalleryMotifCount>> GetMotifCountsAsync(int libraryId, CancellationToken ct)
        => GetMotifCountsAsync([libraryId], ct);

    /// <summary>
    /// Begriffe der Gruppe des Bereichs mit Anzahl Dateien IM Bereich (ADR-0048): eine verknüpfte Bibliothek einzeln zählt
    /// nur ihre eigenen Treffer (über Files), deckt der Bereich die ganze Gruppe ab, zählt der Index allein. Begriffe einer
    /// anderen Gruppe erscheinen nie (auch nicht mit 0).
    /// </summary>
    public Task<IReadOnlyList<GalleryMotifCount>> GetMotifCountsAsync(IReadOnlyList<int> libraryIds, CancellationToken ct)
        => RunAsync<IReadOnlyList<GalleryMotifCount>>(conn =>
        {
            if (libraryIds.Count == 0)
            {
                return [];
            }

            using var cmd = conn.CreateCommand();
            cmd.Parameters.AddWithValue("$libId", libraryIds[0]);
            using (var probe = conn.CreateCommand())
            {
                // Bibliotheken der Gruppe, die NICHT im Bereich sind → Treffer über Files auf den Bereich beschränken
                probe.CommandText = $"SELECT COUNT(*) FROM Libraries WHERE GroupId = {GroupOfScopeSql(libraryIds)} AND Id NOT IN ({string.Join(",", libraryIds)});";
                probe.Parameters.AddWithValue("$libId", libraryIds[0]);
                var partial = Convert.ToInt64(probe.ExecuteScalar() ?? 0L, System.Globalization.CultureInfo.InvariantCulture) > 0;
                var count = partial
                    ? $"(SELECT COUNT(*) FROM FileMotifs m JOIN Files f ON f.Id = m.FileId WHERE m.TermId = t.Id AND {ScopeCondition(libraryIds, "f.LibraryId")})"
                    : "(SELECT COUNT(*) FROM FileMotifs m WHERE m.TermId = t.Id)";
                cmd.CommandText = $"""
                    SELECT t.Id, t.Name, {count}
                    FROM MotifTerms t WHERE t.GroupId = {GroupOfScopeSql(libraryIds)}
                    ORDER BY t.SortOrder, t.Name COLLATE NOCASE, t.Id;
                    """;
            }

            var result = new List<GalleryMotifCount>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                result.Add(new GalleryMotifCount(reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2)));
            }

            return result;
        }, ct);

    /// <summary>Treffer je Datei (höchste Wahrscheinlichkeit zuerst) — eine Abfrage je 500 Dateien über den Primärschlüssel.</summary>
    public Task<IReadOnlyList<GalleryFileMotifRow>> GetMotifsForFilesAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct)
        => RunAsync<IReadOnlyList<GalleryFileMotifRow>>(conn =>
        {
            var result = new List<GalleryFileMotifRow>();
            foreach (var chunk in fileIds.Distinct().Chunk(InChunkSize))
            {
                ct.ThrowIfCancellationRequested();
                using var cmd = conn.CreateCommand();
                var inList = BindInList(cmd, chunk, "$mf");
                cmd.CommandText = $"""
                    SELECT m.FileId, t.Name, m.Score FROM FileMotifs m JOIN MotifTerms t ON t.Id = m.TermId
                    WHERE m.FileId IN ({inList}) ORDER BY m.FileId, m.Score DESC;
                    """;
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    result.Add(new GalleryFileMotifRow(reader.GetInt32(0), reader.GetString(1), reader.GetFloat(2)));
                }
            }

            return result;
        }, ct);
}
