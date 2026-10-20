using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PhotoViewerV3.Services.Categories;
using PhotoViewerV3.Services.Database;
using PhotoViewerV3.Services.Tags;

namespace PhotoViewerV3.Services.Catalog;

/// <summary>
/// „Ordner bewerten / kategorisieren“ (ADR-0034): Bewertung, Favorit, Kategorie-Wert und Tags für alle Dateien unter
/// einem Ordner (rekursiv). Schreibt ausschließlich über die vorhandenen Dienste (<see cref="ICatalogUpdateService"/>,
/// <see cref="ITagService"/>, <see cref="ICategoryService"/> — samt deren Warte-Logik auf einen gesperrten Katalog,
/// ADR-0030) in Abschnitten zu <see cref="ChunkSize"/> Dateien: kurze Transaktionen, Nachrichten je Abschnitt.
/// </summary>
public interface IFolderMetadataService
{
    /// <summary>FileIds aller Dateien im Ordner und allen Unterordnern (über den Ordnerpfad, wie „Unterordner einschließen“).</summary>
    Task<IReadOnlyList<int>> GetFileIdsUnderAsync(int folderId, CancellationToken ct = default);

    /// <summary>Bewertung 1–5 oder null (entfernen).</summary>
    Task SetRatingAsync(IReadOnlyList<int> fileIds, byte? rating, CancellationToken ct = default);

    Task SetFavoriteAsync(IReadOnlyList<int> fileIds, bool isFavorite, CancellationToken ct = default);

    /// <summary>Kategorie-Wert setzen (null = entfernen); liefert die Zahl geänderter Zuordnungen.</summary>
    Task<int> SetCategoryValueAsync(IReadOnlyList<int> fileIds, int categoryId, int? valueId, CancellationToken ct = default);

    /// <summary>Tags zuweisen/entfernen; liefert neue und entfernte Zuweisungen.</summary>
    Task<(int Assigned, int Removed)> ChangeTagsAsync(IReadOnlyList<int> fileIds, IReadOnlyList<int> assignTagIds, IReadOnlyList<int> removeTagIds, CancellationToken ct = default);
}

/// <inheritdoc cref="IFolderMetadataService"/>
public sealed class FolderMetadataService : IFolderMetadataService
{
    /// <summary>Dateien je Schreibaufruf — klein genug für kurze Sperren, groß genug für wenige Nachrichten.</summary>
    public const int ChunkSize = 2000;

    private readonly ILibraryContextFactory _factory;
    private readonly ICatalogUpdateService _catalog;
    private readonly ITagService _tags;
    private readonly ICategoryService _categories;
    private readonly ILogger<FolderMetadataService> _logger;

    public FolderMetadataService(
        ILibraryContextFactory factory,
        ICatalogUpdateService catalog,
        ITagService tags,
        ICategoryService categories,
        ILogger<FolderMetadataService> logger)
    {
        _factory = factory;
        _catalog = catalog;
        _tags = tags;
        _categories = categories;
        _logger = logger;
    }

    public async Task<IReadOnlyList<int>> GetFileIdsUnderAsync(int folderId, CancellationToken ct = default)
    {
        await using var ctx = _factory.Create();
        var folder = await ctx.Folders.AsNoTracking()
            .Where(f => f.Id == folderId)
            .Select(f => new { f.LibraryId, f.Path })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (folder is null)
        {
            return Array.Empty<int>();
        }

        var path = Path.TrimEndingDirectorySeparator(folder.Path);
        var lower = AsciiLower(path);
        var prefix = AsciiLower(path + Path.DirectorySeparatorChar);
        var libraryId = folder.LibraryId;
        return await ctx.Files.AsNoTracking()
            .Where(f => f.LibraryId == libraryId
                        && ctx.Folders.Any(d => d.Id == f.FolderId && (d.Path.ToLower() == lower || d.Path.ToLower().StartsWith(prefix))))
            .OrderBy(f => f.Id)
            .Select(f => f.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public Task SetRatingAsync(IReadOnlyList<int> fileIds, byte? rating, CancellationToken ct = default)
        => ForEachChunkAsync(fileIds, chunk => _catalog.SetRatingAsync(chunk, rating, ct), "Bewertung", ct);

    public Task SetFavoriteAsync(IReadOnlyList<int> fileIds, bool isFavorite, CancellationToken ct = default)
        => ForEachChunkAsync(fileIds, chunk => _catalog.SetFavoriteAsync(chunk, isFavorite, ct), "Favorit", ct);

    public async Task<int> SetCategoryValueAsync(IReadOnlyList<int> fileIds, int categoryId, int? valueId, CancellationToken ct = default)
    {
        var changed = 0;
        await ForEachChunkAsync(fileIds, async chunk => changed += await _categories.SetValueAsync(chunk, categoryId, valueId, ct).ConfigureAwait(false), "Kategorie", ct)
            .ConfigureAwait(false);
        return changed;
    }

    public async Task<(int Assigned, int Removed)> ChangeTagsAsync(
        IReadOnlyList<int> fileIds, IReadOnlyList<int> assignTagIds, IReadOnlyList<int> removeTagIds, CancellationToken ct = default)
    {
        var assigned = 0;
        var removed = 0;
        await ForEachChunkAsync(fileIds, async chunk =>
        {
            if (assignTagIds.Count > 0)
            {
                assigned += await _tags.AssignAsync(chunk, assignTagIds, ct).ConfigureAwait(false);
            }

            if (removeTagIds.Count > 0)
            {
                removed += await _tags.RemoveAsync(chunk, removeTagIds, ct).ConfigureAwait(false);
            }
        }, "Tags", ct).ConfigureAwait(false);
        return (assigned, removed);
    }

    private async Task ForEachChunkAsync(IReadOnlyList<int> fileIds, Func<IReadOnlyList<int>, Task> write, string what, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fileIds);
        var chunks = 0;
        foreach (var chunk in fileIds.Distinct().Chunk(ChunkSize))
        {
            ct.ThrowIfCancellationRequested();
            await write(chunk).ConfigureAwait(false);
            chunks++;
        }

        _logger.LogInformation("Ordner: {What} für {Count} Datei(en) in {Chunks} Abschnitt(en) gesetzt", what, fileIds.Count, chunks);
    }

    /// <summary>Nur A–Z falten — exakt das Verhalten von SQLite <c>lower()</c> ohne ICU.</summary>
    private static string AsciiLower(string value)
        => string.Create(value.Length, value, static (span, src) =>
        {
            for (var i = 0; i < src.Length; i++)
            {
                var c = src[i];
                span[i] = c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
            }
        });
}
