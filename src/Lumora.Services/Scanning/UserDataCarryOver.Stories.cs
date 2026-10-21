namespace Lumora.Services.Scanning;

/// <summary>
/// Storys über „Neu aufbauen“ (ADR-0058). Die Zeilen in <c>Stories</c> selbst (angeheftet, ausgeblendet, eigener Titel,
/// gesehen) hängen an keiner Datei und bleiben ohnehin; an den Dateien hängen ihre Einträge (<c>StoryItems</c>, ON DELETE
/// CASCADE) und Titelbilder (<c>Stories.CoverFileId</c>, ON DELETE SET NULL). Die Einträge werden wie die Alben GEPARKT
/// (negative FileId, Fremdschlüssel bis zum Commit aufgeschoben — <see cref="ParkFaces"/> setzt
/// <c>PRAGMA defer_foreign_keys</c>) — alle, nicht nur die manuellen: so bleibt jede Story samt Tagesabschnitten erhalten,
/// bis die Erkennung nach dem Nachtragen der Aufnahmedaten neu rechnet und sie über die Signatur bzw. die gemeinsamen
/// Dateien wiederfindet. Titelbilder (auch das manuelle Merkmal) werden gesichert und über die Zuordnung alt → neu
/// zurückgesetzt. Was keine neue Datei mehr hat, fällt weg.
/// </summary>
public sealed partial class UserDataCarryOver
{
    private const string ParkStoriesSql = """
        DROP TABLE IF EXISTS temp.pv_carry_story_covers;
        CREATE TEMP TABLE pv_carry_story_covers(
            StoryId   INTEGER NOT NULL PRIMARY KEY,
            OldFileId INTEGER NOT NULL,
            IsManual  INTEGER NOT NULL);
        INSERT INTO temp.pv_carry_story_covers(StoryId, OldFileId, IsManual)
            SELECT Id, CoverFileId, CoverIsManual FROM Stories WHERE CoverFileId IN (SELECT OldFileId FROM temp.pv_carry_files);
        PRAGMA defer_foreign_keys = ON;
        UPDATE StoryItems SET FileId = -FileId WHERE FileId IN (SELECT OldFileId FROM temp.pv_carry_files);
        """;

    private const string RestoreStoriesSql = """
        UPDATE OR IGNORE StoryItems SET FileId = m.NewFileId FROM temp.pv_carry_map m WHERE StoryItems.FileId = -m.OldFileId;
        DELETE FROM StoryItems WHERE FileId < 0;
        UPDATE Stories SET CoverFileId = m.NewFileId, CoverIsManual = c.IsManual
            FROM temp.pv_carry_story_covers c CROSS JOIN temp.pv_carry_map m ON m.OldFileId = c.OldFileId
            WHERE Stories.Id = c.StoryId;
        DROP TABLE IF EXISTS temp.pv_carry_story_covers;
        """;

    private void ParkStories() => Execute(_connection, _transaction, ParkStoriesSql, _libraryId);

    private void RestoreStories() => Execute(_connection, _transaction, RestoreStoriesSql, _libraryId);
}
