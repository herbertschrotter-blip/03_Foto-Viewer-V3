using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Slideshow;

namespace Lumora.Wpf.Tests.Slideshow;

/// <summary>Reihenfolgen (Liste, seitenweise, zufällig), Einstellungen <c>slideshow.*</c>, Leiste/Mauszeiger (ADR-0059).</summary>
public sealed class SlideshowSequenceAndSettingsTests
{
    [Fact]
    public async Task Seitenweise_Reihenfolge_laedt_nur_die_gebrauchten_Seiten()
    {
        var loaded = new List<int>();
        var sequence = new PagedSlideshowSequence(500_000, 200, (page, size, _) =>
        {
            loaded.Add(page);
            return Task.FromResult<IReadOnlyList<int>>(Enumerable.Range((page * size) + 1, size).ToList());
        });

        (await sequence.GetRangeAsync(0, 50, CancellationToken.None)).Should().Equal(Enumerable.Range(1, 50));
        (await sequence.GetRangeAsync(180, 50, CancellationToken.None)).Should().Equal(Enumerable.Range(181, 50), "über die Seitengrenze hinweg");
        (await sequence.GetRangeAsync(499_990, 50, CancellationToken.None)).Should().Equal(Enumerable.Range(499_991, 10), "am Ende kürzer");
        (await sequence.GetRangeAsync(10, 5, CancellationToken.None)).Should().Equal(11, 12, 13, 14, 15);

        loaded.Should().Equal(0, 1, 2499);
        sequence.PagesLoaded.Should().Be(3, "nie alle 500 000 Ids auf einmal");
    }

    [Fact]
    public async Task Zufaellig_ueber_seitenweise_holt_einzelne_Zeilen_statt_ganzer_Seiten()
    {
        var calls = new List<(int Page, int Size)>();
        var paged = new PagedSlideshowSequence(500_000, 200, (page, size, _) =>
        {
            calls.Add((page, size));
            return Task.FromResult<IReadOnlyList<int>>(Enumerable.Range((page * size) + 1, size).ToList());
        });
        var shuffled = new ShuffledSlideshowSequence(paged, firstIndex: 123_456, new Random(7));

        var block = await shuffled.GetRangeAsync(0, 50, CancellationToken.None);

        block.Should().HaveCount(50).And.OnlyHaveUniqueItems();
        block[0].Should().Be(123_457);
        calls.Should().OnlyContain(c => c.Size == 1, "je Folie eine Zeile an ihrer Stelle, keine 200er-Seite");
        paged.SingleLoads.Should().Be(50);
        paged.PagesLoaded.Should().Be(0);
    }

