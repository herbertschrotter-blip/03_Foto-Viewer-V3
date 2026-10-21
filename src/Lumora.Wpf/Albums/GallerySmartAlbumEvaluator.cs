using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Gallery.Data;
using Lumora.Services.Albums;
using Lumora.Services.Database;
using Lumora.Services.Library;

namespace Lumora.Albums;

/// <summary>
/// Wertet intelligente Alben mit derselben SQL aus wie die Galerie (ADR-0054): Filter → <see cref="GalleryQuery"/>
/// (<see cref="GalleryPageSource.BuildSmartQueryAsync"/>) → Ids bzw. Zahl. So zeigt ein intelligentes Album genau das, was
/// die Galerie mit demselben Filter zeigt. Eigene Verbindung (die Galerie-Verbindung bleibt dem Blättern vorbehalten).
/// </summary>
public sealed class GallerySmartAlbumEvaluator : ISmartAlbumEvaluator, IDisposable
{
    private readonly GalleryPageSource _source;
    private readonly object _openGate = new();

    public GallerySmartAlbumEvaluator(ILibraryContextFactory factory, ILogger<GalleryPageSource>? logger = null)
    {
        _source = new GalleryPageSource(factory, logger ?? NullLogger<GalleryPageSource>.Instance);
    }

    public async Task<IReadOnlyList<int>> GetFileIdsAsync(SmartAlbumFilter filter, LibraryScopeInfo scope, CancellationToken ct = default)
    {
        if (!scope.HasLibrary)
        {
            return [];
        }

        var query = await BuildAsync(filter, scope, ct).ConfigureAwait(false);
        return await _source.GetFileIdsAsync(query, null, ct).ConfigureAwait(false);
    }

    public async Task<(int Count, int? FirstFileId)> SummarizeAsync(SmartAlbumFilter filter, LibraryScopeInfo scope, CancellationToken ct = default)
    {
        if (!scope.HasLibrary)
        {
            return (0, null);
        }

        var query = await BuildAsync(filter, scope, ct).ConfigureAwait(false);
        var count = await _source.GetTotalCountAsync(query, ct).ConfigureAwait(false);
        var first = count == 0 ? null : await _source.GetFileIdsAsync(query, 1, ct).ConfigureAwait(false);
        return (count, first is { Count: > 0 } ids ? ids[0] : null);
    }

    private Task<GalleryQuery> BuildAsync(SmartAlbumFilter filter, LibraryScopeInfo scope, CancellationToken ct)
    {
        lock (_openGate)
        {
            _source.Open();
        }

        return _source.BuildSmartQueryAsync(filter, scope.LibraryId, scope.LibraryIds, ct);
    }

    public void Dispose() => _source.Dispose();
}
