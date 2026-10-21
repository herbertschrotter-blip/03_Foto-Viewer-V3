using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PhotoViewerV3.Places.Map.Tiles;

/// <summary>
/// Kartenkacheln von OpenStreetMap (von Herbert freigegeben, ADR-0051) nach den Kachel-Nutzungsregeln: eindeutiger
/// User-Agent „Lumora/&lt;Version&gt; (private use)“, höchstens zwei gleichzeitige Downloads, Platten-Cache mit
/// Ablaufzeit aus den HTTP-Kopfzeilen (mindestens <see cref="MapTileOptions.MinimumFreshness"/>), keine Vorab- oder
/// Massenabrufe (geladen wird nur, was die Karte gerade zeigt — siehe <see cref="MapTileProvider"/>). Abgelaufene
/// Kacheln werden sofort angezeigt und einmal im Hintergrund neu geholt. Nach einem Netzfehler liefert die Quelle für
/// <see cref="MapTileOptions.OfflineRetry"/> nur noch aus dem Cache (Zustand <see cref="MapTileSourceState.Offline"/>).
/// </summary>
public sealed class OsmTileSource : IMapTileSource, IDisposable
{
    /// <summary>Zuordnung laut OSM-Lizenz (ODbL), sichtbar unten rechts auf der Karte.</summary>
    public const string OsmAttribution = "© OpenStreetMap-Mitwirkende";

    private readonly MapTileOptions _options;
    private readonly MapTileDiskCache _cache;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _downloads;
    private readonly Func<DateTime> _clock;
    private readonly ILogger _logger;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<MapTileId, byte> _refreshing = new();
    private DateTime _offlineUntil = DateTime.MinValue;
    private volatile MapTileSourceState _state = MapTileSourceState.Online;

    /// <param name="handler">Nur Tests: eigener Handler (kein Netz). Produktiv der Standard-Handler.</param>
    public OsmTileSource(MapTileOptions options, MapTileDiskCache cache, string userAgent, HttpMessageHandler? handler = null, Func<DateTime>? clock = null, ILogger? logger = null)
    {
        _options = options;
        _cache = cache;
        _clock = clock ?? (() => DateTime.UtcNow);
        _logger = logger ?? NullLogger.Instance;
        _downloads = new SemaphoreSlim(Math.Clamp(options.MaxParallelDownloads, 1, 2));
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(15);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        UserAgent = userAgent;
    }

    /// <summary>„Lumora/0.2.54 (private use)“.</summary>
    public static string BuildUserAgent(string version) => $"Lumora/{version} (private use)";

    public string UserAgent { get; }

    public MapTileSourceState State => _state;

    public string Attribution => OsmAttribution;

    public int MaxZoom => 19;

    private int _downloadCount;

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

        if (now < _offlineUntil)
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
        await _downloads.WaitAsync(ct).ConfigureAwait(false);
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

            var url = _options.UrlTemplate
                .Replace("{z}", id.Zoom.ToString(), StringComparison.Ordinal)
                .Replace("{x}", id.X.ToString(), StringComparison.Ordinal)
                .Replace("{y}", id.Y.ToString(), StringComparison.Ordinal);
            Interlocked.Increment(ref _downloadCount);
            using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                SetState(MapTileSourceState.Online);
                return again?.Bytes;
            }

            if (!response.IsSuccessStatusCode)
            {
                // 429/5xx: Server überlastet oder blockt — nicht nachdrängen, eine Weile nur Cache
                _logger.LogWarning("Kartenkachel-Server antwortet {Status} — Pause, nur Cache", (int)response.StatusCode);
                GoOffline();
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
            _logger.LogInformation("Kartenkacheln nicht erreichbar ({Message}) — offline, nur Cache", ex.Message);
            GoOffline();
            return null;
        }
        finally
        {
            _downloads.Release();
        }
    }

    /// <summary>Ablaufzeit aus Cache-Control max-age bzw. Expires, mindestens <see cref="MapTileOptions.MinimumFreshness"/>.</summary>
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

        return expires;
    }

    private void GoOffline()
    {
        _offlineUntil = _clock() + _options.OfflineRetry;
        SetState(MapTileSourceState.Offline);
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
