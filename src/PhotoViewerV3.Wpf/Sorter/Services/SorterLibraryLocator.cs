using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PhotoViewerV3.Services.Library;

namespace PhotoViewerV3.Sorter.Services;

/// <summary>Aktive Bibliothek mit ihren Wurzeln (ADR-0020: Archiv zuerst, dann die Eingänge; je mit Erreichbarkeit).</summary>
public sealed record SorterLibraryInfo(int Id, string Name, string RootPath, IReadOnlyList<LibraryRootInfo> Roots);

/// <summary>
/// Liefert die aktive Bibliothek samt Wurzeln als Vorbelegung für Quelle und Ziel des Sortierers
/// (über <see cref="ILibraryService"/>; Fehler werden geloggt und als „keine Bibliothek“ gemeldet).
/// </summary>
public sealed class SorterLibraryLocator
{
    private readonly ILibraryService _libraries;
    private readonly ILogger<SorterLibraryLocator> _logger;

    public SorterLibraryLocator(ILibraryService libraries, ILogger<SorterLibraryLocator> logger)
    {
        _libraries = libraries;
        _logger = logger;
    }

    public async Task<SorterLibraryInfo?> GetActiveAsync(CancellationToken ct = default)
    {
        try
        {
            var info = await _libraries.GetActiveAsync(ct).ConfigureAwait(true);
            return info is null ? null : new SorterLibraryInfo(info.Id, info.Name, info.RootPath, info.Roots);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Aktive Bibliothek konnte nicht ermittelt werden");
            return null;
        }
    }
}
