using CommunityToolkit.Mvvm.Messaging;
using Lumora.Places.Map.Tiles;
using Lumora.Places.Dialogs;
using Lumora.Services.Messaging;
using Lumora.Services.Places;
using Lumora.Services.Places.Clustering;
using Lumora.Shell;

namespace Lumora.Wpf.Tests.Places;

/// <summary>
/// Seite „Orte“ mit Fake-Daten und abgeschalteter Kachelquelle (ADR-0051): Laden, Start-Ausschnitt, Bibliotheks-Trennung,
/// Hineinzoomen bis zum Zerfall, „In der Galerie zeigen“, Lightbox, Verorten mit den richtigen Ids, Ortssuche.
/// </summary>
public sealed class PlacesViewModelTests
{
    private static async Task<(PlacesHarness H, Lumora.Places.ViewModels.PlacesViewModel Vm)> LoadedAsync(Action<PlacesHarness>? seed = null)
    {
        var h = new PlacesHarness();
        (seed ?? (x => x.SeedAustria()))(h);
        var vm = h.Create();
        await vm.ActivateAsync();
        return (h, vm);
    }

    [Fact]
    public async Task Laedt_Bereich_mit_Trefferzeile_Gruppen_und_Start_Ausschnitt()
    {
        var (h, vm) = await LoadedAsync();

        vm.HasLibrary.Should().BeTrue();
        vm.SummaryText.Should().Be("5 verortete Fotos · 3 ohne Ort");
        vm.LocatedCount.Should().Be(5);
        vm.Unlocated.Select(t => t.FileId).Should().BeEquivalentTo([6, 7, 8]);
        vm.Markers.Sum(m => m.Count).Should().Be(5, "alle verorteten Fotos im Start-Ausschnitt");
        vm.Markers.Should().Contain(m => m.IsGroup);

        // Start-Ausschnitt = Ausdehnung Graz–Wien plus Rand (nicht die Welt)
        var visible = vm.Viewport.VisibleBounds;
        visible.North.Should().BeGreaterThan(48.209).And.BeLessThan(50);
        visible.South.Should().BeLessThan(47.0707).And.BeGreaterThan(45);
        vm.HasNoLocatedPhotos.Should().BeFalse();
        h.World.PointQueries.Should().OnlyContain(ids => ids.SequenceEqual(new[] { 1 }), "nur die Bibliothek des Bereichs");
        vm.Markers.Should().OnlyContain(m => m.BestFileId <= 5, "nichts aus Bibliothek 2");
    }

    [Fact]
    public async Task Ohne_verortete_Fotos_Weltansicht_und_Leerzustand()
    {
        var (_, vm) = await LoadedAsync(h => { h.World.Add(1, 1); h.World.Add(2, 1); });
        vm.LocatedCount.Should().Be(0);
        vm.HasNoLocatedPhotos.Should().BeTrue();
        vm.Viewport.Zoom.Should().Be(vm.Viewport.MinZoomLevel, "ganze Welt");
        vm.SummaryText.Should().Be("0 verortete Fotos · 2 ohne Ort");
    }

    [Fact]
    public async Task Bibliothekswechsel_laedt_neu_und_zeigt_nur_die_neue_Bibliothek()
    {
        var (h, vm) = await LoadedAsync();
        h.World.ScopeLibraries.Clear();
        h.World.ScopeLibraries.Add(2);
        h.Messenger.Send(new ActiveLibraryChangedMessage(2));
        await WaitUntilAsync(() => vm.LocatedCount == 2 && !vm.IsLoading); // Empfänger läuft ggf. über den UI-Thread (UiThread.Post)
        await vm.LastLoad;

        vm.LocatedCount.Should().Be(2);
        vm.Unlocated.Select(t => t.FileId).Should().Equal(103);
        vm.Markers.SelectMany(m => vm.Index.GetFileIds(m.Cluster)).Should().BeEquivalentTo([101, 102]);
        vm.Viewport.VisibleBounds.West.Should().BeLessThan(2.3522).And.BeGreaterThan(-5, "neuer Start-Ausschnitt um Paris");
    }

