using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace PhotoViewerV3.Services.Library;

/// <summary>
/// Löscht Katalogzeilen einer Library (FTS-Einträge, Thumbnails, Files, Folders) in einer Transaktion — ganz
/// (<see cref="DeleteLibraryRows"/>) oder nur unterhalb einer Wurzel (<see cref="DeleteRootRows"/>, ADR-0020).
/// Gemeinsam genutzt von <see cref="LibraryService"/> und dem Rollback eines unvollständigen Scans.
/// Schneller Weg: FTS-Einträge gezielt mit den alten Werten austragen (CLAUDE.md-Regel für external content).
/// Meldet FTS5 dabei „database disk image is malformed“ (Index und Files-Tabelle passen nicht zusammen),
/// werden die Zeilen gelöscht und der Index per <c>'rebuild'</c> aus der Files-Tabelle neu aufgebaut.
/// </summary>
internal static class LibraryCatalogCleanup
{
    public readonly record struct Result(int Files, int Folders, int Thumbnails, bool FtsRebuilt);

    private const string FtsDeleteLibrarySql =
        "INSERT INTO FilesFts(FilesFts, rowid, Name, Extension) SELECT 'delete', Id, Name, Extension FROM Files WHERE LibraryId = $lib;";

    private const string FtsDeleteRootSql =
        "INSERT INTO FilesFts(FilesFts, rowid, Name, Extension) SELECT 'delete', Id, Name, Extension FROM Files WHERE LibraryId = $lib AND FolderId IN (SELECT Id FROM Folders WHERE RootId = $root);";

    private const string FtsRebuildSql = "INSERT INTO FilesFts(FilesFts) VALUES('rebuild');";

    /// <param name="ftsEntriesExpected">false, wenn die Files dieser Library sicher noch nicht indexiert wurden (dann direkt Rebuild-Pfad).</param>
    public static Result DeleteLibraryRows(SqliteConnection connection, int libraryId, ILogger logger, bool ftsEntriesExpected = true)
        => Delete(connection, libraryId, rootId: null, logger, ftsEntriesExpected);

    /// <summary>Löscht nur die Zeilen unterhalb einer Wurzel (Folders.RootId); Thumbnail-Cache-Dateien räumt der Aufrufer.</summary>
    public static Result DeleteRootRows(SqliteConnection connection, int libraryId, int rootId, ILogger logger)
        => Delete(connection, libraryId, rootId, logger, ftsEntriesExpected: true);

    private static Result Delete(SqliteConnection connection, int libraryId, int? rootId, ILogger logger, bool ftsEntriesExpected)
    {
        if (ftsEntriesExpected)
        {
            try
            {
                using var tx = connection.BeginTransaction();
                Execute(connection, tx, libraryId, rootId, rootId is null ? FtsDeleteLibrarySql : FtsDeleteRootSql);
                var counts = DeleteRows(connection, tx, libraryId, rootId);
                tx.Commit();
                return counts;
            }
            catch (SqliteException ex)
            {
                logger.LogWarning(
                    ex,
                    "FTS-Einträge der Bibliothek {LibraryId} (Wurzel {RootId}) konnten nicht einzeln ausgetragen werden — Suchindex wird nach dem Löschen neu aufgebaut",
                    libraryId, rootId);
            }
        }

        using (var tx = connection.BeginTransaction())
        {
            var counts = DeleteRows(connection, tx, libraryId, rootId);
            Execute(connection, tx, libraryId, rootId, FtsRebuildSql);
            tx.Commit();
            return counts with { FtsRebuilt = true };
        }
    }

    private static Result DeleteRows(SqliteConnection connection, SqliteTransaction tx, int libraryId, int? rootId)
    {
        // Reihenfolge: Thumbnails vor Files (Subselect), Files vor Folders. OperationBatches bleiben (ADR-0012).
        if (rootId is null)
        {
            var thumbnails = Execute(connection, tx, libraryId, null,
                "DELETE FROM Thumbnails WHERE FileId IN (SELECT Id FROM Files WHERE LibraryId = $lib);");
            var files = Execute(connection, tx, libraryId, null, "DELETE FROM Files WHERE LibraryId = $lib;");
            var folders = Execute(connection, tx, libraryId, null, "DELETE FROM Folders WHERE LibraryId = $lib;");
            return new Result(files, folders, thumbnails, FtsRebuilt: false);
        }

        var rootThumbnails = Execute(connection, tx, libraryId, rootId,
            "DELETE FROM Thumbnails WHERE FileId IN (SELECT Id FROM Files WHERE LibraryId = $lib AND FolderId IN (SELECT Id FROM Folders WHERE RootId = $root));");
        var rootFiles = Execute(connection, tx, libraryId, rootId,
            "DELETE FROM Files WHERE LibraryId = $lib AND FolderId IN (SELECT Id FROM Folders WHERE RootId = $root);");
        var rootFolders = Execute(connection, tx, libraryId, rootId,
            "DELETE FROM Folders WHERE LibraryId = $lib AND RootId = $root;");
        return new Result(rootFiles, rootFolders, rootThumbnails, FtsRebuilt: false);
    }

    private static int Execute(SqliteConnection connection, SqliteTransaction tx, int libraryId, int? rootId, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$lib", libraryId);
        if (rootId is { } root)
        {
            cmd.Parameters.AddWithValue("$root", root);
        }

        return cmd.ExecuteNonQuery();
    }
}
