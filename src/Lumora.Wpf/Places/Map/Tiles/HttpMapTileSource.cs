using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumora.Places.Map.Tiles;

/// <summary>Konstanten der OpenStreetMap-Quelle (ADR-0051).</summary>
public static class OsmTileSource
{
    /// <summary>Zuordnung laut OSM-Lizenz (ODbL), sichtbar unten rechts auf der Karte.</summary>
    public const string OsmAttribution = "© OpenStreetMap-Mitwirkende";

    /// <summary>„Lumora/0.2.54 (private use)“.</summary>
    public static string BuildUserAgent(string version) => $"Lumora/{version} (private use)";
}

/// <summary>
/// Kartenkacheln aus dem Netz für einen Stil (ADR-0051, seit 0.4.1 ADR-0055 für OSM, CARTO und MapTiler): eindeutiger
/// User-Agent „Lumora/&lt;Version&gt; (private use)“, höchstens zwei gleichzeitige Downloads, Platten-Cache je Stil mit
/// Ablaufzeit aus den HTTP-Kopfzeilen (mindestens <see cref="MapTileOptions.MinimumFreshness"/>, bei Quellen mit
/// Alterungsgrenze höchstens <see cref="MapTileStyleDefinition.MaxCacheAge"/>), keine Vorab- oder Massenabrufe (geladen
/// wird nur, was die Karte gerade zeigt — siehe <see cref="MapTileProvider"/>). Abgelaufene Kacheln werden sofort angezeigt
/// und einmal im Hintergrund neu geholt; Kacheln über der Alterungsgrenze verwirft der Cache. Quellen mit Schlüsselpflicht
/// rufen ohne Schlüssel nie ab. Nach einem Netzfehler liefert die Quelle für <see cref="MapTileOptions.OfflineRetry"/> nur
/// noch aus dem Cache (Zustand <see cref="MapTileSourceState.Offline"/>; 401/403 = <see cref="MapTileSourceState.KeyRejected"/>).
/// Die Kachel-URL (mit Schlüssel) wird nie protokolliert.
/// </summary>
public sealed class HttpMapTileSource : IMapTileSource, IDisposable
{
    private readonly MapTileOptions _options;
    private readonly MapTileStyleDefinition _style;
    private readonly string? _apiKey;
    private readonly MapTileDiskCache _cache;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _downloads;
    private readonly Func<DateTime> _clock;
    private readonly ILogger _logger;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<MapTileId, byte> _refreshing = new();
    private DateTime _offlineUntil = DateTime.MinValue;
    private volatile MapTileSourceState _state = MapTileSourceState.Online;
    private int _downloadCount;
    private readonly Action? _onDownload; // Lumoras Zählung je Dienst und Monat (MapTileUsageCounter)

    /// <param name="handler">Nur Tests: eigener Handler (kein Netz). Produktiv der Standard-Handler.</param>
    public HttpMapTileSource(
        MapTileStyleDefinition style,
        string? apiKey,
        MapTileOptions options,
        MapTileDiskCache cache,
        string userAgent,
        HttpMessageHandler? handler = null,
        Func<DateTime>? clock = null,
        ILogger? logger = null,
        Action? onDownload = null)
    {
        _onDownload = onDownload;
        _style = style;
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        _options = options;
        _cache = cache;
        _clock = clock ?? (() => DateTime.UtcNow);
        _logger = logger ?? NullLogger.Instance;
        _downloads = new SemaphoreSlim(Math.Clamp(Math.Min(options.MaxParallelDownloads, style.MaxParallelDownloads), 1, 2));
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(15);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        UserAgent = userAgent;
        if (style.RequiresKey && _apiKey is null)
        {
            _state = MapTileSourceState.Disabled; // nie unbeschlüsselt abrufen (ADR-0055)
        }
    }

    /// <summary>Nur OpenStreetMap (bisheriger Konstruktor, ADR-0051).</summary>
    public HttpMapTileSource(MapTileOptions options, MapTileDiskCache cache, string userAgent, HttpMessageHandler? handler = null, Func<DateTime>? clock = null, ILogger? logger = null)
        : this(MapStyleCatalog.OpenStreetMap, null, options, cache, userAgent, handler, clock, logger)
    {
    }

    public string UserAgent { get; }

    public MapTileStyleDefinition Style => _style;

    public MapTileSourceState State => _state;

    public string Attribution => _style.Attribution;

    public int MaxZoom => _style.MaxZoom;

    /// <summary>Anzahl tatsächlich ausgeführter Downloads (Tests, Protokoll).</summary>
    public int DownloadCount => Volatile.Read(ref _downloadCount);

    public event EventHandler? StateChanged;

