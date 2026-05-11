using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace PhotoViewerV3.Data.RawSql;

/// <summary>
/// FTS5-Indexierung gemäß ADR-0011 + Spike #1. Pattern: nach Files-Bulk-Insert
/// ein single INSERT … SELECT, das alle FTS-Einträge auf einmal füllt. Schneller
/// als Trigger pro INSERT und blockiert nicht die Bulk-Insert-Transaktion.
/// </summary>
public sealed class FtsIndexer
{
    public sealed record FtsReport(long RowsInserted, TimeSpan Duration);

    /// <summary>
    /// Befüllt FilesFts mit allen aktuell in Files vorhandenen Rows.
    /// Im Initial-Scan-Modus: FilesFts ist leer, alle Files werden indexiert.
    /// </summary>
    public FtsReport PopulateAll(SqliteConnection connection)
    {
        var sw = Stopwatch.StartNew();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO FilesFts(rowid, Name, Extension)
            SELECT Id, Name, Extension FROM Files
            WHERE Id NOT IN (SELECT rowid FROM FilesFts);
            """;
        var affected = cmd.ExecuteNonQuery();
        sw.Stop();
        return new FtsReport(affected, sw.Elapsed);
    }

    /// <summary>
    /// Manuelle Optimierung des FTS-Index (analog VACUUM für FTS5).
    /// Optional, idR nach großen Bulk-Inserts.
    /// </summary>
    public TimeSpan Optimize(SqliteConnection connection)
    {
        var sw = Stopwatch.StartNew();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO FilesFts(FilesFts) VALUES('optimize');";
        cmd.ExecuteNonQuery();
        sw.Stop();
        return sw.Elapsed;
    }
}
