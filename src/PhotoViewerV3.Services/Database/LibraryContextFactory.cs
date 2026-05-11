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

public sealed class LibraryContextFactory : ILibraryContextFactory
{
    private readonly IAppDataPaths _paths;
    private readonly SqlitePragmaInterceptor _pragmaInterceptor;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly string _connectionString;

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

    public LibraryContext Create()
    {
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
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        SqlitePragmaInterceptor.ApplyPragmas(connection);
        return connection;
    }
}
