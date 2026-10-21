using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace PhotoViewerV3.Gallery.Data;

/// <summary>Eintrag des Beziehungs-Filters: Beziehung mit Anzahl Dateien der Bibliothek, auf denen eine ihrer Personen bestätigt ist.</summary>
public sealed record GalleryRelationCount(int Id, string Name, int FileCount);

/// <summary>
/// Filter nach Beziehung (0.2.47, ADR-0045) im Personen-Flyout: Fotos mit mindestens einer (bestätigten, mit „auch
/// Vorschläge“ auch vorgeschlagenen) Person, deren Beziehung gewählt ist. Die Personen der Beziehungen kommen über
/// <c>IX_Persons_Relation</c> aus der kleinen Persons-Tabelle, die Gesichter über <c>IX_Faces_Person_State_File</c> bzw.
/// <c>IX_Faces_SuggestedPerson_State_File</c>. Parameter $rel{i}.
/// </summary>
public sealed partial class GalleryPageSource
{
    /// <summary>Beziehungen, die mindestens eine Person trägt, mit Zahl der Dateien dieser Bibliothek, nach Name.</summary>
    public Task<IReadOnlyList<GalleryRelationCount>> GetRelationCountsAsync(int libraryId, CancellationToken ct)
        => RunAsync<IReadOnlyList<GalleryRelationCount>>(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT r.Id, r.Name,
                       (SELECT COUNT(DISTINCT fa.FileId)
                          FROM Faces fa JOIN Files f ON f.Id = fa.FileId
                         WHERE fa.PersonId IN (SELECT p.Id FROM Persons p WHERE p.RelationId = r.Id)
                           AND fa.State = 2 AND f.LibraryId = $libId) AS FileCount
                FROM PersonRelations r
                WHERE EXISTS (SELECT 1 FROM Persons p WHERE p.RelationId = r.Id)
                ORDER BY r.Name COLLATE NOCASE, r.Id;
                """;
            cmd.Parameters.AddWithValue("$libId", libraryId);
            var result = new List<GalleryRelationCount>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                result.Add(new GalleryRelationCount(reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2)));
            }

            return result;
        }, ct);

    private static string BuildRelationClause(GalleryPersonFilter filter)
    {
        if (!filter.HasRelationPart)
        {
            return string.Empty;
        }

        var inList = string.Join(",", Enumerable.Range(0, filter.RelationIds!.Count).Select(i => "$rel" + i));
        var persons = $"SELECT pr.Id FROM Persons pr WHERE pr.RelationId IN ({inList})";
        var confirmed = $"SELECT fr.FileId FROM Faces fr WHERE fr.PersonId IN ({persons}) AND fr.State = 2";
        return filter.IncludeSuggestions
            ? $"AND f.Id IN ({confirmed} UNION SELECT fq.FileId FROM Faces fq WHERE fq.SuggestedPersonId IN ({persons}) AND fq.State = 1)"
            : $"AND f.Id IN ({confirmed})";
    }

    private static void BindRelationParams(SqliteCommand cmd, GalleryPersonFilter filter)
    {
        if (!filter.HasRelationPart)
        {
            return;
        }

        for (var i = 0; i < filter.RelationIds!.Count; i++)
        {
            cmd.Parameters.AddWithValue("$rel" + i, filter.RelationIds[i]);
        }
    }
}
