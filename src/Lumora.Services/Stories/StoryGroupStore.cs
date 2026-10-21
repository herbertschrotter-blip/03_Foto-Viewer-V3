using Microsoft.Data.Sqlite;

namespace Lumora.Services.Stories;

/// <summary>
/// Storys beim Verknüpfen, Lösen und Entfernen von Bibliotheks-Gruppen (ADR-0058, Regeln aus ADR-0048/0054) — alles in
/// der Transaktion des Aufrufers (<c>LibraryLinkStore</c>, <c>LibraryGroupStore</c>). Danach rechnet die Erkennung beide
/// Gruppen neu (<c>LibraryGroupsChangedMessage</c>); der Benutzer-Zustand bleibt über die Signaturen erhalten.
/// <list type="bullet">
/// <item>Verknüpfen: Storys gleicher Signatur werden eins (Dateien vereinigt; die Zielgruppe behält ihren Zustand, fehlt
/// etwas — Titel, manuelles Titelbild, angeheftet, ausgeblendet — kommt es von der hinzukommenden), die übrigen wandern.</item>
/// <item>Lösen: die herausgelöste Bibliothek bekommt Kopien aller Storys, in denen Dateien von ihr stehen (mit Zustand,
/// Personen-Storys zeigen auf die kopierte Person); ihre Dateien wandern auf die Kopien. Was danach leer ist, geht.</item>
/// <item>Gruppe ohne Bibliothek: ihre Storys gehen mit.</item>
/// </list>
/// </summary>
internal static class StoryGroupStore
{
    /// <summary>Gruppe <paramref name="sourceGroup"/> geht in <paramref name="targetGroup"/> auf; liefert die Zahl zusammengeführter Storys.</summary>
    public static int Link(SqliteConnection connection, SqliteTransaction tx, int targetGroup, int sourceGroup)
    {
        var p = new (string, object?)[] { ("$t", targetGroup), ("$s", sourceGroup) };
        StorySql.Execute(connection, tx, "DROP TABLE IF EXISTS temp._StoryMap; CREATE TEMP TABLE _StoryMap (SourceId INTEGER NOT NULL PRIMARY KEY, TargetId INTEGER NOT NULL);");
        var merged = StorySql.Execute(connection, tx, """
            INSERT INTO _StoryMap
            SELECT s.Id, t.Id FROM Stories s JOIN Stories t ON t.GroupId = $t AND t.Signature = s.Signature AND t.Kind = s.Kind
            WHERE s.GroupId = $s;
            """, p);
        var targets = ReadIds(connection, tx, "SELECT TargetId FROM _StoryMap;");
        StorySql.Execute(connection, tx, """
            INSERT OR IGNORE INTO StoryItems (StoryId, FileId, Day, Origin, AddedAtUtc)
            SELECT m.TargetId, si.FileId, si.Day, si.Origin, si.AddedAtUtc FROM StoryItems si JOIN _StoryMap m ON m.SourceId = si.StoryId;
            UPDATE StoryItems SET Origin = (SELECT si.Origin FROM StoryItems si JOIN _StoryMap m ON m.SourceId = si.StoryId
                                            WHERE m.TargetId = StoryItems.StoryId AND si.FileId = StoryItems.FileId)
            WHERE Origin = 0 AND StoryId IN (SELECT TargetId FROM _StoryMap)
              AND EXISTS (SELECT 1 FROM StoryItems si JOIN _StoryMap m ON m.SourceId = si.StoryId
                          WHERE m.TargetId = StoryItems.StoryId AND si.FileId = StoryItems.FileId AND si.Origin <> 0);
            UPDATE Stories SET
                IsPinned = MAX(Stories.IsPinned, src.IsPinned),
                PinnedAtUtc = COALESCE(Stories.PinnedAtUtc, src.PinnedAtUtc),
                IsHidden = MAX(Stories.IsHidden, src.IsHidden),
                CustomTitle = COALESCE(Stories.CustomTitle, src.CustomTitle),
                CoverFileId = CASE WHEN Stories.CoverIsManual = 0 AND src.CoverIsManual = 1 THEN src.CoverFileId ELSE Stories.CoverFileId END,
                CoverIsManual = MAX(Stories.CoverIsManual, src.CoverIsManual)
            FROM _StoryMap m JOIN Stories src ON src.Id = m.SourceId
            WHERE Stories.Id = m.TargetId;
            UPDATE FileLinkUndo SET TargetId = (SELECT m.TargetId FROM _StoryMap m WHERE m.SourceId = FileLinkUndo.TargetId)
            WHERE Kind IN (2, 3) AND TargetId IN (SELECT SourceId FROM _StoryMap);
            DELETE FROM Stories WHERE Id IN (SELECT SourceId FROM _StoryMap);
            """);
        StorySql.Execute(connection, tx, "UPDATE Stories SET GroupId = $t WHERE GroupId = $s;", p);
        StorySql.Execute(connection, tx, "DROP TABLE temp._StoryMap;");
        StorySql.RefreshAggregates(connection, tx, targets, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        return merged;
    }

    /// <summary>
    /// Kopiert die Storys von <paramref name="fromGroup"/>, in denen Dateien der Bibliothek <paramref name="libraryId"/>
    /// stehen, nach <paramref name="toGroup"/> und hängt deren Einträge auf die Kopien um; liefert die Zahl kopierter Storys.
    /// Läuft vor dem Verwerfen der Personen-Zuordnung (<c>temp._CopyMap</c>, Art „P“) des Aufrufers, falls es sie gibt.
    /// </summary>
    public static int CopyForLibrary(SqliteConnection connection, SqliteTransaction tx, int fromGroup, int toGroup, int libraryId)
    {
        var p = new (string, object?)[] { ("$from", fromGroup), ("$to", toGroup), ("$lib", libraryId) };
        var personMap = StorySql.Scalar(connection, tx, "SELECT COUNT(*) FROM temp.sqlite_master WHERE name = '_CopyMap';") > 0
            ? "COALESCE((SELECT c.NewId FROM temp._CopyMap c WHERE c.Kind = 'P' AND c.OldId = s.PersonId), s.PersonId)"
            : "s.PersonId";
        StorySql.Execute(connection, tx, "DROP TABLE IF EXISTS temp._StoryCopyMap; CREATE TEMP TABLE _StoryCopyMap (OldId INTEGER NOT NULL PRIMARY KEY, NewId INTEGER NOT NULL);");
        var copied = StorySql.Execute(connection, tx, """
            INSERT INTO _StoryCopyMap
            SELECT s.Id,
                   MAX((SELECT COALESCE(MAX(Id), 0) FROM Stories), COALESCE((SELECT seq FROM sqlite_sequence WHERE name = 'Stories'), 0))
                   + ROW_NUMBER() OVER (ORDER BY s.Id)
            FROM Stories s
            WHERE s.GroupId = $from
              AND EXISTS (SELECT 1 FROM StoryItems si CROSS JOIN Files f ON f.Id = si.FileId WHERE si.StoryId = s.Id AND f.LibraryId = $lib);
            """, p);
        if (copied == 0)
        {
            StorySql.Execute(connection, tx, "DROP TABLE temp._StoryCopyMap;");
            return 0;
        }

        var olds = ReadIds(connection, tx, "SELECT OldId FROM _StoryCopyMap;");
        var news = ReadIds(connection, tx, "SELECT NewId FROM _StoryCopyMap;");
        StorySql.Execute(connection, tx, $"""
            INSERT INTO Stories (Id, GroupId, Kind, Signature, StartAt, EndAt, FirstDay, LastDay, DatePrecision, PlaceId, PersonId, PlaceIds,
                                 VisitCount, IsAway, CustomTitle, CoverFileId, CoverIsManual, IsPinned, PinnedAtUtc, IsHidden, IsSeen, SeenAtUtc,
                                 FileCount, VideoCount, CreatedAtUtc, UpdatedAtUtc)
            SELECT m.NewId, $to, s.Kind, s.Signature, s.StartAt, s.EndAt, s.FirstDay, s.LastDay, s.DatePrecision, s.PlaceId, {personMap}, s.PlaceIds,
                   s.VisitCount, s.IsAway, s.CustomTitle,
                   CASE WHEN EXISTS (SELECT 1 FROM Files f WHERE f.Id = s.CoverFileId AND f.LibraryId = $lib) THEN s.CoverFileId END,
                   CASE WHEN EXISTS (SELECT 1 FROM Files f WHERE f.Id = s.CoverFileId AND f.LibraryId = $lib) THEN s.CoverIsManual ELSE 0 END,
                   s.IsPinned, s.PinnedAtUtc, s.IsHidden, s.IsSeen, s.SeenAtUtc, s.FileCount, s.VideoCount, s.CreatedAtUtc, s.UpdatedAtUtc
            FROM _StoryCopyMap m JOIN Stories s ON s.Id = m.OldId;
            UPDATE Stories SET CoverFileId = NULL, CoverIsManual = 0
            WHERE Id IN (SELECT OldId FROM _StoryCopyMap) AND CoverFileId IN (SELECT Id FROM Files WHERE LibraryId = $lib);
            UPDATE StoryItems SET StoryId = (SELECT m.NewId FROM _StoryCopyMap m WHERE m.OldId = StoryItems.StoryId)
            WHERE StoryId IN (SELECT OldId FROM _StoryCopyMap) AND FileId IN (SELECT Id FROM Files WHERE LibraryId = $lib);
            DROP TABLE temp._StoryCopyMap;
            """, p);

        var touched = olds.Concat(news).ToList();
        StorySql.RefreshAggregates(connection, tx, touched, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        foreach (var id in touched)
        {
            if (StorySql.Scalar(connection, tx, "SELECT FileCount FROM Stories WHERE Id = $id;", ("$id", id)) == 0)
            {
                StoryPlanWriter.DeleteStory(connection, tx, id);
            }
        }

        return copied;
    }

    /// <summary>
    /// Vor dem Zusammenführen zweier Personen (Verknüpfen, ADR-0048): ihre Personen-Storys ziehen auf die bleibende Person
    /// um (Signatur „P:&lt;Person&gt;:…“ mit), sonst nähme die Kaskade beim Löschen der Person sie samt Zustand mit. Liegen
    /// danach zwei Storys gleicher Signatur in den beiden Gruppen, führt <see cref="Link"/> sie zusammen.
    /// </summary>
    public static int MovePersonStories(SqliteConnection connection, SqliteTransaction tx, int sourcePersonId, int targetPersonId)
        => StorySql.Execute(connection, tx, """
            UPDATE OR IGNORE Stories SET PersonId = $t, Signature = 'P:' || $t || substr(Signature, length('P:' || $s) + 1)
            WHERE PersonId = $s AND Kind = 3;
            """, ("$s", sourcePersonId), ("$t", targetPersonId));

    /// <summary>Storys einer Gruppe ohne Bibliothek löschen (Einträge per Kaskade).</summary>
    public static int DeleteGroup(SqliteConnection connection, SqliteTransaction tx, int groupId)
    {
        StorySql.Execute(connection, tx, "DELETE FROM FileLinkUndo WHERE Kind IN (2, 3) AND TargetId IN (SELECT Id FROM Stories WHERE GroupId = $g);", ("$g", groupId));
        return StorySql.Execute(connection, tx, "DELETE FROM Stories WHERE GroupId = $g;", ("$g", groupId));
    }

    private static List<int> ReadIds(SqliteConnection connection, SqliteTransaction tx, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        var list = new List<int>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(reader.GetInt32(0));
        }

        return list;
    }
}
