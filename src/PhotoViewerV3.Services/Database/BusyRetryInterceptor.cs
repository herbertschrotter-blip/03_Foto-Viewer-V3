using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace PhotoViewerV3.Services.Database;

/// <summary>
/// Hängt an jedem EF-Kontext der <see cref="LibraryContextFactory"/> (ADR-0030) und führt in den asynchronen Pfaden
/// alles, was auf die Schreibsperre warten kann, selbst über <see cref="SqliteBusyRetry"/> aus: Autocommit-Schreibbefehle
/// (<c>SaveChangesAsync</c> mit einem Befehl, <c>ExecuteUpdateAsync</c>/<c>ExecuteDeleteAsync</c>,
/// <c>ExecuteSqlAsync</c>) und den Beginn einer Transaktion (<c>SaveChangesAsync</c> mit mehreren Befehlen,
/// <c>Database.BeginTransactionAsync</c>). Damit warten Einstellungen, Tag-/Kategorie-Stammdaten, Thumbnail-Status und
/// Datei-Operationen ohne eigene Zeile asynchron, statt den aufrufenden Thread zu blockieren und nach ~35 s zu scheitern.
/// Befehle innerhalb einer Transaktion und reine Lesebefehle bleiben unberührt; die synchronen Pfade ebenfalls.
/// </summary>
internal sealed class BusyRetryInterceptor(SqliteBusyRetry retry) : IDbCommandInterceptor, IDbTransactionInterceptor
{
    public async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (result.HasResult || command.Transaction is not null || command is not SqliteCommand)
        {
            return result;
        }

        var rows = await retry.RunAsync(() => retry.ExecuteAttempt(command, static c => c.ExecuteNonQuery()), cancellationToken)
            .ConfigureAwait(false);
        return InterceptionResult<int>.SuppressWithResult(rows);
    }

    // SaveChanges mit einem einzigen Befehl läuft ohne Transaktion als Reader (INSERT … RETURNING, UPDATE … RETURNING 1).
    public async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        if (result.HasResult || command.Transaction is not null || command is not SqliteCommand || !IsWrite(command.CommandText))
        {
            return result;
        }

        var reader = await retry.RunAsync(() => retry.ExecuteAttempt(command, static c => c.ExecuteReader()), cancellationToken)
            .ConfigureAwait(false);
        return InterceptionResult<DbDataReader>.SuppressWithResult(reader);
    }

    public async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
        DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result,
        CancellationToken cancellationToken = default)
    {
        if (result.HasResult || connection is not SqliteConnection sqlite)
        {
            return result;
        }

        var transaction = await retry.BeginTransactionAsync(sqlite, eventData.IsolationLevel, cancellationToken).ConfigureAwait(false);
        return InterceptionResult<DbTransaction>.SuppressWithResult(transaction);
    }

    private static bool IsWrite(string? sql)
    {
        var text = sql.AsSpan().TrimStart();
        return text.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("REPLACE", StringComparison.OrdinalIgnoreCase);
    }
}
