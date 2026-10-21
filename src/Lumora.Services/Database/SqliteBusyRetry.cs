using System.Data;
using System.Data.Common;
using System.Diagnostics;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Services.Messaging;

namespace Lumora.Services.Database;

/// <summary>Einstellungen der Wiederholung bei gesperrter Katalog-DB (ADR-0030).</summary>
public sealed record SqliteBusyRetryOptions
{
    public static SqliteBusyRetryOptions Default { get; } = new();

    /// <summary>
    /// So lange wartet ein Schreiber insgesamt auf die Schreibsperre, bevor er mit „database is locked“ aufgibt. Deckt
    /// die Schreibphase von „Neu aufbauen“ (eine Transaktion, 500 000 Dateien ≈ 30 s, ADR-0028) mit viel Reserve ab.
    /// </summary>
    public TimeSpan MaxWait { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// CommandTimeout eines einzelnen Versuchs in Sekunden (mindestens 1): so lange wiederholt Microsoft.Data.Sqlite
    /// intern. Ein Versuch belegt einen Pool-Thread damit höchstens etwa max(busy_timeout, dieser Wert).
    /// </summary>
    public int AttemptTimeoutSeconds { get; init; } = 1;

    /// <summary>Pause zwischen zwei Versuchen (der Busy-Handler von SQLite hat davor schon busy_timeout lang gewartet).</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(100);
}

/// <summary>
/// Zentrale Wiederholung für Schreiber der Katalog-DB (ADR-0030). Hintergrund: „Neu aufbauen“ ersetzt den Katalog in
/// EINER Schreibtransaktion (ADR-0028), bei 500 000 Dateien ≈ 30 s. Microsoft.Data.Sqlite arbeitet auch in seinen
/// *Async-Methoden synchron: ein anderer Schreiber blockierte in dieser Zeit seinen Thread (busy_timeout 5 s plus die
/// interne Wiederholung bis CommandTimeout 30 s — auf dem UI-Thread ein eingefrorenes Fenster) und scheiterte danach
/// mit „database is locked“. Hier läuft jeder Versuch auf dem Thread-Pool, nach einem SQLITE_BUSY/SQLITE_LOCKED wird
/// asynchron gewartet und wiederholt, bis <see cref="SqliteBusyRetryOptions.MaxWait"/> abgelaufen ist; erst dann
/// kommt die Ausnahme beim Aufrufer an (nichts geht still verloren). Wartende melden sich über
/// <see cref="CatalogWriteWaitMessage"/> (Statusleiste).
/// <para>
/// Wiederholt wird nur, was atomar gescheitert ist: ein Befehl außerhalb einer Transaktion (Autocommit) oder
/// <c>BEGIN IMMEDIATE</c>. Befehle innerhalb einer Transaktion halten die Sperre bereits und werden nicht wiederholt.
/// Das ist dieselbe Semantik wie die interne Wiederholung von Microsoft.Data.Sqlite — nur asynchron und länger.
/// </para>
/// </summary>
public sealed class SqliteBusyRetry
{
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;

    private readonly IMessenger _messenger;
    private readonly ILogger _logger;
    private readonly Lock _sync = new();
    private int _waiting;

