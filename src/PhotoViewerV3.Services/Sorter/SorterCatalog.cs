using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PhotoViewerV3.Services.Database;

namespace PhotoViewerV3.Services.Sorter;

/// <summary>Katalog-Eintrag einer Datei (Files-Zeile) für den Pfad → FileId-Lookup.</summary>
public readonly record struct CatalogFileEntry(int Id, long Size, bool Missing);

/// <summary>
/// Katalog-Zugriff des Sortierers: Wurzel und Library zu einem Ordner finden, Dateien eines Ordners (bzw. aller
/// Unterordner) nach Name auflösen und leere Folders-Zeilen nach dem Löschen eines Ordners entfernen.
/// Ordnervergleich ASCII-case-insensitiv wie SQLite <c>lower()</c>. Öffentlich nur für DI und Tests.
/// </summary>
public sealed class SorterCatalog
{
    private const int QueryChunkSize = 500;

    private readonly ILibraryContextFactory _factory;
    private readonly ILogger<SorterCatalog> _logger;

    public SorterCatalog(ILibraryContextFactory factory, ILogger<SorterCatalog> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    /// <summary>
    /// Wurzel (Eingang oder Archiv, ADR-0020), unter der der Ordner liegt — über alle Bibliotheken, bei
    /// Verschachtelung die längste —, samt allen Wurzeln derselben Bibliothek (Archiv zuerst, dann SortOrder);
    /// sonst <see cref="FolderNotInLibraryException"/>.
    /// </summary>
    public async Task<SorterLocation> RequireLocationAsync(string normalizedFolder, CancellationToken ct)
    {
        await using var ctx = _factory.Create();
        var rows = await ctx.LibraryRoots.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var roots = new List<(int LibraryId, int SortOrder, SorterRoot Root)>(rows.Count);
        foreach (var r in rows)
        {
            if (string.IsNullOrWhiteSpace(r.Path))
            {
                continue;
            }

            try
            {
                roots.Add((r.LibraryId, r.SortOrder, new SorterRoot(r.Id, SorterPaths.NormalizeDirectory(r.Path), r.Role, r.Name)));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                _logger.LogWarning(ex, "Wurzel {Id} mit ungültigem Pfad übersprungen: {Path}", r.Id, r.Path);
            }
        }

        var match = roots
            .Where(r => SorterPaths.IsInsideRoot(normalizedFolder, r.Root.Path))
            .OrderByDescending(r => r.Root.Path.Length)
            .FirstOrDefault();
        if (match.Root is null)
        {
            throw new FolderNotInLibraryException(normalizedFolder);
        }

        var libraryRoots = roots
            .Where(r => r.LibraryId == match.LibraryId)
            .OrderBy(r => r.Root.IsArchive ? 0 : 1)
            .ThenBy(r => r.SortOrder)
            .ThenBy(r => r.Root.Id)
            .Select(r => r.Root)
            .ToList();
        return new SorterLocation(match.LibraryId, match.Root, libraryRoots);
    }

    /// <summary>Dateien (Name → Eintrag) der Folders-Zeile mit genau diesem Pfad; leer, wenn der Ordner nicht gescannt ist.</summary>
    public async Task<Dictionary<string, CatalogFileEntry>> GetFilesInFolderAsync(int libraryId, string normalizedFolder, CancellationToken ct)
    {
        await using var ctx = _factory.Create();
        var folderIds = await FindFolderIdsAsync(ctx, libraryId, normalizedFolder, ct).ConfigureAwait(false);
        var result = new Dictionary<string, CatalogFileEntry>(StringComparer.OrdinalIgnoreCase);
        if (folderIds.Count == 0)
        {
            return result;
        }

        var rows = await ctx.Files
            .AsNoTracking()
            .Where(f => folderIds.Contains(f.FolderId))
            .Select(f => new { f.Id, f.Name, f.Size, f.Missing })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var r in rows)
        {
            result[r.Name] = new CatalogFileEntry(r.Id, r.Size, r.Missing);
        }

        return result;
    }