    [Fact]
    public async Task Klick_auf_Gruppe_zoomt_hinein_bis_sie_zerfaellt()
    {
        var (_, vm) = await LoadedAsync();
        vm.Viewport.ShowWorld();
        var group = vm.Markers.Should().ContainSingle("weit draußen eine Gruppe").Subject;
        group.Count.Should().Be(5);
        group.BestFileId.Should().Be(2, "höchste Bewertung");

        for (var i = 0; i < 25 && vm.Markers.Any(m => m.IsGroup); i++)
        {
            var before = vm.Viewport.Zoom;
            var target = vm.Markers.First(m => m.IsGroup);
            if (vm.Index.GetExpansionZoom(target.Cluster) == int.MaxValue)
            {
                break;
            }

            await vm.ActivateMarkerAsync(target);
            vm.Viewport.Zoom.Should().BeGreaterThan(before);
            vm.Markers.Sum(m => m.Count).Should().BeGreaterThan(0);
        }

        vm.Markers.Should().Contain(m => !m.IsGroup, "zerfällt bis zu einzelnen Fotos");
    }

    [Fact]
    public async Task Einzelfoto_oeffnet_die_Lightbox_und_Gruppe_am_selben_Ort_ebenso()
    {
        var (h, vm) = await LoadedAsync(x =>
        {
            x.World.Add(1, 1, 48.2, 16.37);
            x.World.Add(2, 1, 48.2, 16.37);
            x.World.Add(3, 1, 10, 10);
        });
        vm.Viewport.CenterOn(10, 10, 12);
        var single = vm.Markers.Single(m => !m.IsGroup);
        await vm.ActivateMarkerAsync(single);
        h.Lightbox.Opened.Should().ContainSingle();
        var (entries, start) = h.Lightbox.Opened[0];
        entries[start].FileId.Should().Be(3);

        vm.Viewport.CenterOn(48.2, 16.37, 19);
        var stack = vm.Markers.Single(m => m.IsGroup);
        vm.Index.GetExpansionZoom(stack.Cluster).Should().Be(int.MaxValue);
        await vm.ActivateMarkerAsync(stack);
        h.Lightbox.Opened.Should().HaveCount(2);
        h.Lightbox.Opened[1].Entries.Select(e => e.FileId).Should().BeEquivalentTo([1, 2], "Gruppe am selben Ort öffnet ihre Fotos");
    }

    [Fact]
    public async Task In_der_Galerie_zeigen_sendet_genau_die_Dateien_und_wechselt_zur_Galerie()
    {
        var (h, vm) = await LoadedAsync();
        ShowFilesOnMapSelectionMessage? received = null;
        h.Messenger.Register<ShowFilesOnMapSelectionMessage>(this, (_, m) => received = m);
        vm.Viewport.ShowWorld();
        var group = vm.Markers.Single();

        vm.ShowInGallery(group);

        received.Should().NotBeNull();
        received!.FileIds.Should().BeEquivalentTo([1, 2, 3, 4, 5]);
        received.Bounds.South.Should().BeApproximately(47.0707, 1e-4);
        received.Bounds.North.Should().BeApproximately(48.2090, 1e-4);
        h.Navigation.Tags.Should().Equal(NavigationTags.Gallery);

        received = null;
        vm.ShowVisibleInGalleryCommand.Execute(null);
        received!.FileIds.Should().HaveCount(5);
    }

    [Fact]
    public async Task Verorten_ruft_den_Dienst_mit_den_richtigen_Ids_und_laedt_neu()
    {
        var (h, vm) = await LoadedAsync();
        var tiles = vm.Unlocated.Where(t => t.FileId is 6 or 8).ToList();
        vm.SelectTile(tiles[0], toggle: false, range: false);
        vm.SelectTile(tiles[1], toggle: true, range: false);
        vm.SelectedCount.Should().Be(2);
        vm.DragFileIds(tiles[0]).Should().BeEquivalentTo([6, 8], "Ziehen nimmt die Auswahl mit");

        await vm.AssignSelectedAtAsync(48.21, 16.37);

        h.Dialogs.Requests.Should().ContainSingle().Which.Should().Be(new AssignLocationRequest(2, 48.21, 16.37, "Wien, Österreich"));
        h.World.SetCalls.Should().ContainSingle();
        var call = h.World.SetCalls[0];
        call.Ids.Should().BeEquivalentTo([6, 8]);
        (call.Lat, call.Lon, call.Keep).Should().Be((48.21, 16.37, true));
        vm.Unlocated.Select(t => t.FileId).Should().Equal(7);
        vm.LocatedCount.Should().Be(7);
        vm.Message.Should().Be("2 Fotos verortet (Wien).");
    }

