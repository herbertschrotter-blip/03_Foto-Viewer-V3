using Microsoft.Data.Sqlite;
using PhotoViewerV3.Places.Map.Tiles;
using PhotoViewerV3.Places.Styles;
using PhotoViewerV3.Wpf.Tests.ArchiveTarget;
using PhotoViewerV3.Wpf.Tests.Fixtures;

namespace PhotoViewerV3.Wpf.Tests.Places;

/// <summary>
/// Einstellungen › Darstellung › Karte (ADR-0055): Stil, Schlüssel und Kartendienst-Angaben liegen in der Settings-Tabelle
/// des Katalogs (echte, migrierte DB); die Karten „Kartendienst“ sind ohne Schlüssel vorgeschlagen, eine erfolgreiche
/// Prüfung (Fake) richtet den Dienst ein und speichert; Links nur http/https; Lumoras Zählung je Kalendermonat (UTC).
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class MapStyleSettingsTests : IAsyncLifetime
{
    private ArchiveTestDb _db = null!;

    public async Task InitializeAsync() => _db = await ArchiveTestDb.CreateAsync();

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        _db.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Stil_und_Schluessel_werden_im_Katalog_gespeichert()
    {
        var service = new MapStyleService(_db.Factory);
        await service.EnsureLoadedAsync();
        service.Style.Should().Be(MapStyle.Automatic, "Standard");
        service.Keys.Should().Be(MapApiKeys.None);
        var changes = 0;
        service.Changed += (_, _) => changes++;

        await service.SetStyleAsync(MapStyle.DarkMatter);
        await service.SetKeysAsync(new MapApiKeys("  carto-123  ", ""));
        changes.Should().Be(2);

        var reopened = new MapStyleService(_db.Factory);
        await reopened.EnsureLoadedAsync();
        reopened.Style.Should().Be(MapStyle.DarkMatter);
        reopened.Keys.Should().Be(new MapApiKeys("carto-123", null), "getrimmt, leer = kein Schlüssel");
    }

    [Fact]
    public async Task Kartendienst_Angaben_speichern_und_laden_mit_Vorbelegung()
    {
        var store = new MapServiceAccountStore(_db.Factory);
        var fresh = await store.LoadAsync(MapTileVendor.Carto);
        fresh.Should().Be(MapServiceAccount.Default(MapTileVendor.Carto));
        fresh.Website.Should().Be("https://carto.com/basemaps");
        fresh.AccountUrl.Should().Be("https://dashboard.basemaps.carto.com");
        (await store.LoadAsync(MapTileVendor.MapTiler)).AccountUrl.Should().Be("https://cloud.maptiler.com/account/analytics");

        var account = new MapServiceAccount(MapTileVendor.MapTiler, "https://www.maptiler.com", "herbert@example.org", null, "Gratis-Plan", MapServiceAccount.HashKey("mt"));
        await store.SaveAsync(account);

        var loaded = await new MapServiceAccountStore(_db.Factory).LoadAsync(MapTileVendor.MapTiler);
        loaded.Should().Be(account, "leer gespeicherter Konto-Link bleibt leer (keine Vorbelegung mehr)");
        loaded.IsVerifiedFor("mt").Should().BeTrue();
        loaded.IsVerifiedFor("anderer").Should().BeFalse();
        (await store.LoadAsync(MapTileVendor.Carto)).Should().Be(fresh, "CARTO unberührt");
    }

    [Fact]
    public void Links_nur_http_und_https()
    {
        MapServiceLinks.TryParse("https://carto.com/basemaps", out var uri).Should().BeTrue();
        uri!.Host.Should().Be("carto.com");
        MapServiceLinks.TryParse("http://example.org/x", out _).Should().BeTrue();
        MapServiceLinks.TryParse(" https://cloud.maptiler.com/account/keys/ ", out _).Should().BeTrue("Leerzeichen werden entfernt");

        foreach (var bad in new[] { "file:///C:/Windows/notepad.exe", "javascript:alert(1)", "ftp://x.org", "C:\\Programme\\x.exe", "www.carto.com", "mailto:a@b.c", "", null })
        {
            MapServiceLinks.TryParse(bad, out _).Should().BeFalse($"„{bad}“ ist kein http/https-Link");
        }

        foreach (var info in MapServiceInfo.All)
        {
            MapServiceLinks.TryParse(info.RequestKeyUrl, out _).Should().BeTrue();
            MapServiceLinks.TryParse(info.DefaultWebsite, out _).Should().BeTrue();
            MapServiceLinks.TryParse(info.DefaultAccountUrl, out _).Should().BeTrue();
            info.Steps.Should().HaveCount(3);
        }
    }

    [Fact]
    public async Task Zaehlung_je_Kalendermonat_UTC()
    {
        var now = new DateTime(2026, 10, 31, 23, 50, 0, DateTimeKind.Utc);
        var counter = new MapTileUsageCounter(_db.Factory, () => now);
        await counter.EnsureLoadedAsync();
        for (var i = 0; i < 3; i++)
        {
            counter.Count(MapTileVendor.Carto);
        }

        counter.Count(MapTileVendor.MapTiler);
        counter.Count(MapTileVendor.OpenStreetMap); // OSM zählt nicht
        await counter.FlushAsync();
        counter.Get(MapTileVendor.Carto).Should().Be(new MapTileUsage("2026-10", 3, now));

        var reopened = new MapTileUsageCounter(_db.Factory, () => now);
        await reopened.EnsureLoadedAsync();
        reopened.Get(MapTileVendor.Carto).Count.Should().Be(3, "gespeichert");
        reopened.Get(MapTileVendor.MapTiler).Count.Should().Be(1);

        now = new DateTime(2026, 11, 1, 0, 5, 0, DateTimeKind.Utc); // neuer Monat (UTC)
        reopened.Get(MapTileVendor.Carto).Should().Be(new MapTileUsage("2026-11", 0, null), "neuer Monat beginnt bei 0");
        reopened.Count(MapTileVendor.Carto);
        reopened.Get(MapTileVendor.Carto).Count.Should().Be(1);
        MapTileUsageCounter.Parse("2026-10;42;2026-10-21T10:00:00.0000000Z")!.Count.Should().Be(42);
        MapTileUsageCounter.Parse("kaputt").Should().BeNull();
    }

    [Fact]
    public Task Kartendienste_vorgeschlagen_pruefen_richtet_ein_und_speichert() => TestUiThread.Dispatcher.InvokeAsync(async () =>
    {
        var styles = new MapStyleTests.FakeStyles();
        var validator = new RecordingValidator();
        var links = new RecordingLinks();
        var store = new MapServiceAccountStore(_db.Factory);
        var usage = new MapTileUsageCounter(_db.Factory);
        var vm = new MapStyleSettingsViewModel(styles, validator, store, usage, links);
        await vm.LoadAsync();

        // Ohne Schlüssel vorgeschlagen: Status, Anforderung, Anleitung
        vm.Carto.Status.Should().Be(MapServiceStatus.NotSetUp);
        vm.Carto.StatusText.Should().Be("Kein Schlüssel – noch nicht eingerichtet");
        vm.Carto.Steps.Should().HaveCount(3);
        vm.Carto.UsageText.Should().Contain("Diesen Monat von Lumora abgerufen: 0 Kacheln").And.Contain("Gratis bis 5 Mio./Monat");
        vm.Carto.RequestKeyCommand.Execute(null);
        links.Opened.Should().ContainSingle().Which.AbsoluteUri.Should().Be("https://carto.com/basemaps/apikey/");
        vm.MapTiler.RequestKeyCommand.Execute(null);
        links.Opened.Last().AbsoluteUri.Should().Be("https://cloud.maptiler.com/account/keys/");

        // Stil ohne Schlüssel: Hinweis
        vm.SelectedStyle = vm.StyleOptions.Single(o => o.Style == MapStyle.Voyager);
        await Task.Yield();
        styles.Style.Should().Be(MapStyle.Voyager, "Stil wirkt sofort");
        vm.StyleHint.Should().Contain(MapStyleCatalog.KeyMissingNotice);

        // Schlüssel + Angaben eingeben, ungültiger Link sperrt das Speichern
        vm.Carto.Key = "abc";
        vm.Carto.Status.Should().Be(MapServiceStatus.NotVerified);
        vm.Carto.Email = "herbert@example.org";
        vm.Carto.AccountUrl = "javascript:alert(1)";
        vm.Carto.AccountUrlError.Should().NotBeNull();
        vm.HasUnsavedChanges.Should().BeFalse("ungültiger Link");
        vm.Carto.OpenAccountCommand.CanExecute(null).Should().BeFalse();
        vm.Carto.AccountUrl = "https://dashboard.basemaps.carto.com";
        vm.HasUnsavedChanges.Should().BeTrue();

        // Prüfen (Fake: gültig) → eingerichtet und gespeichert, Stile freigeschaltet
        await vm.Carto.CheckKeyCommand.ExecuteAsync(null);
        validator.Calls.Should().ContainSingle().Which.Should().Be((MapTileVendor.Carto, "abc"));
        vm.Carto.Status.Should().Be(MapServiceStatus.Ready);
        vm.Carto.StatusText.Should().Be("Eingerichtet");
        styles.Keys.Carto.Should().Be("abc");
        vm.HasUnsavedChanges.Should().BeFalse();
        vm.StyleHint.Should().BeNull("Schlüssel vorhanden — Voyager freigeschaltet");
        var saved = await store.LoadAsync(MapTileVendor.Carto);
        saved.Email.Should().Be("herbert@example.org");
        saved.IsVerifiedFor("abc").Should().BeTrue();
        vm.Carto.OpenAccountCommand.Execute(null);
        links.Opened.Last().AbsoluteUri.Should().Be("https://dashboard.basemaps.carto.com/");

        // Abgelehnt → Status „abgelehnt“, nichts gespeichert
        validator.Next = new MapKeyCheckResult(false, "abgelehnt");
        vm.MapTiler.Key = "falsch";
        await vm.MapTiler.CheckKeyCommand.ExecuteAsync(null);
        vm.MapTiler.Status.Should().Be(MapServiceStatus.Rejected);
        styles.Keys.MapTiler.Should().BeNull();

        // Neu laden: Eingerichtet bleibt (Hash des geprüften Schlüssels), Schlüssel kommt aus dem Stil-Dienst
        var again = new MapStyleSettingsViewModel(styles, validator, store, usage, links);
        await again.LoadAsync();
        again.Carto.Status.Should().Be(MapServiceStatus.Ready);
        again.Carto.Email.Should().Be("herbert@example.org");
    }).Task.Unwrap().WaitAsync(TimeSpan.FromSeconds(30));

    private sealed class RecordingValidator : IMapKeyValidator
    {
        public List<(MapTileVendor, string?)> Calls { get; } = [];

        public MapKeyCheckResult Next { get; set; } = new(true, "ok");

        public Task<MapKeyCheckResult> CheckAsync(MapTileVendor vendor, string? apiKey, CancellationToken ct = default)
        {
            Calls.Add((vendor, apiKey));
            return Task.FromResult(Next);
        }
    }

    private sealed class RecordingLinks : IMapLinkOpener
    {
        public List<Uri> Opened { get; } = [];

        public bool Open(Uri uri)
        {
            Opened.Add(uri);
            return true;
        }
    }
}