    /// <summary>
    /// Dateien aller gescannten Ordner unterhalb (und inklusive) des Ordners:
    /// normalisierter Ordnerpfad → (Name → Eintrag). Ordnerfilter läuft clientseitig über IsInsideRoot.
    /// </summary>
    public async Task<Dictionary<string, Dictionary<string, CatalogFileEntry>>> GetFilesUnderFolderAsync(
        int libraryId,
        string normalizedFolder,
        CancellationToken ct)
    {
        await using var ctx = _factory.Create();
        var folders = await ctx.Folders
            .AsNoTracking()
            .Where(f => f.LibraryId == libraryId)
            .Select(f => new { f.Id, f.Path })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var byId = new Dictionary<int, string>();
        foreach (var f in folders)
        {
            string normalized;
            try
            {
                normalized = SorterPaths.NormalizeDirectory(f.Path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (SorterPaths.IsInsideRoot(normalized, normalizedFolder))
            {
                byId[f.Id] = normalized;
            }
        }

        var result = new Dictionary<string, Dictionary<string, CatalogFileEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in byId.Values)
        {
            result.TryAdd(path, new Dictionary<string, CatalogFileEntry>(StringComparer.OrdinalIgnoreCase));
        }

        foreach (var chunk in byId.Keys.Chunk(QueryChunkSize))
        {
            var rows = await ctx.Files
                .AsNoTracking()
                .Where(f => chunk.Contains(f.FolderId))
                .Select(f => new { f.Id, f.FolderId, f.Name, f.Size, f.Missing })
                .ToListAsync(ct)
                .ConfigureAwait(false);

            foreach (var r in rows)
            {
                if (byId.TryGetValue(r.FolderId, out var path))
                {
                    result[path][r.Name] = new CatalogFileEntry(r.Id, r.Size, r.Missing);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Entfernt die Folders-Zeile eines (im Dateisystem bereits gelöschten) Ordners, sofern keine
    /// Files-Zeile und kein Unterordner mehr darauf verweist. Best-effort: Fehler werden nur geloggt.
    /// </summary>
    public async Task<bool> TryRemoveEmptyFolderRowAsync(int libraryId, string normalizedFolder, CancellationToken ct)
    {
        try
        {
            await using var ctx = _factory.Create();
            var ids = await FindFolderIdsAsync(ctx, libraryId, normalizedFolder, ct).ConfigureAwait(false);
            var removed = false;
            foreach (var id in ids)
            {
                var inUse = await ctx.Files.AnyAsync(f => f.FolderId == id, ct).ConfigureAwait(false)
                            || await ctx.Folders.AnyAsync(f => f.ParentId == id, ct).ConfigureAwait(false);
                if (inUse)
                {
                    continue;
                }

                var row = await ctx.Folders.FirstOrDefaultAsync(f => f.Id == id, ct).ConfigureAwait(false);
                if (row is not null)
                {
                    ctx.Folders.Remove(row);
                    removed = true;
                }
            }

            if (removed)
            {
                await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            return removed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Folders-Zeile für {Folder} konnte nicht entfernt werden", normalizedFolder);
            return false;
        }
    }

    private static async Task<List<int>> FindFolderIdsAsync(Data.LibraryContext ctx, int libraryId, string normalizedFolder, CancellationToken ct)
    {
        var lower = AsciiLower(normalizedFolder);
        var withSeparator = lower + Path.DirectorySeparatorChar;
        var candidates = await ctx.Folders
            .AsNoTracking()
            .Where(f => f.LibraryId == libraryId && (f.Path.ToLower() == lower || f.Path.ToLower() == withSeparator))
            .Select(f => new { f.Id, f.Path })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return candidates
            .Where(c => SorterPaths.PathEquals(SorterPaths.NormalizeDirectory(c.Path), normalizedFolder))
            .Select(c => c.Id)
            .ToList();
    }

    /// <summary>Nur A–Z falten — exakt das Verhalten von SQLite <c>lower()</c> ohne ICU.</summary>
    internal static string AsciiLower(string value)
        => string.Create(value.Length, value, static (span, src) =>
        {
            for (var i = 0; i < src.Length; i++)
            {
                var c = src[i];
                span[i] = c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
            }
        });
}
