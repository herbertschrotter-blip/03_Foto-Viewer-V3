using Microsoft.EntityFrameworkCore;
using PhotoViewerV3.Data;
using PhotoViewerV3.Domain.Entities;
using PhotoViewerV3.Services.Database;

namespace PhotoViewerV3.Services.Archive;

/// <summary>Lese-/Schreibzugriff auf die Einstellungen des automatischen Entpackens.</summary>
public interface IArchiveAutoExtractSettingsStore
{
    Task<ArchiveAutoExtractSettings> LoadAsync(CancellationToken ct = default);

    Task SaveAsync(ArchiveAutoExtractSettings settings, CancellationToken ct = default);
}

/// <summary>
/// Settings-Tabelle: <c>archives.autoExtract.enabled</c> und <c>archives.autoExtract.recycle</c> („true“/„false“).
/// Fehlt ein Schlüssel oder ist er unlesbar, gilt „aus“.
/// </summary>
public sealed class ArchiveAutoExtractSettingsStore : IArchiveAutoExtractSettingsStore
{
    private readonly ILibraryContextFactory _factory;

    public ArchiveAutoExtractSettingsStore(ILibraryContextFactory factory)
    {
        _factory = factory;
    }

    public async Task<ArchiveAutoExtractSettings> LoadAsync(CancellationToken ct = default)
    {
        await using var ctx = _factory.Create();
        var rows = await ctx.Settings.AsNoTracking()
            .Where(s => s.Key == ArchiveAutoExtractSettings.EnabledKey || s.Key == ArchiveAutoExtractSettings.RecycleKey)
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct)
            .ConfigureAwait(false);

        return new ArchiveAutoExtractSettings(
            ReadBool(rows, ArchiveAutoExtractSettings.EnabledKey, ArchiveAutoExtractSettings.Default.Enabled),
            ReadBool(rows, ArchiveAutoExtractSettings.RecycleKey, ArchiveAutoExtractSettings.Default.RecycleAfterExtract));
    }

    public async Task SaveAsync(ArchiveAutoExtractSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await using var ctx = _factory.Create();
        await UpsertAsync(ctx, ArchiveAutoExtractSettings.EnabledKey, settings.Enabled, ct).ConfigureAwait(false);
        await UpsertAsync(ctx, ArchiveAutoExtractSettings.RecycleKey, settings.RecycleAfterExtract, ct).ConfigureAwait(false);
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static bool ReadBool(Dictionary<string, string?> rows, string key, bool fallback)
        => rows.TryGetValue(key, out var text) && bool.TryParse(text, out var value) ? value : fallback;

    private static async Task UpsertAsync(LibraryContext ctx, string key, bool value, CancellationToken ct)
    {
        var text = value ? "true" : "false";
        var row = await ctx.Settings.FindAsync([key], ct).ConfigureAwait(false);
        if (row is null)
        {
            ctx.Settings.Add(new Setting { Key = key, Value = text });
        }
        else
        {
            row.Value = text;
        }
    }
}
