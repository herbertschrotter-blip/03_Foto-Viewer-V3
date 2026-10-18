using System.Diagnostics;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PhotoViewerV3.Data;
using PhotoViewerV3.Services.Database;
using PhotoViewerV3.Services.Infrastructure;
using PhotoViewerV3.Services.Messaging;
using PhotoViewerV3.Services.Scanning;
using LibraryEntity = PhotoViewerV3.Domain.Entities.Library;

namespace PhotoViewerV3.Services.Library;

/// <summary>
/// Verwaltung der Bibliotheken (ADR-0003: eine aktive Library, Multi-Library vorbereitet).
/// EF Core für CRUD und Zähl-Queries, Raw-SQLite für die Massenlöschung beim Leeren
/// (FTS-Einträge, Thumbnails, Files, Folders in einer Transaktion — ADR-0004).
/// </summary>
public sealed class LibraryService : ILibraryService
{
    private readonly ILibraryContextFactory _factory;
    private readonly IAppDataPaths _paths;
    private readonly ILogger<LibraryService> _logger;
    private readonly IMessenger _messenger = WeakReferenceMessenger.Default;

    public LibraryService(ILibraryContextFactory factory, IAppDataPaths paths, ILogger<LibraryService> logger)
    {
        _factory = factory;
        _paths = paths;
        _logger = logger;
    }

