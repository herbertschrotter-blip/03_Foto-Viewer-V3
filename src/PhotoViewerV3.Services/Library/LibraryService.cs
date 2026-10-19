using System.Diagnostics;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PhotoViewerV3.Data;
using PhotoViewerV3.Domain.Entities;
using PhotoViewerV3.Domain.Enums;
using PhotoViewerV3.Services.Database;
using PhotoViewerV3.Services.Infrastructure;
using PhotoViewerV3.Services.Messaging;
using PhotoViewerV3.Services.Scanning;
using LibraryEntity = PhotoViewerV3.Domain.Entities.Library;

namespace PhotoViewerV3.Services.Library;

/// <summary>
/// Verwaltung der Bibliotheken (ADR-0003) und ihrer Wurzelordner (ADR-0020: genau eine Archiv-Wurzel, beliebig
/// viele Eingangs-Wurzeln). EF Core für CRUD und Zähl-Queries, Raw-SQLite für die Massenlöschung beim Leeren bzw.
/// Entfernen einer Wurzel (FTS-Einträge, Thumbnails, Files, Folders in einer Transaktion — ADR-0004).
/// <c>Libraries.RootPath</c> wird als Anzeige-/Kompatibilitätsfeld mit der Archiv-Wurzel synchron gehalten.
/// </summary>
public sealed class LibraryService : ILibraryService
{
    private readonly ILibraryContextFactory _factory;
    private readonly IAppDataPaths _paths;
    private readonly ILogger<LibraryService> _logger;
    private readonly IMessenger _messenger;

    public LibraryService(ILibraryContextFactory factory, IAppDataPaths paths, ILogger<LibraryService> logger, IMessenger? messenger = null)
    {
        _factory = factory;
        _paths = paths;
        _logger = logger;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
    }