    public async Task<byte[]?> GetTileAsync(MapTileId id, CancellationToken ct)
    {
        if (!IsValid(id))
        {
            return null;
        }

        var now = _clock();
        var cached = _cache.TryRead(id, now);
        if (cached is { IsFresh: true })
        {
            return cached.Value.Bytes;
        }

        if (now < _offlineUntil || _state == MapTileSourceState.Disabled)
        {
            return cached?.Bytes;
        }

        if (cached is { } stale)
        {
            // Abgelaufen: sofort die alte Kachel zeigen, einmal im Hintergrund neu holen (nächstes Öffnen zeigt die neue)
            if (_refreshing.TryAdd(id, 0))
            {
                LastRefresh = Task.Run(async () =>
                {
                    try
                    {
                        await DownloadAsync(id, CancellationToken.None).ConfigureAwait(false);
                    }
                    finally
                    {
                        _refreshing.TryRemove(id, out _);
                    }
                });
            }

            return stale.Bytes;
        }

        return await DownloadAsync(id, ct).ConfigureAwait(false);
    }

    /// <summary>Zuletzt gestartete Hintergrund-Erneuerung einer abgelaufenen Kachel (Tests).</summary>
    internal Task LastRefresh { get; private set; } = Task.CompletedTask;

    private async Task<byte[]?> DownloadAsync(MapTileId id, CancellationToken ct)
    {
        if (_style.BuildUrl(id, _apiKey) is not { } url)
        {
            return null; // Schlüssel fehlt — kein Abruf
        }

        try
        {
            await _downloads.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return null; // Quelle nach einem Stilwechsel freigegeben
        }

        try
        {
            // Inzwischen von einem anderen Auftrag geholt?
            var again = _cache.TryRead(id, _clock());
            if (again is { IsFresh: true })
            {
                return again.Value.Bytes;
            }

            if (_clock() < _offlineUntil)
            {
                return again?.Bytes;
            }

            Interlocked.Increment(ref _downloadCount);
            _onDownload?.Invoke();
            using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                SetState(MapTileSourceState.Online);
                return again?.Bytes;
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden && _style.RequiresKey)
            {
                // Schlüssel abgelehnt: nicht weiter anfragen, Hinweis auf der Karte (URL mit Schlüssel nie ins Log)
                _logger.LogWarning("Kartenkacheln ({Style}): Schlüssel abgelehnt ({Status}) — Pause, nur Cache", _style.Id, (int)response.StatusCode);
                GoOffline(MapTileSourceState.KeyRejected);
                return again?.Bytes;
            }

            if (!response.IsSuccessStatusCode)
            {
                // 429/5xx: Server überlastet oder blockt — nicht nachdrängen, eine Weile nur Cache
                _logger.LogWarning("Kartenkachel-Server ({Style}) antwortet {Status} — Pause, nur Cache", _style.Id, (int)response.StatusCode);
                GoOffline(MapTileSourceState.Offline);
                return again?.Bytes;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            var expires = ExpiresAt(response, _clock());
            _cache.Write(id, bytes, expires, _clock());
            SetState(MapTileSourceState.Online);
            return bytes;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _logger.LogInformation("Kartenkacheln ({Style}) nicht erreichbar ({Error}) — offline, nur Cache", _style.Id, ex.GetType().Name);
            GoOffline(MapTileSourceState.Offline);
            return null;
        }
        catch (ObjectDisposedException)
        {
            return null; // Stil inzwischen gewechselt und diese Quelle freigegeben (StyledMapTileSource)
        }
        finally
        {
            try
            {
                _downloads.Release();
            }
            catch (ObjectDisposedException)
            {
                // freigegeben, s. o.
            }
        }
    }

    /// <summary>
    /// Ablaufzeit aus Cache-Control max-age bzw. Expires, mindestens <see cref="MapTileOptions.MinimumFreshness"/>; bei
    /// Quellen mit Alterungsgrenze höchstens bis zu dieser (dann holt die Karte vorher neu).
    /// </summary>
    private DateTime ExpiresAt(HttpResponseMessage response, DateTime now)
    {
        var expires = now + _options.MinimumFreshness;
        if (response.Headers.CacheControl?.MaxAge is { } maxAge && now + maxAge > expires)
        {
            expires = now + maxAge;
        }
        else if (response.Content.Headers.Expires is { } at && at.UtcDateTime > expires)
        {
            expires = at.UtcDateTime;
        }

        if (_style.MaxCacheAge is { } limit && expires > now + limit)
        {
            expires = now + limit;
        }

        return expires;
    }

    private void GoOffline(MapTileSourceState state)
    {
        _offlineUntil = _clock() + _options.OfflineRetry;
        SetState(state);
    }

    private void SetState(MapTileSourceState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool IsValid(MapTileId id)
    {
        if (id.Zoom < 0 || id.Zoom > MaxZoom)
        {
            return false;
        }

        var n = 1 << id.Zoom;
        return id.X >= 0 && id.X < n && id.Y >= 0 && id.Y < n;
    }

    public void Dispose()
    {
        _http.Dispose();
        _downloads.Dispose();
    }
}