    [Fact]
    public async Task Verorten_abgebrochen_oder_fremde_Ids_schreibt_nichts()
    {
        var (h, vm) = await LoadedAsync();
        h.Dialogs.Next = null;
        await vm.AssignAsync([6], 47.07, 15.44);
        h.Dialogs.Requests.Should().ContainSingle().Which.PlaceName.Should().Be("Graz, Steiermark, Österreich");
        h.World.SetCalls.Should().BeEmpty("abgebrochen");

        h.Dialogs.Next = new AssignLocationAnswer(false);
        await vm.AssignAsync([103, 999], 47.07, 15.44);
        h.World.SetCalls.Should().BeEmpty("Dateien aus einer anderen Bibliothek werden nie verortet");

        await vm.AssignAsync([7, 103], 47.07, 15.44);
        h.World.SetCalls.Should().ContainSingle().Which.Ids.Should().Equal(7);
        h.World.SetCalls[0].Keep.Should().BeFalse();
    }

    [Fact]
    public async Task Marker_behaelt_seinen_Index_auch_nach_dem_Neuladen()
    {
        var (h, vm) = await LoadedAsync();
        vm.Viewport.ShowWorld();
        var old = vm.Markers.Single();
        await vm.AssignAsync([6, 7, 8], 10, 10); // Neuladen ersetzt den Index
        vm.Index.Should().NotBeSameAs(old.Index);

        ShowFilesOnMapSelectionMessage? received = null;
        h.Messenger.Register<ShowFilesOnMapSelectionMessage>(this, (_, m) => received = m);
        vm.ShowInGallery(old);
        received!.FileIds.Should().BeEquivalentTo([1, 2, 3, 4, 5], "Dateien des angeklickten Markers, nicht des neuen Index");

        vm.Viewport.CenterOn(48.2082, 16.3738, 10);
        vm.VisibleFileIds().Should().HaveCount(vm.VisiblePhotoCount, "„Im Ausschnitt“ zählt und zeigt dieselben Fotos");
    }

    [Fact]
    public async Task Aenderungen_anderer_Bibliotheken_laden_nicht_neu()
    {
        var (h, vm) = await LoadedAsync();
        var queries = h.World.PointQueries.Count;
        h.Messenger.Send(new FilesChangedMessage(2, FilesChangeKind.Metadata, [101]));
        await Task.Delay(900);
        h.World.PointQueries.Should().HaveCount(queries, "Bibliothek 2 gehört nicht zum Bereich");

        h.Messenger.Send(new ScanCompletedMessage(1, null!));
        await WaitUntilAsync(() => h.World.PointQueries.Count > queries);
    }

    [Fact]
    public async Task Ortssuche_springt_zum_Ort()
    {
        var (_, vm) = await LoadedAsync();
        vm.SearchText = "gr";
        vm.IsSearchOpen.Should().BeTrue();
        vm.SearchResults.Should().ContainSingle().Which.Detail.Should().Be("Steiermark, Österreich");
        vm.GoToSearchResultCommand.Execute(null);
        vm.Viewport.Zoom.Should().Be(12);
        vm.Viewport.ScreenToGeo(vm.Viewport.Width / 2, vm.Viewport.Height / 2).Latitude.Should().BeApproximately(47.0707, 1e-6);
        vm.IsSearchOpen.Should().BeFalse();
    }

    [Fact]
    public async Task Hinweis_im_versteckten_Modus_nur_mit_Kacheln_aus_dem_Netz()
    {
        var h = new PlacesHarness();
        h.SeedAustria();
        h.Privacy.IsHiddenCatalog = true;
        var offline = h.Create(new DisabledMapTileSource());
        await offline.ActivateAsync();
        offline.IsHiddenNotice.Should().BeFalse("ohne Kachelquelle geht nichts ins Netz");
        offline.TileStatusText.Should().Contain("abgeschaltet");

        var online = h.Create(new OnlineFakeSource());
        await online.ActivateAsync();
        online.IsHiddenNotice.Should().BeTrue();
        online.HasTileStatus.Should().BeFalse();
    }

    [Fact]
    public async Task Marker_bekommen_Vorschaubilder_des_besten_Fotos()
    {
        var (h, vm) = await LoadedAsync();
        await Task.Yield();
        h.Images.Requested.Should().Contain(2);
        vm.Markers.Should().OnlyContain(m => m.Image != null);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "Bedingung innerhalb von 10 s");
            await Task.Delay(20);
        }
    }

    private sealed class OnlineFakeSource : IMapTileSource
    {
        public MapTileSourceState State => MapTileSourceState.Online;

        public string Attribution => OsmTileSource.OsmAttribution;

        public int MaxZoom => 19;

        public event EventHandler? StateChanged
        {
            add { }
            remove { }
        }

        public Task<byte[]?> GetTileAsync(MapTileId id, CancellationToken ct) => Task.FromResult<byte[]?>(null);
    }
}
