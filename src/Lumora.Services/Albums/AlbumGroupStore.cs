using System.Globalization;
using Microsoft.Data.Sqlite;
using static Lumora.Services.Library.LibraryGroupStore;

namespace Lumora.Services.Albums;

/// <summary>
/// Alben beim Verknüpfen, Lösen und Entfernen von Bibliotheks-Gruppen (ADR-0054, Regeln aus ADR-0048) — alles in der
/// Transaktion des Aufrufers (<c>LibraryLinkStore</c>, <c>LibraryGroupStore</c>).
/// <list type="bullet">
/// <item>Verknüpfen: gleichnamige EIGENE Alben werden eins (Fotos vereinigt, die Zielgruppe behält Name und Titelbild,
/// fehlt es, kommt das der Quelle), die übrigen wandern hinter die Alben der Zielgruppe. Ist bei gleichem Namen ein
/// intelligentes Album beteiligt, wird nichts vermischt (zwei Filter lassen sich nicht vereinen): das Album der
/// hinzukommenden Bibliothek bekommt den Bibliotheksnamen als Zusatz („Urlaub (Zweite)“) — keine Rückfrage, Regel wie
/// bei Tags.</item>
/// <item>Lösen: die herausgelöste Bibliothek bekommt Kopien ALLER Alben der Gruppe (eigene mit ihren Fotos aus dieser
/// Bibliothek, intelligente mit demselben Filter — er bezieht Begriffe über ihren Namen und bleibt gültig); die übrigen
/// behalten ihre Fotos. Titelbilder bleiben dort, wo die Datei liegt.</item>
/// <item>Gruppe ohne Bibliothek: ihre Alben gehen mit (Verknüpfungen per Kaskade, Fotos bleiben unberührt).</item>
/// </list>
/// Ordner-Verknüpfungen gelten nur innerhalb einer Bibliothek und sind von Verknüpfen/Lösen nicht betroffen.
/// </summary>
internal static class AlbumGroupStore
{
    /// <summary>Gruppe <paramref name="sourceGroup"/> geht in <paramref name="targetGroup"/> auf; liefert die Zahl zusammengeführter Alben.</summary>
    public static int Link(SqliteConnection connection, SqliteTransaction tx, int targetGroup, int sourceGroup, string sourceLibraryName)
    {
        var p = new (string, object)[] { ("$t", targetGroup), ("$s", sourceGroup) };
        Execute(connection, tx, "DROP TABLE IF EXISTS temp._AlbumMap;");
        Execute(connection, tx, "CREATE TEMP TABLE _AlbumMap (SourceId INTEGER NOT NULL PRIMARY KEY, TargetId INTEGER NOT NULL);");
        var merged = Execute(connection, tx, """
            INSERT INTO _AlbumMap
            SELECT s.Id, t.Id FROM Albums s JOIN Albums t ON t.GroupId = $t AND t.Name = s.Name
            WHERE s.GroupId = $s AND s.Kind = 0 AND t.Kind = 0;
            """, p);
        Execute(connection, tx, """
            INSERT OR IGNORE INTO AlbumItems (AlbumId, FileId, AddedAtUtc)
            SELECT m.TargetId, ai.FileId, ai.AddedAtUtc FROM AlbumItems ai JOIN _AlbumMap m ON m.SourceId = ai.AlbumId;
            UPDATE Albums SET CoverFileId = (SELECT s.CoverFileId FROM _AlbumMap m JOIN Albums s ON s.Id = m.SourceId WHERE m.TargetId = Albums.Id)
            WHERE CoverFileId IS NULL AND Id IN (SELECT TargetId FROM _AlbumMap);
            UPDATE FileLinkUndo SET TargetId = (SELECT m.TargetId FROM _AlbumMap m WHERE m.SourceId = FileLinkUndo.TargetId)
            WHERE Kind = 0 AND TargetId IN (SELECT SourceId FROM _AlbumMap);
            DELETE FROM Albums WHERE Id IN (SELECT SourceId FROM _AlbumMap);
            """);

        // Gleicher Name mit intelligentem Album: das hinzukommende bekommt den Bibliotheksnamen als Zusatz.
        var conflicts = new List<(int Id, string Name)>();
        using (var cmd = Command(connection, tx,
                   "SELECT s.Id, s.Name FROM Albums s WHERE s.GroupId = $s AND EXISTS (SELECT 1 FROM Albums t WHERE t.GroupId = $t AND t.Name = s.Name) ORDER BY s.Id;", p))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                conflicts.Add((reader.GetInt32(0), reader.GetString(1)));
            }
        }

        foreach (var (id, name) in conflicts)
        {
            var suffix = string.IsNullOrWhiteSpace(sourceLibraryName) ? "verknüpft" : sourceLibraryName.Trim();
            var candidate = $"{name} ({suffix})";
            for (var n = 2; NameTaken(connection, tx, candidate, targetGroup, sourceGroup); n++)
            {
                candidate = $"{name} ({suffix} {n.ToString(CultureInfo.InvariantCulture)})";
            }

            Execute(connection, tx, "UPDATE Albums SET Name = $n WHERE Id = $id;", ("$n", candidate), ("$id", id));
        }

        var offset = Scalar(connection, tx, "SELECT COALESCE(MAX(SortOrder), -1) + 1 FROM Albums WHERE GroupId = $t;", p);
        Execute(connection, tx, "UPDATE Albums SET GroupId = $t, SortOrder = SortOrder + $o WHERE GroupId = $s;",
            ("$t", targetGroup), ("$s", sourceGroup), ("$o", offset));
        Execute(connection, tx, "DROP TABLE temp._AlbumMap;");
        return merged;
    }

    /// <summary>
    /// Kopiert alle Alben von <paramref name="fromGroup"/> nach <paramref name="toGroup"/> und hängt die Album-Fotos der
    /// Bibliothek <paramref name="libraryId"/> auf die Kopien um; liefert die Zahl kopierter Alben. Neue Ids liegen über dem
    /// AUTOINCREMENT-Zähler — nie die Id eines gelöschten Albums (gesicherte Verknüpfungen in FileLinkUndo zeigen darauf).
    /// </summary>
    public static int CopyForLibrary(SqliteConnection connection, SqliteTransaction tx, int fromGroup, int toGroup, int libraryId)
    {
        var p = new (string, object)[] { ("$from", fromGroup), ("$to", toGroup), ("$lib", libraryId) };
        Execute(connection, tx, "DROP TABLE IF EXISTS temp._AlbumCopyMap;");
        Execute(connection, tx, "CREATE TEMP TABLE _AlbumCopyMap (OldId INTEGER NOT NULL PRIMARY KEY, NewId INTEGER NOT NULL);");
        var copied = Execute(connection, tx, """
            INSERT INTO _AlbumCopyMap
            SELECT Id,
                   MAX((SELECT COALESCE(MAX(Id), 0) FROM Albums), COALESCE((SELECT seq FROM sqlite_sequence WHERE name = 'Albums'), 0))
                   + ROW_NUMBER() OVER (ORDER BY Id)
            FROM Albums WHERE GroupId = $from;
            """, p);
        Execute(connection, tx, """
            INSERT INTO Albums (Id, GroupId, Name, Kind, FilterJson, CoverFileId, SortOrder, CreatedAtUtc, UpdatedAtUtc)
            SELECT m.NewId, $to, a.Name, a.Kind, a.FilterJson,
                   CASE WHEN EXISTS (SELECT 1 FROM Files f WHERE f.Id = a.CoverFileId AND f.LibraryId = $lib) THEN a.CoverFileId END,
                   a.SortOrder, a.CreatedAtUtc, a.UpdatedAtUtc
            FROM _AlbumCopyMap m JOIN Albums a ON a.Id = m.OldId;
            UPDATE Albums SET CoverFileId = NULL
            WHERE GroupId = $from AND CoverFileId IN (SELECT Id FROM Files WHERE LibraryId = $lib);
            UPDATE AlbumItems SET AlbumId = (SELECT m.NewId FROM _AlbumCopyMap m WHERE m.OldId = AlbumItems.AlbumId)
            WHERE FileId IN (SELECT Id FROM Files WHERE LibraryId = $lib)
              AND AlbumId IN (SELECT OldId FROM _AlbumCopyMap);
            """, p);
        Execute(connection, tx, "DROP TABLE temp._AlbumCopyMap;");
        return copied;
    }

    /// <summary>Alben einer Gruppe ohne Bibliothek löschen (Verknüpfungen per Kaskade; die Fotos sind mit der Bibliothek gegangen).</summary>
    public static int DeleteGroup(SqliteConnection connection, SqliteTransaction tx, int groupId)
    {
        Execute(connection, tx, "DELETE FROM FileLinkUndo WHERE Kind = 0 AND TargetId IN (SELECT Id FROM Albums WHERE GroupId = $g);", ("$g", groupId));
        return Execute(connection, tx, "DELETE FROM Albums WHERE GroupId = $g;", ("$g", groupId));
    }

    private static bool NameTaken(SqliteConnection connection, SqliteTransaction tx, string name, int targetGroup, int sourceGroup)
        => Scalar(connection, tx, "SELECT COUNT(*) FROM Albums WHERE (GroupId = $t OR GroupId = $s) AND Name = $n;",
            ("$t", targetGroup), ("$s", sourceGroup), ("$n", name)) > 0;
}