    [Fact]
    public async Task Seitenweise_Reihenfolge_ist_bei_kuerzeren_Seiten_kuerzer()
    {
        // Katalog inzwischen auf 5 Dateien geschrumpft
        var sequence = new PagedSlideshowSequence(10, 4, (page, size, _) =>
            Task.FromResult<IReadOnlyList<int>>(size == 1 ? (page < 5 ? [page + 1] : []) : page == 0 ? [1, 2, 3, 4] : [5]));

        (await sequence.GetRangeAsync(2, 6, CancellationToken.None)).Should().Equal(3, 4, 5);
        (await sequence.GetRangeAsync(9, 1, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task Zufaellige_Reihenfolge_ist_eine_Permutation_mit_dem_Startbild_vorn()
    {
        var ids = Enumerable.Range(101, 40).ToList();
        var shuffled = new ShuffledSlideshowSequence(new ListSlideshowSequence(ids), firstIndex: 7, new Random(1234));

        var all = await shuffled.GetRangeAsync(0, 40, CancellationToken.None);
        all.Should().HaveCount(40).And.OnlyHaveUniqueItems();
        all.Should().BeEquivalentTo(ids);
        all[0].Should().Be(108, "das Startbild kommt zuerst");
        all.Should().NotEqual(ids, "gemischt");

        var again = await new ShuffledSlideshowSequence(new ListSlideshowSequence(ids), 7, new Random(1234)).GetRangeAsync(0, 40, CancellationToken.None);
        again.Should().Equal(all, "gleicher Startwert, gleiche Reihenfolge");
    }

    [Fact]
    public async Task Listen_Reihenfolge_liefert_Bereiche()
    {
        var sequence = new ListSlideshowSequence([5, 6, 7]);
        (await sequence.GetRangeAsync(1, 10, CancellationToken.None)).Should().Equal(6, 7);
        (await sequence.GetRangeAsync(3, 1, CancellationToken.None)).Should().BeEmpty();
        (await sequence.GetRangeAsync(-1, 1, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task Einstellungen_werden_unter_slideshow_gespeichert_und_gelesen()
    {
        var values = new MemoryViewSettingsStore();
        var store = new SlideshowSettingsStore(values, NullLogger<SlideshowSettingsStore>.Instance);

        (await store.LoadAsync()).Should().Be(SlideshowSettings.Default, "ohne gespeicherte Werte gelten die Standardwerte");

        var custom = new SlideshowSettings { SlideSeconds = 3, KenBurns = false, PlayVideos = false, MaxVideoSeconds = 60, ShowCaption = false, Order = SlideshowOrder.Random };
        await store.SaveAsync(custom);

        values.Values.Keys.Should().OnlyContain(k => k.StartsWith("slideshow.", StringComparison.Ordinal));
        values.Values.Should().HaveCount(6);
        (await store.LoadAsync()).Should().Be(custom);
    }

    [Fact]
    public async Task Ungueltige_Werte_werden_auf_gueltige_gebracht()
    {
        var values = new MemoryViewSettingsStore();
        values.Values[SlideshowSettingsStore.SlideSecondsKey] = "7";
        values.Values[SlideshowSettingsStore.MaxVideoSecondsKey] = "100000";
        values.Values[SlideshowSettingsStore.KenBurnsKey] = "vielleicht";
        values.Values[SlideshowSettingsStore.OrderKey] = "rückwärts";

        var loaded = await new SlideshowSettingsStore(values, NullLogger<SlideshowSettingsStore>.Instance).LoadAsync();
        loaded.SlideSeconds.Should().Be(8);
        loaded.MaxVideoSeconds.Should().Be(600);
        loaded.KenBurns.Should().BeTrue();
        loaded.Order.Should().Be(SlideshowOrder.AsView);
    }

    [Fact]
    public async Task Einstellungs_Abschnitt_laedt_und_speichert_sofort()
    {
        var values = new MemoryViewSettingsStore();
        var store = new SlideshowSettingsStore(values, NullLogger<SlideshowSettingsStore>.Instance);
        await store.SaveAsync(SlideshowSettings.Default with { SlideSeconds = 8, Order = SlideshowOrder.Random });
        var viewModel = new SlideshowSettingsViewModel(store, NullLogger<SlideshowSettingsViewModel>.Instance);

        await viewModel.LoadAsync();
        viewModel.SelectedDuration.Value.Should().Be(8);
        viewModel.SelectedOrder.Value.Should().Be(SlideshowOrder.Random);
        viewModel.SavedText.Should().BeNull("Laden speichert nicht");

        viewModel.PlayVideos = false;
        viewModel.SelectedMaxVideo = viewModel.MaxVideoOptions.First(o => o.Value == 10);
        await viewModel.SaveAsync();
        var saved = await store.LoadAsync();
        saved.PlayVideos.Should().BeFalse();
        saved.MaxVideoSeconds.Should().Be(10);
        saved.SlideSeconds.Should().Be(8);
    }

    [Fact]
    public async Task Leiste_und_Mauszeiger_blenden_nach_zwei_Sekunden_aus_und_bei_Bewegung_ein()
    {
        using var h = new SlideshowHarness([1]);
        h.Loader.AddImage(1);
        await h.ViewModel.StartAsync();
        h.ViewModel.IsChromeVisible.Should().BeTrue();

        h.Time.Advance(TimeSpan.FromSeconds(1.9));
        h.ViewModel.IsChromeVisible.Should().BeTrue();
        h.Time.Advance(TimeSpan.FromSeconds(0.2));
        h.ViewModel.IsChromeVisible.Should().BeFalse("2 s ohne Mausbewegung");

        h.ViewModel.NotifyUserActivity();
        h.ViewModel.IsChromeVisible.Should().BeTrue("Bewegung blendet ein");
        h.Time.Advance(TimeSpan.FromSeconds(1.5));
        h.ViewModel.NotifyUserActivity();
        h.Time.Advance(TimeSpan.FromSeconds(1.5));
        h.ViewModel.IsChromeVisible.Should().BeTrue("jede Bewegung startet die 2 s neu");
        h.Time.Advance(TimeSpan.FromSeconds(0.6));
        h.ViewModel.IsChromeVisible.Should().BeFalse();
    }

    [Fact]
    public async Task Ueber_der_Leiste_bleibt_sie_sichtbar()
    {
        using var h = new SlideshowHarness([1]);
        h.Loader.AddImage(1);
        await h.ViewModel.StartAsync();

        h.ViewModel.IsChromeHovered = true;
        h.Time.Advance(TimeSpan.FromSeconds(10));
        h.ViewModel.IsChromeVisible.Should().BeTrue();

        h.ViewModel.IsChromeHovered = false;
        h.Time.Advance(TimeSpan.FromSeconds(2.1));
        h.ViewModel.IsChromeVisible.Should().BeFalse();
    }
}
