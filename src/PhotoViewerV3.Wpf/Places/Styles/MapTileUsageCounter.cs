using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoViewerV3.Domain.Entities;
using PhotoViewerV3.Places.Map.Tiles;
using PhotoViewerV3.Services.Database;

namespace PhotoViewerV3.Places.Styles;

/// <summary>Lumoras eigene Zählung der Kachelabrufe eines Dienstes im Kalendermonat (UTC).</summary>
/// <param name="Month">„2026-10“.</param>
/// <param name="UpdatedUtc">Zeitpunkt des letzten gezählten Abrufs (null = noch keiner).</param>
public sealed record MapTileUsage(string Month, long Count, DateTime? UpdatedUtc);

/// <summary>
/// Zählt Kachelabrufe je Kartendienst (CARTO, MapTiler) und Kalendermonat UTC (ADR-0055) — CARTO bietet keine
/// dokumentierte Verbrauchs-API, MapTiler nur mit Service-Zugangsdaten; die Zahl im Konto des Anbieters bleibt maßgeblich.
/// Gezählt wird jeder tatsächliche Abruf (auch „Schlüssel prüfen“), nicht Kacheln aus dem Cache. Ablage in der
/// Settings-Tabelle (<c>ui.map.&lt;dienst&gt;.usage</c> = „Monat;Anzahl;Zeit“), gebündelt geschrieben.
/// </summary>
public interface IMapTileUsageCounter
{
    void Count(MapTileVendor vendor);

    /// <summary>Stand des laufenden Monats (ein neuer Monat beginnt bei 0).</summary>
    MapTileUsage Get(MapTileVendor vendor);

    Task EnsureLoadedAsync(CancellationToken ct = default);

    /// <summary>Ausstehende Zählungen sofort speichern.</summary>
    Task FlushAsync(CancellationToken ct = default);
}

public sealed class MapTileUsageCounter : IMapTileUsageCounter
{
    /// <summary>Spätestens nach so vielen Abrufen bzw. dieser Zeit wird gespeichert.</summary>
    private const int FlushEvery = 50;
    private static readonly TimeSpan FlushDelay = TimeSpan.FromSeconds(15);

    private readonly ILibraryContextFactory _factory;
    private readonly Func<DateTime> _clock;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly Dictionary<MapTileVendor, MapTileUsage> _usage = new();
    private readonly SemaphoreSlim _writer = new(1, 1);
    private Task? _load;
    private int _pending;
    private int _flushScheduled;

    public MapTileUsageCounter(ILibraryContextFactory factory, Func<DateTime>? clock = null, ILogger<MapTileUsageCounter>? logger = null)
    {
        _factory = factory;
        _clock = clock ?? (() => DateTime.UtcNow);
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public static string MonthOf(DateTime utc) => utc.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    public static string SettingKey(MapTileVendor vendor) => $"ui.map.{MapServiceInfo.For(vendor).Id}.usage";

    public void Count(MapTileVendor vendor)
    {
        if (vendor == MapTileVendor.OpenStreetMap)
        {
            return;
        }

        var now = _clock();
        var month = MonthOf(now);
        lock (_gate)
        {
            var current = _usage.TryGetValue(vendor, out var u) && u.Month == month ? u.Count : 0;
            _usage[vendor] = new MapTileUsage(month, current + 1, now);
            _pending++;
        }

        _ = EnsureLoadedAsync();
        if (Volatile.Read(ref _pending) >= FlushEvery)
        {
            _ = FlushAsync();
        }
        else if (Interlocked.CompareExchange(ref _flushScheduled, 1, 0) == 0)
        {
            _ = Task.Delay(FlushDelay).ContinueWith(_ => FlushAsync(), TaskScheduler.Default).Unwrap();
        }
    }

    public MapTileUsage Get(MapTileVendor vendor)
    {
        var month = MonthOf(_clock());
        lock (_gate)
        {
            return _usage.TryGetValue(vendor, out var u) && u.Month == month ? u : new MapTileUsage(month, 0, null);
        }
    }

    public Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            return _load ??= Task.Run(LoadAsync, CancellationToken.None);
        }
    }

    public async Task FlushAsync(CancellationToken ct = default)
    {
        Interlocked.Exchange(ref _flushScheduled, 0);
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        await _writer.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            List<(MapTileVendor Vendor, MapTileUsage Usage)> snapshot;
            lock (_gate)
            {
                if (_pending == 0)
                {
                    return;
                }

                _pending = 0;
                snapshot = [.. _usage.Select(kv => (kv.Key, kv.Value))];
            }

            await using var ctx = _factory.Create();
            foreach (var (vendor, usage) in snapshot)
            {
                var key = SettingKey(vendor);
                var value = $"{usage.Month};{usage.Count.ToString(CultureInfo.InvariantCulture)};{usage.UpdatedUtc?.ToString("O", CultureInfo.InvariantCulture)}";
                var setting = await ctx.Settings.FirstOrDefaultAsync(s => s.Key == key, ct).ConfigureAwait(false);
                if (setting is null)
                {
                    ctx.Settings.Add(new Setting { Key = key, Value = value });
                }
                else
                {
                    setting.Value = value;
                }
            }

            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Kachel-Zählung nicht gespeichert (nächster Versuch beim nächsten Abruf)");
            lock (_gate)
            {
                _pending++;
            }
        }
        finally
        {
            _writer.Release();
        }
    }

    private async Task LoadAsync()
    {
        try
        {
            await using var ctx = _factory.Create();
            foreach (var info in MapServiceInfo.All)
            {
                var key = SettingKey(info.Vendor);
                var value = (await ctx.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key).ConfigureAwait(false))?.Value;
                if (Parse(value) is not { } stored)
                {
                    continue;
                }

                lock (_gate)
                {
                    // Abrufe vor dem Laden (gleicher Monat) zu den gespeicherten addieren
                    var counted = _usage.TryGetValue(info.Vendor, out var u) && u.Month == stored.Month ? u : null;
                    _usage[info.Vendor] = counted is null
                        ? (_usage.ContainsKey(info.Vendor) ? _usage[info.Vendor] : stored)
                        : new MapTileUsage(stored.Month, stored.Count + counted.Count, counted.UpdatedUtc);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Kachel-Zählung nicht gelesen");
        }
    }

    internal static MapTileUsage? Parse(string? value)
    {
        var parts = value?.Split(';');
        if (parts is not { Length: >= 2 } || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
        {
            return null;
        }

        DateTime? updated = parts.Length >= 3 && DateTime.TryParse(parts[2], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at : null;
        return new MapTileUsage(parts[0], count, updated);
    }
}
