using System;
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

/// <summary>
/// Gewählter Kartenstil und API-Schlüssel des Benutzers (ADR-0055) — gespeichert in der Settings-Tabelle des Katalogs
/// der Sitzung (im versteckten Katalog also dessen eigene Einstellungen). Die Schlüssel stehen unverschlüsselt in der
/// Tabelle, werden aber nie protokolliert.
/// </summary>
public interface IMapStyleService
{
    MapStyle Style { get; }

    MapApiKeys Keys { get; }

    /// <summary>Stil oder Schlüssel geändert (beliebiger Thread).</summary>
    event EventHandler? Changed;

    /// <summary>Einstellungen einmal lesen (weitere Aufrufe warten nur auf das erste Lesen).</summary>
    Task EnsureLoadedAsync(CancellationToken ct = default);

    Task SetStyleAsync(MapStyle style, CancellationToken ct = default);

    Task SetKeysAsync(MapApiKeys keys, CancellationToken ct = default);
}

/// <summary>Settings-Tabelle als Ablage (Schlüssel <see cref="StyleKey"/>, <see cref="CartoKeySetting"/>, <see cref="MapTilerKeySetting"/>).</summary>
public sealed class MapStyleService : IMapStyleService
{
    public const string StyleKey = "ui.map.style";
    public const string CartoKeySetting = "ui.map.cartoKey";
    public const string MapTilerKeySetting = "ui.map.maptilerKey";

    private readonly ILibraryContextFactory _factory;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private Task? _load;
    private volatile bool _loadFailed;

    public MapStyleService(ILibraryContextFactory factory, ILogger<MapStyleService>? logger = null)
    {
        _factory = factory;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public MapStyle Style { get; private set; } = MapStyle.Automatic;

    public MapApiKeys Keys { get; private set; } = MapApiKeys.None;

    public event EventHandler? Changed;

    public Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_load is { IsCompleted: true } && _loadFailed)
            {
                _loadFailed = false;
                _load = null; // letzter Versuch scheiterte — erneut lesen
            }

            return _load ??= Task.Run(LoadAsync, CancellationToken.None);
        }
    }

    public async Task SetStyleAsync(MapStyle style, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        if (Style == style)
        {
            return;
        }

        await WriteAsync(StyleKey, style.ToString(), ct).ConfigureAwait(false); // erst speichern, dann gelten lassen
        Style = style;
        _logger.LogInformation("Kartenstil: {Style}", style);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task SetKeysAsync(MapApiKeys keys, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        var normalized = new MapApiKeys(Normalize(keys.Carto), Normalize(keys.MapTiler));
        if (normalized == Keys)
        {
            return;
        }

        await WriteAsync(CartoKeySetting, normalized.Carto, ct).ConfigureAwait(false); // erst speichern, dann gelten lassen
        await WriteAsync(MapTilerKeySetting, normalized.MapTiler, ct).ConfigureAwait(false);
        Keys = normalized;
        // Nur ob ein Schlüssel da ist — nie der Schlüssel selbst
        _logger.LogInformation("Karten-Schlüssel gespeichert (CARTO: {Carto}, MapTiler: {MapTiler})", normalized.HasCarto, normalized.HasMapTiler);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task LoadAsync()
    {
        try
        {
            await using var ctx = _factory.Create();
            var rows = await ctx.Settings.AsNoTracking()
                .Where(s => s.Key == StyleKey || s.Key == CartoKeySetting || s.Key == MapTilerKeySetting)
                .ToListAsync()
                .ConfigureAwait(false);
            string? Value(string key) => rows.FirstOrDefault(r => r.Key == key)?.Value;
            Style = MapStyleCatalog.Parse(Value(StyleKey));
            Keys = new MapApiKeys(Normalize(Value(CartoKeySetting)), Normalize(Value(MapTilerKeySetting)));
            _logger.LogInformation("Kartenstil geladen: {Style} (CARTO-Schlüssel: {Carto}, MapTiler-Schlüssel: {MapTiler})", Style, Keys.HasCarto, Keys.HasMapTiler);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Kartenstil konnte nicht gelesen werden — Standard: Automatisch, nächster Versuch beim nächsten Zugriff");
            _loadFailed = true; // vorübergehender Fehler (z. B. Katalog gesperrt) — nicht für die ganze Sitzung merken
            return;
        }

        if (Style != MapStyle.Automatic || Keys != MapApiKeys.None)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task WriteAsync(string key, string? value, CancellationToken ct)
    {
        await using var ctx = _factory.Create();
        var setting = await ctx.Settings.FirstOrDefaultAsync(s => s.Key == key, ct).ConfigureAwait(false);
        if (setting is null)
        {
            ctx.Settings.Add(new Setting { Key = key, Value = value });
        }
        else
        {
            setting.Value = value;
        }

        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
