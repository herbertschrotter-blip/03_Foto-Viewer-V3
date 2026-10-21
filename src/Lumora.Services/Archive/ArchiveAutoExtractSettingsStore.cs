using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Lumora.Data;
using Lumora.Domain.Entities;
using Lumora.Services.Database;

namespace Lumora.Services.Archive;

/// <summary>Lese-/Schreibzugriff auf die Einstellungen des automatischen Entpackens — je Bibliothek (ADR-0033).</summary>
public interface IArchiveAutoExtractSettingsStore
{
    /// <summary>Einstellungen einer Bibliothek; fehlt ein Schlüssel oder ist er unlesbar, gilt „aus“.</summary>
    Task<ArchiveAutoExtractSettings> LoadAsync(int libraryId, CancellationToken ct = default);

    /// <summary>Alle gespeicherten Einstellungen je LibraryId (Bibliotheken ohne Eintrag fehlen: Standard aus).</summary>
    Task<IReadOnlyDictionary<int, ArchiveAutoExtractSettings>> LoadAllAsync(CancellationToken ct = default);

    Task SaveAsync(int libraryId, ArchiveAutoExtractSettings settings, CancellationToken ct = default);

    /// <summary>
    /// Einmalige Übernahme der globalen Einstellung bis 0.2.30: jede bestehende Bibliothek ohne eigenen Eintrag erhält
    /// die globalen Werte, danach werden die globalen Schlüssel entfernt (eine Transaktion). Liefert die Zahl der
    /// Bibliotheken, die die Vorgabe übernommen haben; ohne globale Schlüssel passiert nichts (0).
    /// </summary>
    Task<int> MigrateGlobalDefaultAsync(CancellationToken ct = default);
}

/// <summary>
/// Settings-Tabelle: <c>archives.autoExtract.enabled.{LibraryId}</c> und <c>archives.autoExtract.recycle.{LibraryId}</c>
/// („true“/„false“) — dasselbe Muster wie <c>archive.lastTarget.{LibraryId}</c>, keine Schema-Änderung. LibraryIds werden
/// nicht wiederverwendet (AUTOINCREMENT); Einträge gelöschter Bibliotheken bleiben folgenlos liegen.
/// </summary>
public sealed class ArchiveAutoExtractSettingsStore : IArchiveAutoExtractSettingsStore
{
    private static readonly string EnabledPrefix = ArchiveAutoExtractSettings.LegacyEnabledKey + ".";
    private static readonly string RecyclePrefix = ArchiveAutoExtractSettings.LegacyRecycleKey + ".";

    private readonly ILibraryContextFactory _factory;

    public ArchiveAutoExtractSettingsStore(ILibraryContextFactory factory)
    {
        _factory = factory;
    }

    public async Task<ArchiveAutoExtractSettings> LoadAsync(int libraryId, CancellationToken ct = default)
    {
        var enabledKey = ArchiveAutoExtractSettings.EnabledKeyFor(libraryId);
        var recycleKey = ArchiveAutoExtractSettings.RecycleKeyFor(libraryId);
        await using var ctx = _factory.Create();
        var rows = await ctx.Settings.AsNoTracking()
            .Where(s => s.Key == enabledKey || s.Key == recycleKey)
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct)
            .ConfigureAwait(false);

