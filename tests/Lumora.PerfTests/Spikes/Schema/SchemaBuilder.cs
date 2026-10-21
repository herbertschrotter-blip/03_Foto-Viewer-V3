using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Lumora.PerfTests.Spikes.Schema;

/// <summary>
/// Legt das Spike-Schema gemäß PLAN.md Kap. 4 an: Folders + Files + Pflicht-Indizes + FilesFts (FTS5).
/// FOREIGN-KEY-Constraints bewusst weggelassen (kosten Bulk-Insert-Performance).
/// FTS bleibt initial leer; Bulk-Insert befüllt nach abgeschlossenem File-Insert.
/// </summary>
internal static class SchemaBuilder
{
    public static SqliteConnection OpenAndPrepare(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            Cache = SqliteCacheMode.Default,
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        connection.Open();
        ApplyPragmas(connection);
        return connection;
    }

    public static void DropDatabaseFiles(string dbPath)
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { dbPath, dbPath + "-wal", dbPath + "-shm", dbPath + "-journal" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    public static void ApplyPragmas(SqliteConnection connection)
    {
        ExecuteNonQuery(connection, "PRAGMA journal_mode = WAL;");
        ExecuteNonQuery(connection, "PRAGMA synchronous = NORMAL;");
        ExecuteNonQuery(connection, "PRAGMA busy_timeout = 5000;");
        ExecuteNonQuery(connection, "PRAGMA temp_store = MEMORY;");
        ExecuteNonQuery(connection, "PRAGMA foreign_keys = OFF;");
    }

    public static void CreateSchema(SqliteConnection connection)
    {
        CreateTables(connection);
        CreateIndexes(connection);
        CreateFtsTable(connection);
    }

    private static void CreateTables(SqliteConnection connection)
    {
        ExecuteNonQuery(connection, """
            CREATE TABLE Folders (
                Id          INTEGER PRIMARY KEY,
                LibraryId   INTEGER NOT NULL,
                Path        TEXT    NOT NULL,
                ParentId    INTEGER NULL,
                Name        TEXT    NOT NULL,
                ScannedAt   INTEGER NOT NULL
            );
            """);

        ExecuteNonQuery(connection, """
            CREATE TABLE Files (
                Id              INTEGER PRIMARY KEY,
                LibraryId       INTEGER NOT NULL,
                FolderId        INTEGER NOT NULL,
                Name            TEXT    NOT NULL,
                Extension       TEXT    NOT NULL,
                Size            INTEGER NOT NULL,
                CreatedAtUtc    INTEGER NOT NULL,
                ModifiedAtUtc   INTEGER NOT NULL,
                MediaType       INTEGER NOT NULL,
                Width           INTEGER NULL,
                Height          INTEGER NULL,
                DurationMs      INTEGER NULL,
                Codec           TEXT    NULL,
                IsFavorite      INTEGER NOT NULL,
                Rating          INTEGER NULL,
                Missing         INTEGER NOT NULL
            );
            """);
    }

    private static void CreateIndexes(SqliteConnection connection)
    {
        // Folders
        ExecuteNonQuery(connection,
            "CREATE UNIQUE INDEX UX_Folders_Library_Path ON Folders(LibraryId, Path);");

        // Files: V0.1-Pflicht-Indizes laut PLAN.md Kap. 4
        ExecuteNonQuery(connection,
            "CREATE INDEX IX_Files_Library_Folder ON Files(LibraryId, FolderId);");
        ExecuteNonQuery(connection,
            "CREATE INDEX IX_Files_Folder_Name ON Files(FolderId, Name);");
        ExecuteNonQuery(connection,
            "CREATE INDEX IX_Files_Library_MediaType ON Files(LibraryId, MediaType);");
        ExecuteNonQuery(connection,
            "CREATE INDEX IX_Files_Library_Modified ON Files(LibraryId, ModifiedAtUtc DESC);");
        ExecuteNonQuery(connection,
            "CREATE INDEX IX_Files_Library_Favorite ON Files(LibraryId, IsFavorite);");
        ExecuteNonQuery(connection,
            "CREATE UNIQUE INDEX UX_Files_Folder_Name_Size_Modified ON Files(FolderId, Name, Size, ModifiedAtUtc);");
    }

    private static void CreateFtsTable(SqliteConnection connection)
    {
        ExecuteNonQuery(connection, """
            CREATE VIRTUAL TABLE FilesFts USING fts5(
                Name,
                Extension,
                content='Files',
                content_rowid='Id'
            );
            """);
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
