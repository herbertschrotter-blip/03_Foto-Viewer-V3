using System.Text.Json;
using Microsoft.Extensions.Logging;
using Lumora.Domain.Entities;
using Lumora.Services.Database;

namespace Lumora.Services.Sorter;

/// <summary>
/// Lauf-Protokoll des Sortierers in der Settings-Tabelle: je Art (Sortieren / Flatten) der letzte
/// Lauf mit seinen Batch-Ids, damit „Rückgängig (letzter Lauf)“ alle Batches gemeinsam zurücknimmt.
/// </summary>
public sealed class SorterRunLog
{
    public const string SortKey = "sorter.lastRun";
    public const string FlattenKey = "sorter.lastFlattenRun";

    private readonly ILibraryContextFactory _factory;
    private readonly ILogger<SorterRunLog> _logger;

    public SorterRunLog(ILibraryContextFactory factory, ILogger<SorterRunLog> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task<SorterRunRecord?> GetAsync(string key, CancellationToken ct)
    {
        await using var ctx = _factory.Create();
        var row = await ctx.Settings.FindAsync([key], ct).ConfigureAwait(false);
        if (row?.Value is null || string.IsNullOrWhiteSpace(row.Value))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<SorterRunRecord>(row.Value, SorterJson.Options);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Lauf-Protokoll {Key} ist nicht lesbar", key);
            return null;
        }
    }

    public async Task SaveAsync(string key, SorterRunRecord record, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(record, SorterJson.Options);
        await using var ctx = _factory.Create();
        var row = await ctx.Settings.FindAsync([key], ct).ConfigureAwait(false);
        if (row is null)
        {
            ctx.Settings.Add(new Setting { Key = key, Value = json });
        }
        else
        {
            row.Value = json;
        }

        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
