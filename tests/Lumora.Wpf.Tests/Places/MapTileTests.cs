using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Lumora.Places.Map.Tiles;

namespace Lumora.Wpf.Tests.Places;

/// <summary>
/// Kachelquelle nach den OSM-Nutzungsregeln (ADR-0051) — ausschließlich mit einem Fake-Handler, KEIN echter Abruf:
/// User-Agent „Lumora/x (private use)“, höchstens zwei gleichzeitige Downloads, Platten-Cache mit Ablaufzeit, offline
/// nur Cache, LRU-Grenze des Platten-Caches, abgeschaltete Quelle lädt nichts.
/// </summary>
public sealed class MapTileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pv3-maptiles-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task Sendet_Lumora_User_Agent_und_nutzt_den_Cache()
    {
        var handler = new FakeHandler();
        var now = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
        using var source = new HttpMapTileSource(new MapTileOptions(), new MapTileDiskCache(_dir, 10_000_000, trimInBackground: false), OsmTileSource.BuildUserAgent("0.2.54"), handler, () => now);

        var first = await source.GetTileAsync(new MapTileId(3, 4, 2), CancellationToken.None);
        var second = await source.GetTileAsync(new MapTileId(3, 4, 2), CancellationToken.None);

        first.Should().Equal(FakeHandler.Png);
        second.Should().Equal(FakeHandler.Png);
        handler.Requests.Should().ContainSingle("zweiter Abruf kommt aus dem Cache");
        handler.Requests[0].RequestUri!.ToString().Should().Be("https://tile.openstreetmap.org/3/4/2.png");
        handler.Requests[0].Headers.UserAgent.ToString().Should().Be("Lumora/0.2.54 (private use)");
        File.Exists(Path.Combine(_dir, "maptiles", "3", "4", "2.png")).Should().BeTrue();

        // Abgelaufen (Cache-Control max-age 1 Tag, mindestens 7 Tage): nach 8 Tagen neu holen
        now = now.AddDays(8);
        (await source.GetTileAsync(new MapTileId(3, 4, 2), CancellationToken.None)).Should().Equal(FakeHandler.Png, "abgelaufen: sofort aus dem Cache");
        await source.LastRefresh;
        handler.Requests.Should().HaveCount(2, "… und einmal im Hintergrund neu geholt");

        (await source.GetTileAsync(new MapTileId(3, 9, 0), CancellationToken.None)).Should().BeNull("x außerhalb von Stufe 3");
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task Hoechstens_zwei_gleichzeitige_Downloads()
    {
        var handler = new FakeHandler { Delay = TimeSpan.FromMilliseconds(80) };
        using var source = new HttpMapTileSource(new MapTileOptions(), new MapTileDiskCache(_dir, 10_000_000, trimInBackground: false), "Lumora/test (private use)", handler);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => source.GetTileAsync(new MapTileId(4, i, 3), CancellationToken.None)));

        handler.MaxConcurrent.Should().BeLessThanOrEqualTo(2);
        handler.Requests.Should().HaveCount(8);
    }

    [Fact]
    public async Task Offline_liefert_nur_Cache_und_meldet_den_Zustand()
    {
        var handler = new FakeHandler();
        var now = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
        using var source = new HttpMapTileSource(new MapTileOptions(), new MapTileDiskCache(_dir, 10_000_000, trimInBackground: false), "Lumora/test (private use)", handler, () => now);
        await source.GetTileAsync(new MapTileId(2, 1, 1), CancellationToken.None);
        var changes = 0;
        source.StateChanged += (_, _) => changes++;

        handler.Fail = true;
        now = now.AddDays(30); // Cache abgelaufen
        var stale = await source.GetTileAsync(new MapTileId(2, 1, 1), CancellationToken.None);
        stale.Should().Equal(FakeHandler.Png, "abgelaufene Kachel aus dem Cache statt nichts");
        await source.LastRefresh;
        source.State.Should().Be(MapTileSourceState.Offline);
        changes.Should().Be(1);

        var requests = handler.Requests.Count;
        (await source.GetTileAsync(new MapTileId(2, 0, 0), CancellationToken.None)).Should().BeNull();
        handler.Requests.Should().HaveCount(requests, "während der Offline-Pause kein neuer Versuch");

        handler.Fail = false;
        now = now.AddMinutes(1);
        (await source.GetTileAsync(new MapTileId(2, 0, 0), CancellationToken.None)).Should().NotBeNull();
        source.State.Should().Be(MapTileSourceState.Online);
    }

    [Fact]
    public void Platten_Cache_raeumt_die_am_laengsten_unbenutzten_Kacheln_weg()
    {
        var cache = new MapTileDiskCache(_dir, maxBytes: 1000, trimInBackground: false);
        var now = DateTime.UtcNow;
        var bytes = new byte[300];
        for (var i = 0; i < 5; i++)
        {
            cache.Write(new MapTileId(5, i, 0), bytes, now.AddDays(7), now.AddMinutes(i));
        }

        cache.TryRead(new MapTileId(5, 0, 0), now.AddMinutes(10)).Should().NotBeNull(); // 0 wird wieder benutzt
        var total = cache.TrimNow();

        total.Should().BeLessThanOrEqualTo(900);
        cache.TryRead(new MapTileId(5, 0, 0), now.AddMinutes(11)).Should().NotBeNull("zuletzt benutzt bleibt");
        cache.TryRead(new MapTileId(5, 1, 0), now.AddMinutes(11)).Should().BeNull("am längsten unbenutzt fliegt raus");
        cache.TryRead(new MapTileId(5, 4, 0), now.AddMinutes(11)).Should().NotBeNull();

        new MapTileDiskCache(string.Empty, 1000).Folder.Should().BeNull("leerer Cache-Ordner = nur Speicher (versteckter Modus)");
    }

    [Fact]
    public async Task Abgeschaltete_Quelle_laedt_nichts()
    {
        var source = new DisabledMapTileSource();
        var provider = new MapTileProvider(source, new MapTileOptions { Enabled = false });
        provider.SetWanted([new MapTileId(0, 0, 0)]);
        (await source.GetTileAsync(new MapTileId(0, 0, 0), CancellationToken.None)).Should().BeNull();
        provider.TryGet(new MapTileId(0, 0, 0)).Should().BeNull();
        provider.State.Should().Be(MapTileSourceState.Disabled);
        MapStyleCatalog.OpenStreetMap.UrlTemplate.Should().StartWith("https://tile.openstreetmap.org/");
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];
        private int _current;

        public List<HttpRequestMessage> Requests { get; } = [];

        public TimeSpan Delay { get; set; }

        public bool Fail { get; set; }

        public int MaxConcurrent { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(request);
                _current++;
                MaxConcurrent = Math.Max(MaxConcurrent, _current);
            }

            try
            {
                if (Delay > TimeSpan.Zero)
                {
                    await Task.Delay(Delay, cancellationToken);
                }

                if (Fail)
                {
                    throw new HttpRequestException("kein Netz (Test)");
                }

                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) };
                response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromDays(1) };
                return response;
            }
            finally
            {
                lock (Requests)
                {
                    _current--;
                }
            }
        }
    }
}
