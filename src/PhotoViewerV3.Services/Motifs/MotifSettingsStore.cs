using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PhotoViewerV3.Domain.Entities;
using PhotoViewerV3.Services.Database;

namespace PhotoViewerV3.Services.Motifs;

/// <summary>Lese-/Schreibzugriff auf die Einstellungen der Motiv-Erkennung.</summary>
public interface IMotifSettingsStore
{
    Task<MotifSettings> LoadAsync(CancellationToken ct = default);

    Task SaveAsync(MotifSettings settings, CancellationToken ct = default);
}

/// <summary>Settings-Tabelle: <c>motifs.enabled</c>, <c>motifs.useGpu</c> („true“/„false“), <c>motifs.threshold</c> (invariant).</summary>
public sealed class MotifSettingsStore(ILibraryContextFactory factory) : IMotifSettingsStore
{
    public async Task<MotifSettings> LoadAsync(CancellationToken ct = default)
    {
        await using var ctx = factory.Create();
        var rows = await ctx.Settings.AsNoTracking()
            .Where(s => s.Key == MotifSettings.EnabledKey || s.Key == MotifSettings.UseGpuKey || s.Key == MotifSettings.ThresholdKey)
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct)
            .ConfigureAwait(false);
        var defaults = MotifSettings.Default;
        var threshold = rows.TryGetValue(MotifSettings.ThresholdKey, out var text)
                        && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value is > 0 and < 1
            ? value
            : defaults.DefaultThreshold;
        return new MotifSettings(
            ReadBool(rows, MotifSettings.EnabledKey, defaults.Enabled),
            ReadBool(rows, MotifSettings.UseGpuKey, defaults.UseGpu),
            threshold);
    }

    public async Task SaveAsync(MotifSettings settings, CancellationToken ct = default)
    {
        await using var ctx = factory.Create();
        await UpsertAsync(ctx, MotifSettings.EnabledKey, settings.Enabled ? "true" : "false", ct).ConfigureAwait(false);
        await UpsertAsync(ctx, MotifSettings.UseGpuKey, settings.UseGpu ? "true" : "false", ct).ConfigureAwait(false);
        await UpsertAsync(ctx, MotifSettings.ThresholdKey, settings.DefaultThreshold.ToString("R", CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static bool ReadBool(Dictionary<string, string?> rows, string key, bool fallback)
        => rows.TryGetValue(key, out var text) && bool.TryParse(text, out var value) ? value : fallback;

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
