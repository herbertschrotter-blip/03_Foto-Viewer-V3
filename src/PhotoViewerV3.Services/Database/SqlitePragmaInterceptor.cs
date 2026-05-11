using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace PhotoViewerV3.Services.Database;

/// <summary>
/// Setzt die V0.1-Pflicht-PRAGMAs (WAL, synchronous=NORMAL, busy_timeout=5000, temp_store=MEMORY)
/// nach jedem Connection-Open, analog zum Pattern aus Spike #1. Greift sowohl für EF-CRUD-Pfad
/// als auch für Raw Microsoft.Data.Sqlite, wenn dieser Interceptor genutzt wird.
/// </summary>
public sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    private const string PragmaScript =
        "PRAGMA journal_mode=WAL; " +
        "PRAGMA synchronous=NORMAL; " +
        "PRAGMA busy_timeout=5000; " +
        "PRAGMA temp_store=MEMORY;";

    private readonly ILogger<SqlitePragmaInterceptor>? _logger;

    public SqlitePragmaInterceptor(ILogger<SqlitePragmaInterceptor>? logger = null)
    {
        _logger = logger;
    }

    public override void ConnectionOpened(
        DbConnection connection,
        ConnectionEndEventData eventData)
    {
        ApplyPragmas(connection);
        base.ConnectionOpened(connection, eventData);
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await ApplyPragmasAsync(connection, cancellationToken).ConfigureAwait(false);
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken).ConfigureAwait(false);
    }

    public static void ApplyPragmas(DbConnection connection)
    {
        if (connection is not SqliteConnection)
        {
            return;
        }

        using var cmd = connection.CreateCommand();
        cmd.CommandText = PragmaScript;
        cmd.ExecuteNonQuery();
    }

    public static async Task ApplyPragmasAsync(DbConnection connection, CancellationToken ct)
    {
        if (connection is not SqliteConnection)
        {
            return;
        }

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = PragmaScript;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
