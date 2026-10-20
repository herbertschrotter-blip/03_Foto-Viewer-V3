using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PhotoViewerV3.Data;
using PhotoViewerV3.Services.Infrastructure;

namespace PhotoViewerV3.Services.Database;

/// <summary>
/// Erzeugt LibraryContext-Instanzen mit dem AppDataPaths-basierten DB-Pfad und dem
/// SqlitePragmaInterceptor. EF Core CRUD-Pfad. Bulk- und FTS-Pfade öffnen ihre eigenen
/// SqliteConnections (aber gegen dieselbe DB-Datei) — siehe ADR-0004.
/// </summary>
public interface ILibraryContextFactory
{
    LibraryContext Create();
    string DatabasePath { get; }
    string ConnectionString { get; }

    /// <summary>
    /// Öffnet eine rohe SqliteConnection (gegen dieselbe DB) für den Bulk-/FTS-Pfad
    /// gemäß ADR-0004. PRAGMAs werden direkt nach Open gesetzt.
    /// Caller ist für Dispose verantwortlich.
    /// </summary>
    SqliteConnection OpenSqliteConnection();
}

/// <summary>
/// Sperrt neue Verbindungen zur Katalog-DB, solange sie umzieht (ADR-0026): wer danach <c>Create</c> oder
/// <c>OpenSqliteConnection</c> aufruft, bekommt eine <see cref="DatabaseSuspendedException"/> statt in eine DB zu
/// schreiben, die gerade kopiert wird. Bereits offene Verbindungen bleiben unberührt.
/// </summary>
public interface IDatabaseAccessGate
{
    bool IsSuspended { get; }

    /// <summary>Sperrt bis zum Dispose des Rückgabewerts; nicht verschachtelbar.</summary>
    IDisposable Suspend(string reason);
}

/// <summary>Die Katalog-DB ist vorübergehend gesperrt (Umzug des Speicherorts).</summary>
public sealed class DatabaseSuspendedException(string reason)
    : InvalidOperationException($"Die Katalog-Datenbank ist gerade gesperrt: {reason}");

public sealed class LibraryContextFactory : ILibraryContextFactory, IDatabaseAccessGate
{
    private readonly IAppDataPaths _paths;
    private readonly SqlitePragmaInterceptor _pragmaInterceptor;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly string _connectionString;
    private string? _suspendReason;

    public LibraryContextFactory(
        IAppDataPaths paths,
        SqlitePragmaInterceptor pragmaInterceptor,
        ILoggerFactory? loggerFactory = null)
    {
        _paths = paths;
        _pragmaInterceptor = pragmaInterceptor;
        _loggerFactory = loggerFactory;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _paths.LibraryDatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
            Pooling = false,
        }.ToString();
    }

    public string DatabasePath => _paths.LibraryDatabasePath;
    public string ConnectionString => _connectionString;

    public bool IsSuspended => Volatile.Read(ref _suspendReason) is not null;

    public IDisposable Suspend(string reason)
    {
        if (Interlocked.CompareExchange(ref _suspendReason, reason, null) is not null)
        {
            throw new InvalidOperationException("Die Katalog-Datenbank ist bereits gesperrt.");
        }

        return new Suspension(this);
    }

    public LibraryContext Create()
    {
        ThrowIfSuspended();
        var optionsBuilder = new DbContextOptionsBuilder<LibraryContext>()
            .UseSqlite(_connectionString)
            .AddInterceptors(_pragmaInterceptor);

        if (_loggerFactory is not null)
        {
            optionsBuilder.UseLoggerFactory(_loggerFactory);
        }

        return new LibraryContext(optionsBuilder.Options);
    }

    public SqliteConnection OpenSqliteConnection()
    {
        ThrowIfSuspended();
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        SqlitePragmaInterceptor.ApplyPragmas(connection);
        return connection;
    }

    private void ThrowIfSuspended()
    {
        if (Volatile.Read(ref _suspendReason) is { } reason)
        {
            throw new DatabaseSuspendedException(reason);
        }
    }

    private sealed class Suspension(LibraryContextFactory owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Volatile.Write(ref owner._suspendReason, null);
            }
        }
    }
}