        return new ArchiveAutoExtractSettings(
            ReadBool(rows, enabledKey, ArchiveAutoExtractSettings.Default.Enabled),
            ReadBool(rows, recycleKey, ArchiveAutoExtractSettings.Default.RecycleAfterExtract));
    }

    public async Task<IReadOnlyDictionary<int, ArchiveAutoExtractSettings>> LoadAllAsync(CancellationToken ct = default)
    {
        await using var ctx = _factory.Create();
        var rows = await ctx.Settings.AsNoTracking()
            .Where(s => s.Key.StartsWith(EnabledPrefix) || s.Key.StartsWith(RecyclePrefix))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var enabled = new Dictionary<int, bool>();
        var recycle = new Dictionary<int, bool>();
        foreach (var row in rows)
        {
            if (TryParse(row, EnabledPrefix, out var id, out var value))
            {
                enabled[id] = value;
            }
            else if (TryParse(row, RecyclePrefix, out id, out value))
            {
                recycle[id] = value;
            }
        }

        var result = new Dictionary<int, ArchiveAutoExtractSettings>();
        foreach (var id in enabled.Keys.Union(recycle.Keys))
        {
            result[id] = new ArchiveAutoExtractSettings(
                enabled.GetValueOrDefault(id, ArchiveAutoExtractSettings.Default.Enabled),
                recycle.GetValueOrDefault(id, ArchiveAutoExtractSettings.Default.RecycleAfterExtract));
        }

        return result;
    }

    public async Task SaveAsync(int libraryId, ArchiveAutoExtractSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await using var ctx = _factory.Create();
        await UpsertAsync(ctx, ArchiveAutoExtractSettings.EnabledKeyFor(libraryId), settings.Enabled, ct).ConfigureAwait(false);
        await UpsertAsync(ctx, ArchiveAutoExtractSettings.RecycleKeyFor(libraryId), settings.RecycleAfterExtract, ct).ConfigureAwait(false);
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> MigrateGlobalDefaultAsync(CancellationToken ct = default)
    {
        await using var ctx = _factory.Create();
        var legacy = await ctx.Settings
            .Where(s => s.Key == ArchiveAutoExtractSettings.LegacyEnabledKey || s.Key == ArchiveAutoExtractSettings.LegacyRecycleKey)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (legacy.Count == 0)
        {
            return 0;
        }

        var values = legacy.ToDictionary(s => s.Key, s => s.Value);
        var global = new ArchiveAutoExtractSettings(
            ReadBool(values, ArchiveAutoExtractSettings.LegacyEnabledKey, ArchiveAutoExtractSettings.Default.Enabled),
            ReadBool(values, ArchiveAutoExtractSettings.LegacyRecycleKey, ArchiveAutoExtractSettings.Default.RecycleAfterExtract));

        var libraryIds = await ctx.Libraries.AsNoTracking().Select(l => l.Id).ToListAsync(ct).ConfigureAwait(false);
        var ownKeys = (await ctx.Settings.AsNoTracking()
                .Where(s => s.Key.StartsWith(EnabledPrefix) || s.Key.StartsWith(RecyclePrefix))
                .Select(s => s.Key)
                .ToListAsync(ct)
                .ConfigureAwait(false))
            .ToHashSet(StringComparer.Ordinal);

        var adopted = 0;
        foreach (var id in libraryIds)
        {
            var enabledKey = ArchiveAutoExtractSettings.EnabledKeyFor(id);
            var recycleKey = ArchiveAutoExtractSettings.RecycleKeyFor(id);
            if (ownKeys.Contains(enabledKey) || ownKeys.Contains(recycleKey))
            {
                continue; // eigene Einstellung der Bibliothek hat Vorrang
            }

            ctx.Settings.Add(new Setting { Key = enabledKey, Value = Text(global.Enabled) });
            ctx.Settings.Add(new Setting { Key = recycleKey, Value = Text(global.RecycleAfterExtract) });
            adopted++;
        }

        ctx.Settings.RemoveRange(legacy); // Marker: die Übernahme läuft genau einmal
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        return adopted;
    }

    private static bool TryParse(Setting row, string prefix, out int libraryId, out bool value)
    {
        libraryId = 0;
        value = false;
        return row.Key.StartsWith(prefix, StringComparison.Ordinal)
               && int.TryParse(row.Key.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out libraryId)
               && bool.TryParse(row.Value, out value);
    }

    private static bool ReadBool(Dictionary<string, string?> rows, string key, bool fallback)
        => rows.TryGetValue(key, out var text) && bool.TryParse(text, out var value) ? value : fallback;

    private static string Text(bool value) => value ? "true" : "false";

    private static async Task UpsertAsync(LibraryContext ctx, string key, bool value, CancellationToken ct)
    {
        var row = await ctx.Settings.FindAsync([key], ct).ConfigureAwait(false);
        if (row is null)
        {
            ctx.Settings.Add(new Setting { Key = key, Value = Text(value) });
        }
        else
        {
            row.Value = Text(value);
        }
    }
}
