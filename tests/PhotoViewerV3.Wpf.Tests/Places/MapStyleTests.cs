using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using PhotoViewerV3.Places.Map.Tiles;
using PhotoViewerV3.Places.Styles;

namespace PhotoViewerV3.Wpf.Tests.Places;

/// <summary>
/// Kartenstile (ADR-0055) — ausschließlich mit Fake-Handlern, KEIN echter Abruf: „Automatisch“ folgt Hell/Dunkel,
/// URL-Bau mit und ohne Schlüssel (ohne Schlüssel nie ein CARTO-/MapTiler-Abruf, Rückfall OSM mit Hinweis), Cache je Stil
/// mit 30-Tage-Grenze, Namensnennung je Stil, abgeschaltete Quelle ruft nie ab, „Schlüssel prüfen“ = ein Abruf.
/// </summary>
public sealed class MapStyleTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pv3-mapstyles-" + Guid.NewGuid().ToString("N"));

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

    // ---- Stilwahl ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Automatisch_folgt_hell_und_dunkel()
    {
        var keys = new MapApiKeys("carto-key", null);
        MapStyleCatalog.Resolve(MapStyle.Automatic, isDark: false, keys).Definition.Should().Be(MapStyleCatalog.Positron);
        MapStyleCatalog.Resolve(MapStyle.Automatic, isDark: true, keys).Definition.Should().Be(MapStyleCatalog.DarkMatter);
        MapStyleCatalog.Resolve(MapStyle.Automatic, isDark: true, keys).ApiKey.Should().Be("carto-key");
    }

    [Fact]
    public void Ohne_Schluessel_Rueckfall_auf_OSM_mit_Hinweis()
    {
        var automatic = MapStyleCatalog.Resolve(MapStyle.Automatic, isDark: false, MapApiKeys.None);
        automatic.Definition.Should().Be(MapStyleCatalog.OpenStreetMap);
        automatic.Notice.Should().BeNull("Automatisch ohne Schlüssel = bisheriges Verhalten ohne Dauerhinweis");

        foreach (var style in new[] { MapStyle.Voyager, MapStyle.Positron, MapStyle.DarkMatter, MapStyle.Satellite })
        {
            var resolved = MapStyleCatalog.Resolve(style, isDark: false, MapApiKeys.None);
            resolved.Definition.Should().Be(MapStyleCatalog.OpenStreetMap, $"{style} ohne Schlüssel");
            resolved.ApiKey.Should().BeNull();
            resolved.Notice.Should().Be(MapStyleCatalog.KeyMissingNotice);
        }

        // CARTO-Schlüssel hilft dem Satelliten nicht (MapTiler)
        MapStyleCatalog.Resolve(MapStyle.Satellite, false, new MapApiKeys("carto", null)).Definition.Should().Be(MapStyleCatalog.OpenStreetMap);
        MapStyleCatalog.Resolve(MapStyle.Satellite, false, new MapApiKeys(null, "mt")).Definition.Should().Be(MapStyleCatalog.Satellite);
        MapStyleCatalog.Resolve(MapStyle.OpenStreetMap, true, new MapApiKeys("c", "m")).Definition.Should().Be(MapStyleCatalog.OpenStreetMap);
    }

    [Fact]
    public void Url_mit_und_ohne_Schluessel()
    {
        var tile = new MapTileId(3, 4, 2);
        MapStyleCatalog.Positron.BuildUrl(tile, "abc").Should().Be("https://basemaps.cartocdn.com/rastertiles/light_all/3/4/2.png?key=abc");
        MapStyleCatalog.Voyager.BuildUrl(tile, "abc").Should().Be("https://basemaps.cartocdn.com/rastertiles/voyager/3/4/2.png?key=abc");
        MapStyleCatalog.DarkMatter.BuildUrl(tile, "a b&c").Should().Be("https://basemaps.cartocdn.com/rastertiles/dark_all/3/4/2.png?key=a%20b%26c");
        MapStyleCatalog.Satellite.BuildUrl(tile, "mt").Should().Be("https://api.maptiler.com/maps/satellite/256/3/4/2.jpg?key=mt");
        MapStyleCatalog.OpenStreetMap.BuildUrl(tile, null).Should().Be("https://tile.openstreetmap.org/3/4/2.png");

        MapStyleCatalog.Positron.BuildUrl(tile, null).Should().BeNull("nie unbeschlüsselt bei CARTO abrufen");
        MapStyleCatalog.Positron.BuildUrl(tile, "   ").Should().BeNull();
        MapStyleCatalog.Satellite.BuildUrl(tile, null).Should().BeNull();
    }

    [Fact]
    public void Namensnennung_je_Stil()
    {
        MapStyleCatalog.OpenStreetMap.Attribution.Should().Be("© OpenStreetMap-Mitwirkende");
        foreach (var carto in new[] { MapStyleCatalog.Voyager, MapStyleCatalog.Positron, MapStyleCatalog.DarkMatter })
        {
            carto.Attribution.Should().Be("© OpenStreetMap-Mitwirkende, © CARTO");
            carto.AttributionLink.Should().Contain("carto.com");
        }

        MapStyleCatalog.Satellite.Attribution.Should().StartWith("© MapTiler © OpenStreetMap-Mitwirkende").And.Contain("Satellitenbilder");
        MapStyleCatalog.Satellite.AttributionLink.Should().Contain("maptiler.com/copyright");
        new DisabledMapTileSource(MapStyleCatalog.Voyager).Attribution.Should().Be(MapStyleCatalog.CartoAttribution);
        MapStyleCatalog.Parse("DarkMatter").Should().Be(MapStyle.DarkMatter);
        MapStyleCatalog.Parse("Unsinn").Should().Be(MapStyle.Automatic);
        MapStyleCatalog.Parse(null).Should().Be(MapStyle.Automatic);
    }

    // ---- Netz-Quelle je Stil -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Carto_ohne_Schluessel_ruft_nie_ab()
    {
        var handler = new FakeHandler();
        using var source = new HttpMapTileSource(MapStyleCatalog.Positron, null, new MapTileOptions(), Cache(MapStyleCatalog.Positron), "Lumora/test (private use)", handler, () => Now);

        (await source.GetTileAsync(new MapTileId(2, 1, 1), CancellationToken.None)).Should().BeNull();
        handler.Requests.Should().BeEmpty("ohne Schlüssel kein CARTO-Abruf (Wasserzeichen)");
        source.State.Should().Be(MapTileSourceState.Disabled);
    }

    [Fact]
    public async Task Carto_mit_Schluessel_eigener_Cache_Ordner_und_Namensnennung()
    {
        var handler = new FakeHandler();
        var counted = 0;
        using var source = new HttpMapTileSource(MapStyleCatalog.Voyager, "geheim", new MapTileOptions(), Cache(MapStyleCatalog.Voyager), "Lumora/test (private use)", handler, () => Now, onDownload: () => counted++);

        (await source.GetTileAsync(new MapTileId(3, 4, 2), CancellationToken.None)).Should().Equal(FakeHandler.Png);
        (await source.GetTileAsync(new MapTileId(3, 4, 2), CancellationToken.None)).Should().Equal(FakeHandler.Png);
        counted.Should().Be(1, "Lumoras Zählung: nur echte Abrufe, nicht der Cache");
        handler.Requests.Should().ContainSingle().Which.RequestUri!.ToString()
            .Should().Be("https://basemaps.cartocdn.com/rastertiles/voyager/3/4/2.png?key=geheim");
        File.Exists(Path.Combine(_dir, "maptiles", "carto-voyager", "3", "4", "2.png")).Should().BeTrue("Cache je Stil getrennt");
        source.Attribution.Should().Be(MapStyleCatalog.CartoAttribution);
    }

    [Fact]
    public async Task Carto_Cache_hoechstens_30_Tage()
    {
        var now = Now;
        var handler = new FakeHandler { MaxAge = TimeSpan.FromDays(60) };
        var cache = new MapTileDiskCache(_dir, 10_000_000, trimInBackground: false, styleFolder: MapStyleCatalog.Positron.Id, maxAge: MapStyleCatalog.CartoMaxCacheAge);
        using var source = new HttpMapTileSource(MapStyleCatalog.Positron, "k", new MapTileOptions(), cache, "Lumora/test (private use)", handler, () => now);
        var tile = new MapTileId(4, 3, 5);
        var path = Path.Combine(_dir, "maptiles", "carto-positron", "4", "3", "5.png");

        await source.GetTileAsync(tile, CancellationToken.None);
        File.GetLastWriteTimeUtc(path).Should().BeOnOrBefore(Now.AddDays(30), "Ablaufzeit trotz max-age 60 Tage höchstens 30 Tage");

        now = Now.AddDays(29);
        (await source.GetTileAsync(tile, CancellationToken.None)).Should().NotBeNull();
        handler.Requests.Should().HaveCount(1, "nach 29 Tagen aus dem Cache");

        now = Now.AddDays(31);
        cache.TryRead(tile, now).Should().BeNull("älter als 30 Tage: verworfen");
        File.Exists(path).Should().BeFalse("… und gelöscht");
        (await source.GetTileAsync(tile, CancellationToken.None)).Should().NotBeNull();
        handler.Requests.Should().HaveCount(2, "neu geholt statt die alte Kachel zu zeigen");
    }

    [Fact]
    public void Gealterte_Kacheln_aller_Stile_werden_geloescht_OSM_bleibt()
    {
        var carto = new MapTileDiskCache(_dir, 10_000_000, trimInBackground: false, styleFolder: MapStyleCatalog.DarkMatter.Id, maxAge: MapStyleCatalog.CartoMaxCacheAge);
        var osm = new MapTileDiskCache(_dir, 10_000_000, trimInBackground: false, styleFolder: MapStyleCatalog.OpenStreetMap.Id);
        carto.Write(new MapTileId(1, 0, 0), [1, 2, 3], Now.AddDays(7), Now.AddDays(-40));
        carto.Write(new MapTileId(1, 1, 0), [1, 2, 3], Now.AddDays(7), Now.AddDays(-2));
        osm.Write(new MapTileId(1, 0, 0), [1, 2, 3], Now.AddDays(7), Now.AddDays(-400));

        MapTileDiskCache.SweepExpired(_dir, MapStyleCatalog.All, Now).Should().Be(1);
        File.Exists(Path.Combine(_dir, "maptiles", "carto-darkmatter", "1", "0", "0.png")).Should().BeFalse();
        File.Exists(Path.Combine(_dir, "maptiles", "carto-darkmatter", "1", "1", "0.png")).Should().BeTrue();
        File.Exists(Path.Combine(_dir, "maptiles", "osm", "1", "0", "0.png")).Should().BeTrue("OSM: nur HTTP-Ablaufzeit, keine Alterungsgrenze");
    }

    [Fact]
    public void Alte_OSM_Kacheln_werden_in_den_Stil_Ordner_uebernommen()
    {
        var legacy = new MapTileDiskCache(_dir, 10_000_000, trimInBackground: false); // Aufbau bis 0.4.0: maptiles\z\x\y.png
        legacy.Write(new MapTileId(3, 4, 2), [7, 7, 7], Now.AddDays(7), Now);

        MapTileDiskCache.MigrateLegacyLayout(_dir, MapStyleCatalog.OpenStreetMap.Id).Should().Be(1);
        File.Exists(Path.Combine(_dir, "maptiles", "osm", "3", "4", "2.png")).Should().BeTrue("keine erneuten OSM-Abrufe nach dem Update");
        Directory.Exists(Path.Combine(_dir, "maptiles", "3")).Should().BeFalse();
        var osm = new MapTileDiskCache(_dir, 10_000_000, trimInBackground: false, styleFolder: MapStyleCatalog.OpenStreetMap.Id);
        osm.TryRead(new MapTileId(3, 4, 2), Now)!.Value.Bytes.Should().Equal(7, 7, 7);
        MapTileDiskCache.MigrateLegacyLayout(_dir, MapStyleCatalog.OpenStreetMap.Id).Should().Be(0, "nur einmal");
    }

    [Fact]
    public async Task Abgelehnter_Schluessel_pausiert_und_meldet_den_Zustand()
    {
        var handler = new FakeHandler { Status = HttpStatusCode.Unauthorized };
        using var source = new HttpMapTileSource(MapStyleCatalog.Positron, "falsch", new MapTileOptions(), Cache(MapStyleCatalog.Positron), "Lumora/test (private use)", handler, () => Now);

        (await source.GetTileAsync(new MapTileId(1, 0, 0), CancellationToken.None)).Should().BeNull();
        source.State.Should().Be(MapTileSourceState.KeyRejected);
        await source.GetTileAsync(new MapTileId(1, 1, 0), CancellationToken.None);
        handler.Requests.Should().HaveCount(1, "während der Pause keine weiteren Abrufe");
    }

    // ---- Umschaltbare Quelle -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Automatisch_wechselt_die_Quelle_live_mit_dem_Farbschema()
    {
        var styles = new FakeStyles { Keys = new MapApiKeys("k", null) };
        var theme = new FakeTheme();
        var created = new List<MapStyleResolution>();
        var source = new StyledMapTileSource(styles, theme, new MapTileOptions(), r =>
        {
            created.Add(r);
            return new DisabledMapTileSource(r.Definition);
        });
        var changes = 0;
        source.SourceChanged += (_, _) => changes++;

        source.Resolution.Definition.Should().Be(MapStyleCatalog.Positron);
        theme.SetDark(true);
        source.Resolution.Definition.Should().Be(MapStyleCatalog.DarkMatter);
        source.Attribution.Should().Be(MapStyleCatalog.CartoAttribution);
        changes.Should().Be(1);
        created.Select(r => r.Definition.Id).Should().Equal("carto-positron", "carto-darkmatter");

        theme.SetDark(true); // gleiches Schema: kein Wechsel
        changes.Should().Be(1);

        await styles.SetStyleAsync(MapStyle.OpenStreetMap);
        source.Resolution.Definition.Should().Be(MapStyleCatalog.OpenStreetMap);
        source.Attribution.Should().Be(OsmTileSource.OsmAttribution);
        changes.Should().Be(2);
    }

    [Fact]
    public async Task Ohne_Schluessel_wird_keine_CARTO_Quelle_erzeugt()
    {
        var styles = new FakeStyles { Style = MapStyle.Voyager };
        var created = new List<MapStyleResolution>();
        var source = new StyledMapTileSource(styles, new FakeTheme(), new MapTileOptions(), r =>
        {
            created.Add(r);
            return new DisabledMapTileSource(r.Definition);
        });

        created.Should().ContainSingle().Which.Definition.Should().Be(MapStyleCatalog.OpenStreetMap);
        source.Resolution.Notice.Should().Be(MapStyleCatalog.KeyMissingNotice);

        await styles.SetKeysAsync(new MapApiKeys("neu", null));
        source.Resolution.Definition.Should().Be(MapStyleCatalog.Voyager);
        created.Last().ApiKey.Should().Be("neu");
        source.Resolution.Notice.Should().BeNull();
    }

    [Fact]
    public async Task Abgeschaltet_wird_der_Stil_aufgeloest_aber_nie_abgerufen()
    {
        var styles = new FakeStyles { Style = MapStyle.Positron, Keys = new MapApiKeys("k", null) };
        var factoryCalls = 0;
        var source = new StyledMapTileSource(styles, new FakeTheme(), new MapTileOptions { Enabled = false }, _ =>
        {
            factoryCalls++;
            throw new InvalidOperationException("darf nicht erzeugt werden");
        });

        source.State.Should().Be(MapTileSourceState.Disabled);
        source.Attribution.Should().Be(MapStyleCatalog.CartoAttribution, "Namensnennung des gewählten Stils (Rauchtest)");
        await styles.SetStyleAsync(MapStyle.Satellite);
        source.Resolution.Definition.Should().Be(MapStyleCatalog.OpenStreetMap, "Satellit ohne MapTiler-Schlüssel");
        (await source.GetTileAsync(new MapTileId(0, 0, 0), CancellationToken.None)).Should().BeNull();
        factoryCalls.Should().Be(0);
    }

    [Fact]
    public async Task Provider_laedt_nach_Stilwechsel_die_sichtbaren_Kacheln_neu()
    {
        var styles = new FakeStyles { Keys = new MapApiKeys("k", null) };
        var theme = new FakeTheme();
        var sources = new List<RecordingTileSource>();
        var source = new StyledMapTileSource(styles, theme, new MapTileOptions(), r =>
        {
            var s = new RecordingTileSource(r.Definition);
            sources.Add(s);
            return s;
        });
        var provider = new MapTileProvider(source, new MapTileOptions());
        var visible = new[] { new MapTileId(2, 1, 1), new MapTileId(2, 2, 1) };

        provider.SetWanted(visible);
        await WaitUntilAsync(() => sources[0].Requested.Count == 2);
        theme.SetDark(true);
        await WaitUntilAsync(() => sources.Count == 2 && sources[1].Requested.Count == 2);

        sources[1].Style.Should().Be(MapStyleCatalog.DarkMatter);
        sources[1].Requested.Should().BeEquivalentTo(visible, "derselbe Ausschnitt mit dem neuen Stil");
        provider.Attribution.Should().Be(MapStyleCatalog.CartoAttribution);
    }

    // ---- Schlüssel prüfen ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Schluessel_pruefen_ist_genau_ein_Abruf()
    {
        var handler = new FakeHandler();
        var validator = new MapKeyValidator(new MapTileOptions(), "Lumora/test (private use)", handler);

        var ok = await validator.CheckAsync(MapTileVendor.Carto, "abc");
        ok.IsValid.Should().BeTrue();
        handler.Requests.Should().ContainSingle().Which.RequestUri!.ToString().Should().Be("https://basemaps.cartocdn.com/rastertiles/light_all/1/1/0.png?key=abc");

        handler.Status = HttpStatusCode.Forbidden;
        (await validator.CheckAsync(MapTileVendor.MapTiler, "mt")).IsValid.Should().BeFalse();
        handler.Requests.Last().RequestUri!.Host.Should().Be("api.maptiler.com");

        (await validator.CheckAsync(MapTileVendor.Carto, " ")).IsValid.Should().BeFalse();
        handler.Requests.Should().HaveCount(2, "ohne Schlüssel kein Abruf");
    }

    [Fact]
    public async Task Schluessel_pruefen_bei_abgeschalteter_Quelle_ohne_Netz()
    {
        var handler = new FakeHandler();
        var validator = new MapKeyValidator(new MapTileOptions { Enabled = false }, "Lumora/test (private use)", handler);
        var result = await validator.CheckAsync(MapTileVendor.Carto, "abc");
        result.IsValid.Should().BeNull();
        handler.Requests.Should().BeEmpty("Tests und Rauchtest rufen nie ab");
    }

    // ---- Hilfen --------------------------------------------------------------------------------------------------------

    private MapTileDiskCache Cache(MapTileStyleDefinition style)
        => new(_dir, 10_000_000, trimInBackground: false, styleFolder: style.Id, maxAge: style.MaxCacheAge);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
            {
                throw new TimeoutException("Bedingung nicht erfüllt");
            }

            await Task.Delay(20);
        }
    }

    internal sealed class FakeStyles : IMapStyleService
    {
        public MapStyle Style { get; set; } = MapStyle.Automatic;

        public MapApiKeys Keys { get; set; } = MapApiKeys.None;

        public event EventHandler? Changed;

        public Task EnsureLoadedAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task SetStyleAsync(MapStyle style, CancellationToken ct = default)
        {
            Style = style;
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public Task SetKeysAsync(MapApiKeys keys, CancellationToken ct = default)
        {
            Keys = keys;
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
    }

    internal sealed class FakeTheme : IMapThemeSource
    {
        public bool IsDark { get; private set; }

        public event EventHandler? Changed;

        public void SetDark(bool dark)
        {
            IsDark = dark;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class RecordingTileSource(MapTileStyleDefinition style) : IMapTileSource
    {
        public MapTileStyleDefinition Style { get; } = style;

        public System.Collections.Concurrent.ConcurrentBag<MapTileId> Requested { get; } = [];

        public MapTileSourceState State => MapTileSourceState.Online;

        public string Attribution => Style.Attribution;

        public int MaxZoom => Style.MaxZoom;

        public event EventHandler? StateChanged
        {
            add { }
            remove { }
        }

        public Task<byte[]?> GetTileAsync(MapTileId id, CancellationToken ct)
        {
            Requested.Add(id);
            return Task.FromResult<byte[]?>(null);
        }
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];

        public List<HttpRequestMessage> Requests { get; } = [];

        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        public TimeSpan MaxAge { get; set; } = TimeSpan.FromDays(1);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(request);
            }

            var response = new HttpResponseMessage(Status) { Content = new ByteArrayContent(Png) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = MaxAge };
            return Task.FromResult(response);
        }
    }
}
