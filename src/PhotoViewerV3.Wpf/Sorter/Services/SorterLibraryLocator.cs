using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PhotoViewerV3.Services.Database;
using PhotoViewerV3.Services.Library;

namespace PhotoViewerV3.Sorter.Services;

public sealed record SorterLibraryInfo(int Id, string Name, string RootPath);

/// <summary>
/// Liefert die aktive Bibliothek (Name + Root) als Vorbelegung für den Quellordner. Nutzt
/// <see cref="ILibraryService"/>, sobald er registriert ist; bis dahin (Welle 1 läuft parallel)
/// die Library mit dem jüngsten LastOpenedAt direkt aus der DB.
/// </summary>
public sealed class SorterLibraryLocator
{
    private readonly ILibraryContextFactory _factory;
    private readonly ILogger<SorterLibraryLocator> _logger;
    private readonly ILibraryService? _libraryService;

    public SorterLibraryLocator(
        ILibraryContextFactory factory,
        ILogger<SorterLibraryLocator> logger,
        ILibraryService? libraryService = null)
    {
        _factory = factory;
        _logger = logger;
        _libraryService = libraryService;
    }

    public async Task<SorterLibraryInfo?> GetActiveAsync(CancellationToken ct = default)
    {
        try
        {
            if (_libraryService is not null)
            {
                var info = await _libraryService.GetActiveAsync(ct).ConfigureAwait(true);
                return info is null ? null : new SorterLibraryInfo(info.Id, info.Name, info.RootPath);
            }

            await using var ctx = _factory.Create();
            var rows = await ctx.Libraries
                .AsNoTracking()
                .Select(l => new { l.Id, l.Name, l.RootPath, l.LastOpenedAt, l.CreatedAt })
                .ToListAsync(ct)
                .ConfigureAwait(true);
            var active = rows
                .OrderByDescending(l => l.LastOpenedAt ?? DateTime.MinValue)
                .ThenByDescending(l => l.CreatedAt)
                .FirstOrDefault();
            return active is null ? null : new SorterLibraryInfo(active.Id, active.Name, active.RootPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Aktive Bibliothek konnte nicht ermittelt werden");
            return null;
        }
    }
}
