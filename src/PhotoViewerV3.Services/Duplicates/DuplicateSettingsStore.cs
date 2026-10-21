using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PhotoViewerV3.Domain.Entities;
using PhotoViewerV3.Services.Database;

namespace PhotoViewerV3.Services.Duplicates;

/// <summary>Einstellungen der Seite „Doppelte“ (Schwelle „ähnlich“, Angaben übertragen) in der Settings-Tabelle des Katalogs.</summary>
public interface IDuplicateSettingsStore
{
    Task<DuplicateSettings> LoadAsync(CancellationToken ct = default);

    Task SaveAsync(DuplicateSettings settings, CancellationToken ct = default);
}

/// <inheritdoc cref="IDuplicateSettingsStore"/>
public sealed class DuplicateSettingsStore(ILibraryContextFactory factory) : IDuplicateSettingsStore
{
    public async Task<DuplicateSettings> LoadAsync(CancellationToken ct = default)
    {
        await using var ctx = factory.Create();
        var rows = await ctx.Settings.AsNoTracking()
            .Where(s => s.Key == DuplicateSettings.ThresholdKey || s.Key == DuplicateSettings.TransferKey)
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct)
            .ConfigureAwait(false);
        var defaults = DuplicateSettings.Default;
        var threshold = rows.TryGetValue(DuplicateSettings.ThresholdKey, out var text)
                        && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, DuplicateSettings.MinThreshold, DuplicateSettings.MaxThreshold)
            : defaults.SimilarThreshold;
        var transfer = rows.TryGetValue(DuplicateSettings.TransferKey, out var flag) && bool.TryParse(flag, out var b) ? b : defaults.TransferMetadata;
        return new DuplicateSettings(threshold, transfer);
    }

    public async Task SaveAsync(DuplicateSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await using var ctx = factory.Create();
        await UpsertAsync(ctx, DuplicateSettings.ThresholdKey, Math.Clamp(settings.SimilarThreshold, DuplicateSettings.MinThreshold, DuplicateSettings.MaxThreshold).ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
        await UpsertAsync(ctx, DuplicateSettings.TransferKey, settings.TransferMetadata ? "true" : "false", ct).ConfigureAwait(false);
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static async Task UpsertAsync(Data.LibraryContext ctx, string key, string value, CancellationToken ct)
    {
        var row = await ctx.Settings.FindAsync([key], ct).ConfigureAwait(false);
        if (row is null)
        {
            ctx.Settings.Add(new Setting { Key = key, Value = value });
        }
        else
        {
            row.Value = value;
        }
    }
}