    public async Task<IReadOnlyList<LibraryInfo>> GetAllAsync(CancellationToken ct = default)
    {
        List<LibraryEntity> entities;
        Dictionary<int, int> folderCounts;
        Dictionary<int, int> fileCounts;

        await using (var ctx = _factory.Create())
        {
            entities = await ActiveFirst(ctx.Libraries.AsNoTracking()).ToListAsync(ct).ConfigureAwait(false);

            // Zähler je Library in zwei GroupBy-Queries statt N+1
            folderCounts = await ctx.Folders.AsNoTracking()
                .GroupBy(f => f.LibraryId)
                .Select(g => new { LibraryId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.LibraryId, x => x.Count, ct)
                .ConfigureAwait(false);
            fileCounts = await ctx.Files.AsNoTracking()
                .GroupBy(f => f.LibraryId)
                .Select(g => new { LibraryId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.LibraryId, x => x.Count, ct)
                .ConfigureAwait(false);
        }

        // Erreichbarkeit parallel prüfen — Offline-Laufwerke würden sonst nacheinander bis zum Timeout blockieren
        var availability = await Task.WhenAll(entities.Select(e => IsRootAvailableAsync(e.RootPath, ct)))
            .ConfigureAwait(false);

        var result = new List<LibraryInfo>(entities.Count);
        for (var i = 0; i < entities.Count; i++)
        {
            var e = entities[i];
            result.Add(ToInfo(e, folderCounts.GetValueOrDefault(e.Id), fileCounts.GetValueOrDefault(e.Id), availability[i]));
        }

        return result;
    }

    public async Task<LibraryInfo?> GetAsync(int libraryId, CancellationToken ct = default)
    {
        await using var ctx = _factory.Create();
        var entity = await ctx.Libraries.AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == libraryId, ct)
            .ConfigureAwait(false);
        return entity is null ? null : await ToInfoAsync(ctx, entity, ct).ConfigureAwait(false);
    }

    public async Task<LibraryInfo?> GetActiveAsync(CancellationToken ct = default)
    {
        await using var ctx = _factory.Create();
        var entity = await ActiveFirst(ctx.Libraries.AsNoTracking())
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        return entity is null ? null : await ToInfoAsync(ctx, entity, ct).ConfigureAwait(false);
    }

    public async Task<LibraryInfo> CreateAsync(string name, string rootPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        var normalized = NormalizeRootPath(rootPath);
        if (!Directory.Exists(normalized))
        {
            throw new DirectoryNotFoundException($"Der Ordner „{normalized}“ existiert nicht oder ist nicht erreichbar.");
        }

        var displayName = string.IsNullOrWhiteSpace(name) ? SuggestName(normalized) : name.Trim();

        LibraryEntity entity;
        await using (var ctx = _factory.Create())
        {
            // Duplikat-Check case-insensitive auf normalisierten Pfaden (SQLite vergleicht sonst byteweise)
            var existing = await ctx.Libraries.AsNoTracking()
                .Select(l => new { l.Id, l.Name, l.RootPath })
                .ToListAsync(ct)
                .ConfigureAwait(false);
            var duplicate = existing.FirstOrDefault(l => PathEquals(SafeNormalize(l.RootPath), normalized));
            if (duplicate is not null)
            {
                throw new InvalidOperationException(
                    $"Der Ordner „{normalized}“ gehört bereits zur Bibliothek „{duplicate.Name}“.");
            }

            var now = DateTime.UtcNow;
            entity = new LibraryEntity
            {
                Name = displayName,
                RootPath = normalized,
                CreatedAt = now,
                LastOpenedAt = now,
            };
            ctx.Libraries.Add(entity);
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        _logger.LogInformation("Bibliothek angelegt: Id={LibraryId} Name={Name} Root={Root}", entity.Id, entity.Name, entity.RootPath);
        _messenger.Send(new ActiveLibraryChangedMessage(entity.Id));

        return ToInfo(entity, 0, 0, isRootAvailable: true);
    }

    public async Task RenameAsync(int libraryId, string newName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);

        await using var ctx = _factory.Create();
        var entity = await ctx.Libraries.FindAsync(new object[] { libraryId }, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Bibliothek Id={libraryId} nicht gefunden.");
        entity.Name = newName.Trim();
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Bibliothek umbenannt: Id={LibraryId} Name={Name}", libraryId, entity.Name);
    }

    public async Task SetActiveAsync(int libraryId, CancellationToken ct = default)
    {
        await using (var ctx = _factory.Create())
        {
            var entity = await ctx.Libraries.FindAsync(new object[] { libraryId }, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Bibliothek Id={libraryId} nicht gefunden.");
            entity.LastOpenedAt = DateTime.UtcNow;
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        _logger.LogInformation("Aktive Bibliothek: Id={LibraryId}", libraryId);
        _messenger.Send(new ActiveLibraryChangedMessage(libraryId));
    }

    public async Task ClearAsync(int libraryId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var sw = Stopwatch.StartNew();

        // Raw-SQLite in EINER Transaktion: FTS-Einträge (alte Werte!), Thumbnails, Files, Folders.
        // OperationBatches/FileOperations bleiben als Protokoll bestehen (ADR-0012).
        var result = await Task.Run(() => ClearCatalogRows(libraryId), ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Bibliothek geleert: Id={LibraryId} — {Files} Dateien, {Folders} Ordner, {Thumbnails} Thumbnail-Zeilen in {Ms:N0} ms (FTS-Rebuild: {Rebuilt})",
            libraryId, result.Files, result.Folders, result.Thumbnails, sw.Elapsed.TotalMilliseconds, result.FtsRebuilt);

        await DeleteThumbnailCacheAsync(libraryId).ConfigureAwait(false);
    }

    public async Task DeleteAsync(int libraryId, CancellationToken ct = default)
    {
        string? name;
        int? activeBefore;
        await using (var ctx = _factory.Create())
        {
            name = await ctx.Libraries.AsNoTracking()
                .Where(l => l.Id == libraryId)
                .Select(l => l.Name)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
            if (name is null)
            {
                _logger.LogWarning("DeleteAsync: Bibliothek Id={LibraryId} existiert nicht.", libraryId);
                return;
            }

            activeBefore = await GetActiveIdAsync(ctx, ct).ConfigureAwait(false);
        }

        await ClearAsync(libraryId, ct).ConfigureAwait(false);

        await using (var ctx = _factory.Create())
        {
            await ctx.Libraries.Where(l => l.Id == libraryId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }

        _logger.LogInformation("Bibliothek entfernt: Id={LibraryId} Name={Name}", libraryId, name);

        if (activeBefore != libraryId)
        {
            return;
        }

        // Die gelöschte Library war aktiv → nächste aktivieren (sendet ActiveLibraryChangedMessage), sonst nichts.
        int? next;
        await using (var ctx = _factory.Create())
        {
            next = await GetActiveIdAsync(ctx, ct).ConfigureAwait(false);
        }

        if (next is { } nextId)
        {
            await SetActiveAsync(nextId, ct).ConfigureAwait(false);
        }
        else
        {
            _logger.LogInformation("Keine Bibliothek mehr vorhanden — keine aktive Bibliothek.");
        }
    }

    // ---- Pfad-Helfer -----------------------------------------------------------------------

    /// <summary>Absoluter Pfad ohne abschließenden Separator; Laufwerkswurzeln (C:\) behalten ihn.</summary>
    public static string NormalizeRootPath(string rootPath)
    {
        var full = Path.GetFullPath(rootPath.Trim());
        var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (trimmed.Length == 0)
        {
            return full;
        }

        return trimmed.EndsWith(':') ? trimmed + Path.DirectorySeparatorChar : trimmed;
    }

    /// <summary>Vorschlag für den Anzeigenamen: letzter Pfadbestandteil, bei Laufwerkswurzeln der Laufwerksbuchstabe.</summary>
    public static string SuggestName(string rootPath)
    {
        var trimmed = rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }

    private static string SafeNormalize(string rootPath)
    {
        try
        {
            return NormalizeRootPath(rootPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return rootPath;
        }
    }

    private static bool PathEquals(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Erreichbarkeit mit Timeout (lokal 1 s, UNC 3 s; Timeout zählt als „nicht erreichbar“) — dieselbe Mechanik
    /// wie die Vorbedingung und der Root-Recheck des Scans (PLAN Kap. 12).
    /// </summary>
    private static Task<bool> IsRootAvailableAsync(string rootPath, CancellationToken ct) =>
        RootAvailabilityProbe.IsAvailableAsync(rootPath, ct);

    // ---- Query-Helfer ----------------------------------------------------------------------

    /// <summary>Aktiv = jüngstes LastOpenedAt, sonst jüngstes CreatedAt (NULLs sortieren in SQLite bei DESC nach hinten).</summary>
    private static IOrderedQueryable<LibraryEntity> ActiveFirst(IQueryable<LibraryEntity> query) =>
        query.OrderByDescending(l => l.LastOpenedAt)
            .ThenByDescending(l => l.CreatedAt)
            .ThenByDescending(l => l.Id);

    private static Task<int?> GetActiveIdAsync(LibraryContext ctx, CancellationToken ct) =>
        ActiveFirst(ctx.Libraries.AsNoTracking()).Select(l => (int?)l.Id).FirstOrDefaultAsync(ct);

    private async Task<LibraryInfo> ToInfoAsync(LibraryContext ctx, LibraryEntity entity, CancellationToken ct)
    {
        var folders = await ctx.Folders.CountAsync(f => f.LibraryId == entity.Id, ct).ConfigureAwait(false);
        var files = await ctx.Files.CountAsync(f => f.LibraryId == entity.Id, ct).ConfigureAwait(false);
        var available = await IsRootAvailableAsync(entity.RootPath, ct).ConfigureAwait(false);
        return ToInfo(entity, folders, files, available);
    }

    private static LibraryInfo ToInfo(LibraryEntity e, int folderCount, int fileCount, bool isRootAvailable) =>
        new(e.Id, e.Name, e.RootPath, e.CreatedAt, e.LastOpenedAt, folderCount, fileCount, isRootAvailable);

    // ---- Raw-SQLite ------------------------------------------------------------------------

    private LibraryCatalogCleanup.Result ClearCatalogRows(int libraryId)
    {
        using var connection = _factory.OpenSqliteConnection();
        return LibraryCatalogCleanup.DeleteLibraryRows(connection, libraryId, _logger);
    }

    /// <summary>Thumbnail-Cache-Ordner der Library löschen — best effort, Fehler werden nur geloggt.</summary>
    private async Task DeleteThumbnailCacheAsync(int libraryId)
    {
        var path = _paths.GetLibraryThumbnailsPath(libraryId);
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            await Task.Run(() => Directory.Delete(path, recursive: true)).ConfigureAwait(false);
            _logger.LogInformation("Thumbnail-Cache gelöscht: {Path}", path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Thumbnail-Cache konnte nicht (vollständig) gelöscht werden: {Path}", path);
        }
    }
}
