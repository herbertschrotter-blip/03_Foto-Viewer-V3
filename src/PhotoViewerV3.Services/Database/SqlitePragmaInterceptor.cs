using System.Data.Common;
using System.Globalization;
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
    /// <summary>Produktiver busy_timeout in Millisekunden (CLAUDE.md: WAL, busy_timeout 5000).</summary>
    public const int DefaultBusyTimeoutMs = 5000;

    private readonly ILogger<SqlitePragmaInterceptor>? _logger;
    private readonly string _pragmaScript;

    /// <param name="busyTimeoutMs">
    /// Nur Tests verkürzen ihn (ADR-0030: Wiederholung bei gesperrter DB prüfen, ohne 30 s zu warten).
    /// </param>
    public SqlitePragmaInterceptor(ILogger<SqlitePragmaInterceptor>? logger = null, int busyTimeoutMs = DefaultBusyTimeoutMs)
    {
        _logger = logger;
        BusyTimeoutMs = busyTimeoutMs;
        _pragmaScript = BuildScript(busyTimeoutMs);
    }

    public int BusyTimeoutMs { get; }

    public override void ConnectionOpened(
        DbConnection connection,
        ConnectionEndEventData eventData)
    {
        Apply(connection);
        base.ConnectionOpened(connection, eventData);
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await ExecuteAsync(connection, _pragmaScript, cancellationToken).ConfigureAwait(false);
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Setzt die PRAGMAs dieses Interceptors (mit seinem busy_timeout) auf einer rohen Verbindung.</summary>
    public void Apply(DbConnection connection) => Execute(connection, _pragmaScript);

    public static void ApplyPragmas(DbConnection connection) => Execute(connection, BuildScript(DefaultBusyTimeoutMs));

    public static Task ApplyPragmasAsync(DbConnection connection, CancellationToken ct)
        => ExecuteAsync(connection, BuildScript(DefaultBusyTimeoutMs), ct);

    private static void Execute(DbConnection connection, string script)
    {
        if (connection is not SqliteConnection)
        {
            return;
        }

        using var cmd = connection.CreateCommand();
        cmd.CommandText = script;
        cmd.ExecuteNonQuery();
    }

    private static async Task ExecuteAsync(DbConnection connection, string script, CancellationToken ct)
    {
        if (connection is not SqliteConnection)
        {
            return;
        }

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = script;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static string BuildScript(int busyTimeoutMs) =>
        "PRAGMA journal_mode=WAL; " +
        "PRAGMA synchronous=NORMAL; " +
        string.Create(CultureInfo.InvariantCulture, $"PRAGMA busy_timeout={busyTimeoutMs}; ") +
        "PRAGMA temp_store=MEMORY;";
}
