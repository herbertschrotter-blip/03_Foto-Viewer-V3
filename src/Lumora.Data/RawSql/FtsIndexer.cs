using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace Lumora.Data.RawSql;

/// <summary>
/// FTS5-Indexierung gemäß ADR-0011 + Spike #1. Pattern: nach Files-Bulk-Insert
/// ein single INSERT … SELECT, das alle FTS-Einträge auf einmal füllt. Schneller
/// als Trigger pro INSERT und blockiert nicht die Bulk-Insert-Transaktion.
/// </summary>
public sealed class FtsIndexer
{
    public sealed record FtsReport(long RowsInserted, TimeSpan Duration);

    /// <summary>
    /// Baut den FTS-Index vollständig aus Files auf (FTS5-Kommando <c>'rebuild'</c>) und zählt
    /// danach die indexierten Dokumente. Idempotent: ein zweiter Aufruf erzeugt keine Duplikate.
    /// </summary>
    /// <remarks>
    /// FilesFts ist eine External-Content-Tabelle (content='Files'): jedes SELECT ohne MATCH —
    /// auch <c>COUNT(*)</c> oder <c>SELECT rowid FROM FilesFts</c> — liest aus der Content-Tabelle
    /// Files, nie aus dem Index. Das frühere
    /// <c>INSERT … SELECT … WHERE Id NOT IN (SELECT rowid FROM FilesFts)</c> schloss deshalb alle
    /// Dateien aus und ließ den Index leer, während die Zählung „voll“ meldete.
    /// Die Zahl der indexierten Dokumente steht in der Shadow-Tabelle <c>FilesFts_docsize</c>
    /// (eine Zeile je Dokument; Standard columnsize=1). 'rebuild' indexiert alle Libraries neu —
    /// für V0.1 (eine Library, Fresh-Scan) der einfachste konsistente Weg.
    /// </remarks>
    /// <param name="transaction">Offene Transaktion des Aufrufers (ADR-0028: „Neu aufbauen“ in einer Transaktion); sonst null.</param>
    public FtsReport PopulateAll(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        var sw = Stopwatch.StartNew();
        using (var rebuildCmd = connection.CreateCommand())
        {
            rebuildCmd.Transaction = transaction;
            rebuildCmd.CommandText = "INSERT INTO FilesFts(FilesFts) VALUES('rebuild');";
            rebuildCmd.ExecuteNonQuery();
        }

        long count;
        using (var countCmd = connection.CreateCommand())
        {
            countCmd.Transaction = transaction;
            countCmd.CommandText = "SELECT COUNT(*) FROM FilesFts_docsize;";
            count = (long)(countCmd.ExecuteScalar() ?? 0L);
        }

        sw.Stop();
        return new FtsReport(count, sw.Elapsed);
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
