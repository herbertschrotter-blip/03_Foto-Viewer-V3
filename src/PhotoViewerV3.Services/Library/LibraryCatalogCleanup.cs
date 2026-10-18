using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace PhotoViewerV3.Services.Library;

/// <summary>
/// Löscht alle Katalogzeilen einer Library (FTS-Einträge, Thumbnails, Files, Folders) in einer Transaktion.
/// Gemeinsam genutzt von <see cref="LibraryService.ClearAsync"/> und dem Rollback eines unvollständigen Scans.
/// Schneller Weg: FTS-Einträge gezielt mit den alten Werten austragen (CLAUDE.md-Regel für external content).
/// Meldet FTS5 dabei „database disk image is malformed“ (Index und Files-Tabelle passen nicht zusammen),
/// werden die Zeilen gelöscht und der Index per <c>'rebuild'</c> aus der Files-Tabelle neu aufgebaut.
/// </summary>
internal static class LibraryCatalogCleanup
{
    public readonly record struct Result(int Files, int Folders, int Thumbnails, bool FtsRebuilt);

    private const string FtsDeleteSql =
        "INSERT INTO FilesFts(FilesFts, rowid, Name, Extension) SELECT 'delete', Id, Name, Extension FROM Files WHERE LibraryId = $lib;";

    private const string FtsRebuildSql = "INSERT INTO FilesFts(FilesFts) VALUES('rebuild');";

    /// <param name="ftsEntriesExpected">false, wenn die Files dieser Library sicher noch nicht indexiert wurden (dann direkt Rebuild-Pfad).</param>
    public static Result DeleteLibraryRows(SqliteConnection connection, int libraryId, ILogger logger, bool ftsEntriesExpected = true)
    {
        if (ftsEntriesExpected)
        {
            try
            {
                using var tx = connection.BeginTransaction();
                Execute(connection, tx, libraryId, FtsDeleteSql);
                var counts = DeleteRows(connection, tx, libraryId);
                tx.Commit();
                return counts;
            }
            catch (SqliteException ex)
            {
                logger.LogWarning(
                    ex,
                    "FTS-Einträge der Bibliothek {LibraryId} konnten nicht einzeln ausgetragen werden — Suchindex wird nach dem Löschen neu aufgebaut",
                    libraryId);
            }
        }

        using (var tx = connection.BeginTransaction())
        {
            var counts = DeleteRows(connection, tx, libraryId);
            Execute(connection, tx, libraryId, FtsRebuildSql);
            tx.Commit();
            return counts with { FtsRebuilt = true };
        }
    }

    private static Result DeleteRows(SqliteConnection connection, SqliteTransaction tx, int libraryId)
    {
        // Reihenfolge: Thumbnails vor Files (Subselect), Files vor Folders. OperationBatches bleiben (ADR-0012).
        var thumbnails = Execute(connection, tx, libraryId,
            "DELETE FROM Thumbnails WHERE FileId IN (SELECT Id FROM Files WHERE LibraryId = $lib);");
        var files = Execute(connection, tx, libraryId, "DELETE FROM Files WHERE LibraryId = $lib;");
        var folders = Execute(connection, tx, libraryId, "DELETE FROM Folders WHERE LibraryId = $lib;");
        return new Result(files, folders, thumbnails, FtsRebuilt: false);
    }

    private static int Execute(SqliteConnection connection, SqliteTransaction tx, int libraryId, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$lib", libraryId);
        return cmd.ExecuteNonQuery();
    }
}
