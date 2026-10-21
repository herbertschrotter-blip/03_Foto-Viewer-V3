using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PhotoViewerV3.Domain.Enums;

namespace PhotoViewerV3.Services.Database;

/// <summary>
/// Initialisiert die Library-DB beim App-Start: ggf. Migrations anwenden, Crash-Recovery
/// für Thumbnail-Status (Pending → Stale gemäß ADR-0007) und Bestandsaufnahme unfertiger
/// Operations-Batches (Applying/Undoing, ADR-0012) — die werden nur gezählt und geloggt, nicht
/// verändert: ihr Zustand ist die Grundlage für den späteren Recovery-Dialog.
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

        // Sortier-Index fürs Aufnahmedatum (ADR-0046) ist ein Ausdrucks-Index, den EF nicht kennt: nach einem künftigen
        // Tabellen-Neuaufbau von Files durch eine Migration wäre er weg — hier idempotent sicherstellen.
        await ctx.Database.ExecuteSqlRawAsync(Data.RawSql.CaptureDateSortSql.CreateIndexSql, ct).ConfigureAwait(false);

        // Crash-Recovery: Thumbnails im Status 'Pending' sind nach Abstürzen unklar.
        // → Auf 'Stale' setzen; der ThumbnailService rendert Stale-Zeilen beim nächsten Request neu.
        var staleRecovered = await ctx.Thumbnails
            .Where(t => t.Status == ThumbnailStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, ThumbnailStatus.Stale), ct)
            .ConfigureAwait(false);
        if (staleRecovered > 0)
        {
            _logger.LogInformation("Crash-Recovery: {Count} Pending-Thumbnails → Stale.", staleRecovered);
        }

        // Unfertige Datei-Operationen (ADR-0012): nur melden — Recovery-UI folgt.
        var unfinishedBatches = await ctx.OperationBatches
            .CountAsync(b => b.Status == BatchStatus.Applying || b.Status == BatchStatus.Undoing, ct)
            .ConfigureAwait(false);
        if (unfinishedBatches > 0)
        {
            _logger.LogWarning(
                "{Count} Datei-Operation(en) wurden nicht abgeschlossen (Status Applying/Undoing) — Dateisystem und Katalog können abweichen; nächster Scan gleicht ab.",
                unfinishedBatches);
        }

        return new DatabaseInitResult(
            DatabasePath: _factory.DatabasePath,
            AppliedMigrations: (applied.Length + (migrated ? pending.Length : 0)),
            NewlyMigrated: migrated ? pending.Length : 0,
            StaleThumbnailsRecovered: staleRecovered,
            UnfinishedBatches: unfinishedBatches);
    }
}

public sealed record DatabaseInitResult(
    string DatabasePath,
    int AppliedMigrations,
    int NewlyMigrated,
    int StaleThumbnailsRecovered,
    int UnfinishedBatches = 0);