    public async Task<IReadOnlyList<LibraryInfo>> GetAllAsync(CancellationToken ct = default)
    {
        List<LibraryEntity> entities;
        List<LibraryRoot> roots;
        Dictionary<int, int> folderCounts;
        Dictionary<int, int> fileCounts;

        await using (var ctx = _factory.Create())
        {
            entities = await ActiveFirst(ctx.Libraries.AsNoTracking()).ToListAsync(ct).ConfigureAwait(false);
            roots = await ctx.LibraryRoots.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);

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

        // Erreichbarkeit aller Wurzeln parallel prüfen — Offline-Laufwerke würden sonst nacheinander bis zum Timeout blockieren
        var rootInfos = await ToRootInfosAsync(roots, ct).ConfigureAwait(false);

        var result = new List<LibraryInfo>(entities.Count);
        foreach (var e in entities)
        {
            result.Add(ToInfo(e, folderCounts.GetValueOrDefault(e.Id), fileCounts.GetValueOrDefault(e.Id), rootInfos.Where(r => r.LibraryId == e.Id)));
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
        LibraryRoot root;
        await using (var ctx = _factory.Create())
        {
            await EnsureRootPathIsFreeAsync(ctx, libraryId: null, normalized, ct).ConfigureAwait(false);

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

            root = new LibraryRoot
            {
                LibraryId = entity.Id,
                Path = normalized,
                Role = LibraryRootRole.Archive,
                Name = SuggestName(normalized),
                SortOrder = 0,
            };
            ctx.LibraryRoots.Add(root);
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        _logger.LogInformation("Bibliothek angelegt: Id={LibraryId} Name={Name} Archiv-Wurzel={Root}", entity.Id, entity.Name, entity.RootPath);
        _messenger.Send(new ActiveLibraryChangedMessage(entity.Id));

        return ToInfo(entity, 0, 0, new[] { ToRootInfo(root, isAvailable: true) });
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
            await ctx.LibraryRoots.Where(r => r.LibraryId == libraryId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
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

    // ---- Wurzeln (ADR-0020) -----------------------------------------------------------------

    public async Task<IReadOnlyList<LibraryRootInfo>> GetRootsAsync(int libraryId, CancellationToken ct = default)
    {
        List<LibraryRoot> roots;
        await using (var ctx = _factory.Create())
        {
            roots = await ctx.LibraryRoots.AsNoTracking().Where(r => r.LibraryId == libraryId).ToListAsync(ct).ConfigureAwait(false);
        }

        return await ToRootInfosAsync(roots, ct).ConfigureAwait(false);
    }

    public async Task<LibraryRootInfo> AddRootAsync(int libraryId, string path, LibraryRootRole role, string? name = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalized = NormalizeRootPath(path);
        if (!Directory.Exists(normalized))
        {
            throw new DirectoryNotFoundException($"Der Ordner „{normalized}“ existiert nicht oder ist nicht erreichbar.");
        }

        LibraryRoot root;
        await using (var ctx = _factory.Create())
        {
            var library = await ctx.Libraries.FindAsync(new object[] { libraryId }, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Bibliothek Id={libraryId} nicht gefunden.");

            await EnsureRootPathIsFreeAsync(ctx, libraryId, normalized, ct).ConfigureAwait(false);

            if (role == LibraryRootRole.Archive
                && await ctx.LibraryRoots.AnyAsync(r => r.LibraryId == libraryId && r.Role == LibraryRootRole.Archive, ct).ConfigureAwait(false))
            {
                throw new InvalidOperationException("Die Bibliothek hat bereits eine Archiv-Wurzel — eine Bibliothek hat genau eine.");
            }

            var maxSort = await ctx.LibraryRoots.Where(r => r.LibraryId == libraryId).Select(r => (int?)r.SortOrder).MaxAsync(ct).ConfigureAwait(false) ?? -1;
            root = new LibraryRoot
            {
                LibraryId = libraryId,
                Path = normalized,
                Role = role,
                Name = string.IsNullOrWhiteSpace(name) ? SuggestName(normalized) : name.Trim(),
                SortOrder = maxSort + 1,
            };
            ctx.LibraryRoots.Add(root);
            if (role == LibraryRootRole.Archive)
            {
                library.RootPath = normalized;
            }

            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        _logger.LogInformation("Wurzel hinzugefügt: Library={LibraryId} Id={RootId} Rolle={Role} Pfad={Path}", libraryId, root.Id, root.Role, root.Path);
        _messenger.Send(new LibraryRootsChangedMessage(libraryId));
        return ToRootInfo(root, isAvailable: true);
    }

    public async Task RenameRootAsync(int rootId, string newName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        int libraryId;
        await using (var ctx = _factory.Create())
        {
            var root = await ctx.LibraryRoots.FindAsync(new object[] { rootId }, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Wurzel Id={rootId} nicht gefunden.");
            root.Name = newName.Trim();
            libraryId = root.LibraryId;
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        _logger.LogInformation("Wurzel umbenannt: Id={RootId} Name={Name}", rootId, newName.Trim());
        _messenger.Send(new LibraryRootsChangedMessage(libraryId));
    }

    public async Task SetRootRoleAsync(int rootId, LibraryRootRole role, CancellationToken ct = default)
    {
        int libraryId;
        await using (var ctx = _factory.Create())
        {
            var root = await ctx.LibraryRoots.FindAsync(new object[] { rootId }, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Wurzel Id={rootId} nicht gefunden.");
            libraryId = root.LibraryId;
            if (root.Role == role)
            {
                return;
            }

            var library = await ctx.Libraries.FindAsync(new object[] { libraryId }, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Bibliothek Id={libraryId} nicht gefunden.");

            if (role == LibraryRootRole.Inbox)
            {
                throw new InvalidOperationException(
                    "Die Archiv-Wurzel kann nicht zum Eingang werden — mache stattdessen eine andere Wurzel zum Archiv.");
            }

            // Eingang → Archiv: bisherige Archiv-Wurzel wird Eingang, RootPath folgt der neuen Archiv-Wurzel.
            var previous = await ctx.LibraryRoots
                .Where(r => r.LibraryId == libraryId && r.Role == LibraryRootRole.Archive && r.Id != rootId)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            foreach (var p in previous)
            {
                p.Role = LibraryRootRole.Inbox;
            }

            root.Role = LibraryRootRole.Archive;
            library.RootPath = root.Path;
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        _logger.LogInformation("Wurzel-Rolle gesetzt: Id={RootId} Rolle={Role}", rootId, role);
        _messenger.Send(new LibraryRootsChangedMessage(libraryId));
    }

    public async Task RemoveRootAsync(int rootId, CancellationToken ct = default)
    {
        int libraryId;
        List<int> fileIds;
        await using (var ctx = _factory.Create())
        {
            var root = await ctx.LibraryRoots.AsNoTracking().FirstOrDefaultAsync(r => r.Id == rootId, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Wurzel Id={rootId} nicht gefunden.");
            if (root.Role == LibraryRootRole.Archive)
            {
                throw new InvalidOperationException("Die Archiv-Wurzel kann nicht entfernt werden — entferne die Bibliothek oder mache eine andere Wurzel zum Archiv.");
            }

            libraryId = root.LibraryId;
            fileIds = await ctx.Files.AsNoTracking()
                .Where(f => f.LibraryId == libraryId && ctx.Folders.Any(d => d.Id == f.FolderId && d.RootId == rootId))
                .Select(f => f.Id)
                .ToListAsync(ct)
                .ConfigureAwait(false);
        }

        var sw = Stopwatch.StartNew();
        var result = await Task.Run(() =>
        {
            using var connection = _factory.OpenSqliteConnection();
            return LibraryCatalogCleanup.DeleteRootRows(connection, libraryId, rootId, _logger);
        }, ct).ConfigureAwait(false);

        await using (var ctx = _factory.Create())
        {
            await ctx.LibraryRoots.Where(r => r.Id == rootId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }

        await Task.Run(() => DeleteThumbnailFiles(libraryId, fileIds), CancellationToken.None).ConfigureAwait(false);

        _logger.LogInformation(
            "Wurzel entfernt: Library={LibraryId} Id={RootId} — {Files} Dateien, {Folders} Ordner, {Thumbnails} Thumbnail-Zeilen in {Ms:N0} ms (FTS-Rebuild: {Rebuilt})",
            libraryId, rootId, result.Files, result.Folders, result.Thumbnails, sw.Elapsed.TotalMilliseconds, result.FtsRebuilt);

        if (fileIds.Count > 0)
        {
            _messenger.Send(new FilesChangedMessage(libraryId, FilesChangeKind.Removed, fileIds));
        }

        _messenger.Send(new LibraryRootsChangedMessage(libraryId));
    }

    /// <summary>
    /// Der Pfad darf keine bestehende Wurzel sein (alle Libraries) und innerhalb derselben Library weder in einer
    /// Wurzel liegen noch eine enthalten — sonst würde der Scanner Dateien doppelt katalogisieren.
    /// </summary>
    private static async Task EnsureRootPathIsFreeAsync(LibraryContext ctx, int? libraryId, string normalized, CancellationToken ct)
    {
        var roots = await ctx.LibraryRoots.AsNoTracking()
            .Join(ctx.Libraries.AsNoTracking(), r => r.LibraryId, l => l.Id, (r, l) => new { Root = r, LibraryName = l.Name })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var entry in roots)
        {
            var existing = SafeNormalize(entry.Root.Path);
            if (PathEquals(existing, normalized))
            {
                throw new InvalidOperationException($"Der Ordner „{normalized}“ gehört bereits zur Bibliothek „{entry.LibraryName}“.");
            }

            if (libraryId is { } id && entry.Root.LibraryId == id)
            {
                if (IsInside(normalized, existing))
                {
                    throw new InvalidOperationException($"„{normalized}“ liegt innerhalb der Wurzel „{existing}“ derselben Bibliothek.");
                }

                if (IsInside(existing, normalized))
                {
                    throw new InvalidOperationException($"„{normalized}“ enthält die Wurzel „{existing}“ derselben Bibliothek.");
                }
            }
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

    /// <summary>true, wenn <paramref name="path"/> gleich <paramref name="root"/> ist oder darunter liegt (beide normalisiert).</summary>
    public static bool IsInside(string path, string root)
    {
        if (PathEquals(path, root))
        {
            return true;
        }

        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
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
        var roots = await ctx.LibraryRoots.AsNoTracking().Where(r => r.LibraryId == entity.Id).ToListAsync(ct).ConfigureAwait(false);
        var rootInfos = await ToRootInfosAsync(roots, ct).ConfigureAwait(false);
        return ToInfo(entity, folders, files, rootInfos);
    }

    /// <summary>Erreichbarkeit aller Wurzeln parallel; Reihenfolge: Archiv zuerst, dann SortOrder, Id.</summary>
    private static async Task<IReadOnlyList<LibraryRootInfo>> ToRootInfosAsync(List<LibraryRoot> roots, CancellationToken ct)
    {
        var ordered = roots
            .OrderBy(r => r.LibraryId)
            .ThenBy(r => r.Role == LibraryRootRole.Archive ? 0 : 1)
            .ThenBy(r => r.SortOrder)
            .ThenBy(r => r.Id)
            .ToList();
        var availability = await Task.WhenAll(ordered.Select(r => IsRootAvailableAsync(r.Path, ct))).ConfigureAwait(false);
        var result = new List<LibraryRootInfo>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            result.Add(ToRootInfo(ordered[i], availability[i]));
        }

        return result;
    }

    private static LibraryRootInfo ToRootInfo(LibraryRoot r, bool isAvailable) =>
        new(r.Id, r.LibraryId, r.Path, r.Role, r.Name, r.SortOrder, isAvailable);

    /// <summary>
    /// RootPath/IsRootAvailable spiegeln die Archiv-Wurzel; fehlt sie (sollte nach der Migration nie vorkommen),
    /// bleibt der Pfad der Library-Zeile und gilt als nicht erreichbar.
    /// </summary>
    private static LibraryInfo ToInfo(LibraryEntity e, int folderCount, int fileCount, IEnumerable<LibraryRootInfo> roots)
    {
        var list = roots.ToList();
        var archive = list.FirstOrDefault(r => r.IsArchive);
        return new LibraryInfo(
            e.Id,
            e.Name,
            archive?.Path ?? e.RootPath,
            e.CreatedAt,
            e.LastOpenedAt,
            folderCount,
            fileCount,
            archive?.IsAvailable ?? false,
            list);
    }

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

    /// <summary>Cache-Dateien einzelner Dateien (FileId-basiert, <c>{fileId}_*</c>) löschen — best effort.</summary>
    private void DeleteThumbnailFiles(int libraryId, IReadOnlyList<int> fileIds)
    {
        var dir = _paths.GetLibraryThumbnailsPath(libraryId);
        if (fileIds.Count == 0 || !Directory.Exists(dir))
        {
            return;
        }

        var ids = new HashSet<int>(fileIds);
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var name = Path.GetFileName(file);
                var underscore = name.IndexOf('_');
                if (underscore <= 0 || !int.TryParse(name.AsSpan(0, underscore), out var id) || !ids.Contains(id))
                {
                    continue;
                }

                try
                {
                    File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogDebug(ex, "Thumbnail-Datei konnte nicht gelöscht werden: {Path}", file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Thumbnail-Cache nicht lesbar: {Dir}", dir);
        }
    }
}