    /// <param name="messenger">Optional (Tests: eigene Instanz); produktiv WeakReferenceMessenger.Default.</param>
    public SqliteBusyRetry(SqliteBusyRetryOptions? options = null, IMessenger? messenger = null, ILogger? logger = null)
    {
        Options = options ?? SqliteBusyRetryOptions.Default;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Standard-Instanz für Context-Factories ohne eigene (Test-Fakes).</summary>
    public static SqliteBusyRetry Default { get; } = new();

    public SqliteBusyRetryOptions Options { get; }

    /// <summary>Anzahl der Schreibvorgänge, die gerade auf die Sperre warten (Momentaufnahme).</summary>
    public int WaitingWriters => Volatile.Read(ref _waiting);

    /// <summary>true für SQLITE_BUSY/SQLITE_LOCKED, auch eingepackt (z. B. in einer DbUpdateException).</summary>
    public static bool IsBusy(Exception? exception)
    {
        for (var ex = exception; ex is not null; ex = ex.InnerException)
        {
            if (ex is SqliteException { SqliteErrorCode: SqliteBusy or SqliteLocked })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Führt <paramref name="attempt"/> auf dem Thread-Pool aus und wiederholt ihn nach SQLITE_BUSY/SQLITE_LOCKED, bis
    /// er gelingt oder <see cref="SqliteBusyRetryOptions.MaxWait"/> abgelaufen ist (dann fliegt die letzte Ausnahme).
    /// Der Versuch muss bei einem Fehlschlag atomar gescheitert sein (Autocommit-Befehl, BEGIN, ganze Transaktion).
    /// </summary>
    public async Task<T> RunAsync<T>(Func<T> attempt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        var clock = Stopwatch.StartNew();
        var waiting = false;
        var attempts = 0;
        var succeeded = false;
        try
        {
            while (true)
            {
                attempts++;
                try
                {
                    var result = await Task.Run(attempt, ct).ConfigureAwait(false);
                    succeeded = true;
                    return result;
                }
                catch (Exception ex) when (IsBusy(ex) && clock.Elapsed < Options.MaxWait)
                {
                    if (!waiting)
                    {
                        waiting = true;
                        ChangeWaiting(+1);
                        _logger.LogInformation("Katalog-DB gesperrt (anderer Schreiber, z. B. „Neu aufbauen“) — Schreibvorgang wartet und wird wiederholt");
                    }

                    await Task.Delay(Options.RetryDelay, ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            if (waiting)
            {
                ChangeWaiting(-1);
                if (succeeded)
                {
                    _logger.LogInformation("Katalog-DB wieder frei — Schreibvorgang nach {Seconds:N1} s ({Attempts} Versuche) ausgeführt", clock.Elapsed.TotalSeconds, attempts);
                }
                else
                {
                    _logger.LogWarning("Schreibvorgang nach {Seconds:N1} s Warten auf die Katalog-DB nicht ausgeführt ({Attempts} Versuche)", clock.Elapsed.TotalSeconds, attempts);
                }
            }
        }
    }

    /// <summary>
    /// <c>BEGIN IMMEDIATE</c> auf einer rohen Verbindung mit Wiederholung (<see cref="RunAsync{T}"/>): wartet asynchron,
    /// solange ein anderer Schreiber die Sperre hält. Ersetzt <c>connection.BeginTransaction()</c> in Schreibpfaden.
    /// </summary>
    public Task<SqliteTransaction> BeginTransactionAsync(SqliteConnection connection, CancellationToken ct = default)
        => BeginTransactionAsync(connection, IsolationLevel.Unspecified, ct);

    public Task<SqliteTransaction> BeginTransactionAsync(SqliteConnection connection, IsolationLevel isolationLevel, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return RunAsync(
            () =>
            {
                // BEGIN läuft als Befehl mit connection.DefaultTimeout — für den einzelnen Versuch kurz halten.
                var previous = connection.DefaultTimeout;
                connection.DefaultTimeout = AttemptTimeout;
                try
                {
                    return connection.BeginTransaction(isolationLevel);
                }
                finally
                {
                    connection.DefaultTimeout = previous;
                }
            },
            ct);
    }

    /// <summary>Führt einen Befehl als einzelnen Versuch aus (kurzer CommandTimeout, siehe <see cref="SqliteBusyRetryOptions.AttemptTimeoutSeconds"/>).</summary>
    internal TResult ExecuteAttempt<TResult>(DbCommand command, Func<DbCommand, TResult> execute)
    {
        command.CommandTimeout = AttemptTimeout;
        return execute(command);
    }

    private int AttemptTimeout => Math.Max(1, Options.AttemptTimeoutSeconds);

    private void ChangeWaiting(int delta)
    {
        // Unter der Sperre, damit die Zählerstände in der richtigen Reihenfolge ankommen (Empfänger dispatchen nur).
        lock (_sync)
        {
            var now = Interlocked.Add(ref _waiting, delta);
            try
            {
                _messenger.Send(new CatalogWriteWaitMessage(now));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Empfänger der CatalogWriteWaitMessage warf eine Ausnahme");
            }
        }
    }
}
