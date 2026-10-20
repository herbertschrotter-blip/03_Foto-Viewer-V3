using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using PhotoViewerV3.Services.Database;
using PhotoViewerV3.Services.Messaging;

namespace PhotoViewerV3.Services.Catalog;

/// <summary>
/// Schreibpfad für Benutzer-Metadaten (Favorit, Bewertung). Set-basiertes <c>UPDATE</c> in Chunks zu je
/// <see cref="ChunkSize"/> Ids (SQLite-Parameterlimit) in EINER Transaktion, eigener Single-Writer-Lock. Wartet eine
/// belegte Schreibsperre asynchron ab und setzt Ziele nach einem Neuaufbau auf die neuen FileIds um (ADR-0030).
/// Nach dem Commit eine <see cref="FilesChangedMessage"/> (Kind = Metadata) je betroffener Library; scheitert oder endet
/// der Vorgang vorher, ist nichts geschrieben und es kommt keine Nachricht. Ausnahmen von Empfängern werden geloggt.
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
        => UpdateAsync(fileIds, "IsFavorite", isFavorite, ct);

    public Task SetRatingAsync(IReadOnlyList<int> fileIds, byte? rating, CancellationToken ct = default)
    {
        if (rating is < 1 or > 5)
        {
            throw new ArgumentOutOfRangeException(nameof(rating), rating, "Die Bewertung muss zwischen 1 und 5 liegen oder null sein.");
        }

        return UpdateAsync(fileIds, "Rating", rating, ct);
    }

    /// <summary>
    /// Seit 0.2.29 (ADR-0030) über <see cref="CatalogFileTargets.WriteAsync"/>: eine Transaktion auf dem Thread-Pool, die
    /// asynchron auf eine belegte Schreibsperre wartet (z. B. „Neu aufbauen“) und Ziele nach einem Neuaufbau auf die neuen
    /// FileIds umsetzt. Die Nachricht nennt die tatsächlich geschriebenen Ids.
    /// </summary>
    private async Task UpdateAsync(IReadOnlyList<int> fileIds, string column, object? value, CancellationToken ct)
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
            await CatalogFileTargets.WriteAsync(_factory, ids, _logger, (connection, tx, files) =>
            {
                foreach (var chunk in files.Chunk(ChunkSize))
                {
                    ct.ThrowIfCancellationRequested();
                    using var cmd = connection.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.Parameters.AddWithValue("$v", value ?? DBNull.Value);
                    cmd.CommandText = $"UPDATE Files SET {column} = $v WHERE Id IN ({CatalogFileTargets.BindIds(cmd, chunk.Select(f => f.FileId).ToArray(), "$f")});";
                    cmd.ExecuteNonQuery();
                }

                foreach (var file in files)
                {
                    if (!byLibrary.TryGetValue(file.LibraryId, out var list))
                    {
                        list = new List<int>();
                        byLibrary[file.LibraryId] = list;
                    }

                    list.Add(file.FileId);
                }

                return 0;
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }

        Publish(byLibrary);
    }

    private void Publish(Dictionary<int, List<int>> byLibrary)
    {
        foreach (var (libraryId, list) in byLibrary)
        {
            _logger.LogDebug("Metadaten aktualisiert: Library={LibraryId} Files={Count}", libraryId, list.Count);
            try
            {
                _messenger.Send(new FilesChangedMessage(libraryId, FilesChangeKind.Metadata, list));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Empfänger der FilesChangedMessage (Metadata) warf eine Ausnahme");
            }
        }
    }

    public void Dispose() => _writeLock.Dispose();
}
