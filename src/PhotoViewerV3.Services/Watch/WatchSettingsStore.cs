using Microsoft.EntityFrameworkCore;
using PhotoViewerV3.Data;
using PhotoViewerV3.Domain.Entities;
using PhotoViewerV3.Services.Database;

namespace PhotoViewerV3.Services.Watch;

/// <summary>Einstellungen der Ordnerüberwachung (Settings-Tabelle).</summary>
public sealed record WatchSettings(bool Enabled, TimeSpan QuietPeriod)
{
    public const string EnabledKey = "watch.enabled";
    public const string QuietSecondsKey = "watch.quietSeconds";
    public const int MinQuietSeconds = 1;
    public const int MaxQuietSeconds = 60;
    public const int DefaultQuietSeconds = 3;

    public static WatchSettings Default { get; } = new(true, TimeSpan.FromSeconds(DefaultQuietSeconds));

    /// <summary>Begrenzt die Ruhezeit auf 1–60 s (ganze Sekunden).</summary>
    public static TimeSpan ClampQuietPeriod(TimeSpan value)
    {
        var seconds = (int)Math.Round(value.TotalSeconds);
        return TimeSpan.FromSeconds(Math.Clamp(seconds, MinQuietSeconds, MaxQuietSeconds));
    }
}

/// <summary>Lese-/Schreibzugriff auf die Einstellungen der Ordnerüberwachung.</summary>
public interface IWatchSettingsStore
{
    Task<WatchSettings> LoadAsync(CancellationToken ct = default);

    Task SaveAsync(WatchSettings settings, CancellationToken ct = default);
}

/// <summary>Settings-Tabelle: <c>watch.enabled</c> („true“/„false“) und <c>watch.quietSeconds</c> (ganze Sekunden).</summary>
public sealed class WatchSettingsStore : IWatchSettingsStore
{
    private readonly ILibraryContextFactory _factory;

    public WatchSettingsStore(ILibraryContextFactory factory)
    {
        _factory = factory;
    }

    public async Task<WatchSettings> LoadAsync(CancellationToken ct = default)
    {
        await using var ctx = _factory.Create();
        var rows = await ctx.Settings.AsNoTracking()
            .Where(s => s.Key == WatchSettings.EnabledKey || s.Key == WatchSettings.QuietSecondsKey)
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct)
            .ConfigureAwait(false);

        var enabled = WatchSettings.Default.Enabled;
        if (rows.TryGetValue(WatchSettings.EnabledKey, out var enabledText) && bool.TryParse(enabledText, out var parsedEnabled))
        {
            enabled = parsedEnabled;
        }

        var quiet = WatchSettings.Default.QuietPeriod;
        if (rows.TryGetValue(WatchSettings.QuietSecondsKey, out var quietText) && int.TryParse(quietText, out var parsedSeconds))
        {
            quiet = WatchSettings.ClampQuietPeriod(TimeSpan.FromSeconds(parsedSeconds));
        }

        return new WatchSettings(enabled, quiet);
    }

    public async Task SaveAsync(WatchSettings settings, CancellationToken ct = default)
    {
        await using var ctx = _factory.Create();
        await UpsertAsync(ctx, WatchSettings.EnabledKey, settings.Enabled ? "true" : "false", ct).ConfigureAwait(false);
        await UpsertAsync(ctx, WatchSettings.QuietSecondsKey, ((int)WatchSettings.ClampQuietPeriod(settings.QuietPeriod).TotalSeconds).ToString(), ct).ConfigureAwait(false);
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static async Task UpsertAsync(LibraryContext ctx, string key, string value, CancellationToken ct)
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
