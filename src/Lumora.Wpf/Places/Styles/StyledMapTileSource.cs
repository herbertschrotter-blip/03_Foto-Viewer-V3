using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Places.Map.Tiles;
using Wpf.Ui.Appearance;

namespace Lumora.Places.Styles;

/// <summary>Hell oder dunkel — das wirksame Farbschema von Lumora (für den Kartenstil „Automatisch“).</summary>
public interface IMapThemeSource
{
    bool IsDark { get; }

    /// <summary>Farbschema gewechselt (beliebiger Thread).</summary>
    event EventHandler? Changed;
}

/// <summary>Farbschema aus WPF-UI (<see cref="ApplicationThemeManager"/>; „System“ folgt Windows live).</summary>
public sealed class WpfUiMapThemeSource : IMapThemeSource
{
    public WpfUiMapThemeSource()
    {
        ApplicationThemeManager.Changed += OnThemeChanged; // Singleton, lebt so lange wie die App
    }

    public bool IsDark => ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;

    public event EventHandler? Changed;

    private void OnThemeChanged(ApplicationTheme theme, Color accent) => Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// Kachelquelle mit umschaltbarem Stil (ADR-0055): bestimmt aus gewähltem Stil, Farbschema und Schlüsseln
/// (<see cref="MapStyleCatalog.Resolve"/>) die konkrete Quelle und tauscht sie bei jeder Änderung aus — „Automatisch“
/// folgt so dem Farbschema live. Ist die Kachelquelle abgeschaltet (Tests, Rauchtest, <c>LUMORA_MAP_TILES=off</c>),
/// wird trotzdem der Stil aufgelöst (Namensnennung, Hinweis), aber nie etwas abgerufen. Vor dem ersten Lesen der
/// Einstellungen liefert die Quelle keine Kachel (kein Abruf mit dem falschen Stil).
/// </summary>
public sealed class StyledMapTileSource : ISwitchableMapTileSource
{
    private readonly IMapStyleService _styles;
    private readonly IMapThemeSource _theme;
    private readonly MapTileOptions _options;
    private readonly Func<MapStyleResolution, IMapTileSource> _factory;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private IMapTileSource _inner;
    private int _version; // steigt mit jeder ausgetauschten Quelle

    /// <param name="factory">Erzeugt die Netz-Quelle eines aufgelösten Stils (nur bei eingeschalteter Kachelquelle).</param>
    public StyledMapTileSource(
        IMapStyleService styles,
        IMapThemeSource theme,
        MapTileOptions options,
        Func<MapStyleResolution, IMapTileSource> factory,
        ILogger? logger = null)
    {
        _styles = styles;
        _theme = theme;
        _options = options;
        _factory = factory;
        _logger = logger ?? NullLogger.Instance;
        Resolution = Resolve();
        _inner = Create(Resolution);
        _inner.StateChanged += OnInnerStateChanged;
        _styles.Changed += (_, _) => Update();
        _theme.Changed += (_, _) => Update();
        _ = _styles.EnsureLoadedAsync();
    }

    /// <summary>Aufgelöster Stil (Quelle, Hinweis beim Rückfall).</summary>
    public MapStyleResolution Resolution { get; private set; }

    /// <summary>Konkrete Quelle des aufgelösten Stils (Tests).</summary>
    public IMapTileSource Inner => _inner;

    public MapTileSourceState State => _inner.State;

    public string Attribution => Resolution.Definition.Attribution;

    public int MaxZoom => Resolution.Definition.MaxZoom;

    public event EventHandler? StateChanged;

    /// <summary>Die Quelle wurde ausgetauscht (anderer Stil, Schlüssel oder Farbschema) — Kacheln neu laden.</summary>
    public event EventHandler? SourceChanged;

    public async Task<byte[]?> GetTileAsync(MapTileId id, CancellationToken ct)
    {
        var version = Volatile.Read(ref _version);
        await _styles.EnsureLoadedAsync(ct).ConfigureAwait(false);
        if (version != Volatile.Read(ref _version))
        {
            return null; // Auftrag gehörte zur vorigen Quelle (der Provider fordert neu an) — kein Abruf mit fremdem Stil
        }

        return await _inner.GetTileAsync(id, ct).ConfigureAwait(false);
    }

    /// <summary>Stil neu auflösen; bei Änderung die Quelle austauschen.</summary>
    public void Update()
    {
        IMapTileSource old;
        lock (_gate)
        {
            var next = Resolve();
            if (next == Resolution)
            {
                return;
            }

            var sameSource = next.Definition == Resolution.Definition && next.ApiKey == Resolution.ApiKey;
            Resolution = next;
            if (sameSource)
            {
                old = _inner; // nur der Hinweis hat sich geändert
            }
            else
            {
                old = _inner;
                old.StateChanged -= OnInnerStateChanged;
                Interlocked.Increment(ref _version);
                _inner = Create(next);
                _inner.StateChanged += OnInnerStateChanged;
                RetireLater(old);
            }
        }

        _logger.LogInformation("Kartenstil: {Requested} → Quelle {Source}{Fallback}", Resolution.Requested, Resolution.Definition.Id, Resolution.IsFallback ? " (Rückfall, Schlüssel fehlt)" : string.Empty);
        SourceChanged?.Invoke(this, EventArgs.Empty);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private MapStyleResolution Resolve() => MapStyleCatalog.Resolve(_styles.Style, _theme.IsDark, _styles.Keys);

    private IMapTileSource Create(MapStyleResolution resolution)
        => _options.Enabled ? _factory(resolution) : new DisabledMapTileSource(resolution.Definition);

    private void OnInnerStateChanged(object? sender, EventArgs e) => StateChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Alte Quelle erst nach laufenden Abrufen freigeben (keine Ausnahme mitten im Download).</summary>
    private static void RetireLater(IMapTileSource old)
    {
        if (old is IDisposable disposable)
        {
            _ = Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ => disposable.Dispose(), TaskScheduler.Default);
        }
    }
}
