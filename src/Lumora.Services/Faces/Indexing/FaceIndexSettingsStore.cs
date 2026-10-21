using Microsoft.EntityFrameworkCore;
using Lumora.Domain.Entities;
using Lumora.Services.Database;

namespace Lumora.Services.Faces.Indexing;

/// <summary>Lese-/Schreibzugriff auf die Einstellungen der Gesichtserkennung.</summary>
public interface IFaceIndexSettingsStore
{
    Task<FaceIndexSettings> LoadAsync(CancellationToken ct = default);

    Task SaveAsync(FaceIndexSettings settings, CancellationToken ct = default);
}

/// <summary>Settings-Tabelle: <c>faces.enabled</c> und <c>faces.useGpu</c> („true“/„false“).</summary>
public sealed class FaceIndexSettingsStore(ILibraryContextFactory factory) : IFaceIndexSettingsStore
{
    public async Task<FaceIndexSettings> LoadAsync(CancellationToken ct = default)
    {
        await using var ctx = factory.Create();
        var rows = await ctx.Settings.AsNoTracking()
            .Where(s => s.Key == FaceIndexSettings.EnabledKey || s.Key == FaceIndexSettings.UseGpuKey)
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct)
            .ConfigureAwait(false);
        var defaults = FaceIndexSettings.Default;
        return new FaceIndexSettings(
            Read(rows, FaceIndexSettings.EnabledKey, defaults.Enabled),
            Read(rows, FaceIndexSettings.UseGpuKey, defaults.UseGpu));
    }

    public async Task SaveAsync(FaceIndexSettings settings, CancellationToken ct = default)
    {
        await using var ctx = factory.Create();
        await UpsertAsync(ctx, FaceIndexSettings.EnabledKey, settings.Enabled, ct).ConfigureAwait(false);
        await UpsertAsync(ctx, FaceIndexSettings.UseGpuKey, settings.UseGpu, ct).ConfigureAwait(false);
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static bool Read(Dictionary<string, string?> rows, string key, bool fallback)
        => rows.TryGetValue(key, out var text) && bool.TryParse(text, out var value) ? value : fallback;

    private static async Task UpsertAsync(Data.LibraryContext ctx, string key, bool value, CancellationToken ct)
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
