using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace PhotoViewerV3.Services.Database;

/// <summary>
/// Initialisiert die Library-DB beim App-Start: ggf. Migrations anwenden, Crash-Recovery
/// für Thumbnail-Status (Pending → Stale gemäß ADR-0007).
/// V0.1-Strategie laut ADR-0005: bis erste produktive Nutzung darf DB neu erzeugt werden,
/// ab V0.2 strict Migrations + versioniertes Backup.
/// </summary>
public sealed class DatabaseInitializer
{
    private readonly ILibraryContextFactory _factory;
    private readonly ILogger<DatabaseInitializer> _logger;

    public DatabaseInitializer(ILibraryContextFactory factory, ILogger<DatabaseInitializer> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task<DatabaseInitResult> InitializeAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Library-DB initialisieren: {Path}", _factory.DatabasePath);

        await using var ctx = _factory.Create();

        var pending = (await ctx.Database.GetPendingMigrationsAsync(ct).ConfigureAwait(false)).ToArray();
        var applied = (await ctx.Database.GetAppliedMigrationsAsync(ct).ConfigureAwait(false)).ToArray();

        var migrated = false;
        if (pending.Length > 0)
        {
            _logger.LogInformation(
                "Migration ausstehend: {Count} ({First} → {Last}). Wende an…",
                pending.Length, pending[0], pending[^1]);
            await ctx.Database.MigrateAsync(ct).ConfigureAwait(false);
            migrated = true;
            _logger.LogInformation("Migration abgeschlossen.");
        }
        else
        {
            _logger.LogInformation(
                "Keine ausstehenden Migrations ({Applied} bereits angewendet).",
                applied.Length);
        }

        // Crash-Recovery: Thumbnails im Status 'Pending' (1) sind nach Abstürzen unklar.
        // → Auf 'Stale' (4) setzen, damit sie beim nächsten Queue-Run regeneriert werden.
        var staleRecovered = await ctx.Database.ExecuteSqlRawAsync(
            "UPDATE Thumbnails SET Status = 4 WHERE Status = 1;",
            ct).ConfigureAwait(false);
        if (staleRecovered > 0)
        {
            _logger.LogInformation("Crash-Recovery: {Count} Pending-Thumbnails → Stale.", staleRecovered);
        }

        return new DatabaseInitResult(
            DatabasePath: _factory.DatabasePath,
            AppliedMigrations: (applied.Length + (migrated ? pending.Length : 0)),
            NewlyMigrated: migrated ? pending.Length : 0,
            StaleThumbnailsRecovered: staleRecovered);
    }
}

public sealed record DatabaseInitResult(
    string DatabasePath,
    int AppliedMigrations,
    int NewlyMigrated,
    int StaleThumbnailsRecovered);
