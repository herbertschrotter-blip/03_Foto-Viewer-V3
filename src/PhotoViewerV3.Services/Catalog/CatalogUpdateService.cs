using CommunityToolkit.Mvvm.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.Logging;
using PhotoViewerV3.Domain.Entities;
using PhotoViewerV3.Services.Database;
using PhotoViewerV3.Services.Messaging;

namespace PhotoViewerV3.Services.Catalog;

/// <summary>
/// Schreibpfad für Benutzer-Metadaten (Favorit, Bewertung). Set-basiertes
/// <c>ExecuteUpdateAsync</c> in Chunks zu je <see cref="ChunkSize"/> Ids (SQLite-Parameterlimit,
/// kurze Schreibläufe), eigener Single-Writer-Lock. Nach dem Update eine
/// <see cref="FilesChangedMessage"/> (Kind = Metadata) je betroffener Library.
/// </summary>
public sealed class CatalogUpdateService : ICatalogUpdateService, IDisposable
{
    public const int ChunkSize = 500;

    private readonly ILibraryContextFactory _factory;
    private readonly ILogger<CatalogUpdateService> _logger;
    private readonly IMessenger _messenger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public CatalogUpdateService(
        ILibraryContextFactory factory,
        ILogger<CatalogUpdateService> logger,
        IMessenger? messenger = null)
    {
        _factory = factory;
        _logger = logger;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
    }

    public Task SetFavoriteAsync(IReadOnlyList<int> fileIds, bool isFavorite, CancellationToken ct = default)
        => UpdateAsync(fileIds, s => s.SetProperty(f => f.IsFavorite, isFavorite), ct);

    public Task SetRatingAsync(IReadOnlyList<int> fileIds, byte? rating, CancellationToken ct = default)
    {
        if (rating is < 1 or > 5)
        {
            throw new ArgumentOutOfRangeException(nameof(rating), rating, "Die Bewertung muss zwischen 1 und 5 liegen oder null sein.");
        }

        return UpdateAsync(fileIds, s => s.SetProperty(f => f.Rating, rating), ct);
    }

    private async Task UpdateAsync(
        IReadOnlyList<int> fileIds,
        Action<UpdateSettersBuilder<MediaFile>> setters,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fileIds);
        var ids = fileIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return;
        }

        var byLibrary = new Dictionary<int, List<int>>();

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var ctx = _factory.Create();
            foreach (var chunk in ids.Chunk(ChunkSize))
            {
                ct.ThrowIfCancellationRequested();

                // LibraryId der Dateien ermitteln (für die Nachricht), dann set-basiert schreiben.
                var affected = await ctx.Files
                    .AsNoTracking()
                    .Where(f => chunk.Contains(f.Id))
                    .Select(f => new { f.Id, f.LibraryId })
                    .ToListAsync(ct)
                    .ConfigureAwait(false);

                await ctx.Files
                    .Where(f => chunk.Contains(f.Id))
                    .ExecuteUpdateAsync(setters, ct)
                    .ConfigureAwait(false);

                foreach (var row in affected)
                {
                    if (!byLibrary.TryGetValue(row.LibraryId, out var list))
                    {
                        list = new List<int>();
                        byLibrary[row.LibraryId] = list;
                    }

                    list.Add(row.Id);
                }
            }
        }
        finally
        {
            _writeLock.Release();
        }

        foreach (var (libraryId, list) in byLibrary)
        {
            _logger.LogDebug("Metadaten aktualisiert: Library={LibraryId} Files={Count}", libraryId, list.Count);
            _messenger.Send(new FilesChangedMessage(libraryId, FilesChangeKind.Metadata, list));
        }
    }

    public void Dispose() => _writeLock.Dispose();
}
