using CommunityToolkit.Mvvm.Messaging;
using Lumora.Domain.Enums;
using Lumora.Services.Messaging;
using Lumora.Services.Stories;
using Lumora.Stories;
using Lumora.Wpf.Tests.Fixtures;

namespace Lumora.Wpf.Tests.Stories;

/// <summary>
/// Übersicht der Story-Seite (ADR-0060): Abschnitte und Art-Filter, Suche, Jahres-Chips, Rückblick-Banner nur wenn
/// vorhanden, Zähler „n neu“ sinkt nach dem Öffnen, Anheften/Ausblenden/Wieder zeigen, Diashow einer Karte, Kartenreihen
/// nach Breite, Bilder erst für sichtbare Reihen. Läuft auf dem Test-UI-Thread (Nachrichten und Zähler gehen über ihn).
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class StoriesViewModelTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static Task OnUiAsync(Func<Task> scenario)
        => TestUiThread.Dispatcher.InvokeAsync(scenario).Task.Unwrap().WaitAsync(Timeout);

    private static StoryTestKit Kit()
    {
        var kit = new StoryTestKit();
        kit.Stories.Add(FakeStoryService.Card(1, StoryKind.Trip, "12.–19. Juli 2025", pinned: true, seen: false, days: 3), dayCount: 3);
        kit.Stories.Add(FakeStoryService.Card(2, StoryKind.Event, "Samstag, 6. September 2025", seen: false, subtitle: "Schloss Eggenberg, Graz"));
        kit.Stories.Add(FakeStoryService.Card(3, StoryKind.Event, "3.–5. Mai 2024", year: 2024, subtitle: "Wien"));
        kit.Stories.Add(FakeStoryService.Card(4, StoryKind.Person, "Anna · 2019", year: 2019, subtitle: "312 Fotos mit Anna", personId: 1));
        kit.Stories.Add(FakeStoryService.Card(5, StoryKind.Place, "Grado · 2014–2025", year: 2014, subtitle: "6 Besuche · Italien"));
        kit.Stories.Add(FakeStoryService.Card(6, StoryKind.Event, "Ausgeblendet", hidden: true));
        return kit;
    }

    private static StoryMemory Memory(string key = "R:D:2022-10-21", StoryMemoryKind kind = StoryMemoryKind.OnThisDay)
        => new(key, kind, 3, new DateOnly(2022, 10, 21), "Heute vor 3 Jahren", "Freitag, 21. Oktober 2022", "Wien", "48 Fotos · 2 Videos",
            50, 2, 501, [501, 502, 503, 504, 505], null, false);

    [Fact]
    public Task Uebersicht_gruppiert_nach_Abschnitten_und_zaehlt_je_Art() => OnUiAsync(async () =>
    {
        var kit = Kit();
        var vm = kit.CreateOverview();
        vm.SetLayoutWidth(1000);

        await vm.LoadAsync();

        Sections(vm).Should().Equal("Angeheftet", "Neu", "Reisen & Ereignisse", "Personen", "Orte");
        CardsAfter(vm, "Angeheftet").Should().Equal(1);
        CardsAfter(vm, "Neu").Should().BeEquivalentTo([1, 2]);
        CardsAfter(vm, "Reisen & Ereignisse").Should().BeEquivalentTo([1, 2, 3]);
        CardsAfter(vm, "Personen").Should().Equal(4);
        CardsAfter(vm, "Orte").Should().Equal(5);
        vm.KindOptions.Single(o => o.Filter == StoryKindFilter.Events).Count.Should().Be(3);
        vm.KindOptions.Single(o => o.Filter == StoryKindFilter.Persons).Count.Should().Be(1);
        vm.HiddenCount.Should().Be(1);
        vm.HiddenText.Should().Be("Ausgeblendet (1)");
        vm.Rows.OfType<OverviewMemoryRow>().Should().BeEmpty("ohne Rückblick von heute kein Banner");
        kit.Badge.Text.Should().Be("2 neu");
        vm.SummaryText.Should().StartWith("5 Storys aus");
    });

    [Fact]
    public Task Art_Filter_zeigt_nur_die_gewaehlte_Art() => OnUiAsync(async () =>
    {
        var kit = Kit();
        var vm = kit.CreateOverview();
        await vm.LoadAsync();

        vm.SelectKind(vm.KindOptions.Single(o => o.Filter == StoryKindFilter.Persons));
        Sections(vm).Should().Equal("Personen");
        AllCards(vm).Should().Equal(4);

        vm.SelectKind(vm.KindOptions.Single(o => o.Filter == StoryKindFilter.Events));
        Sections(vm).Should().Equal("Angeheftet", "Neu", "Reisen & Ereignisse");
        vm.Rows.OfType<OverviewSectionRow>().Should().NotContain(s => s.Title == "Personen");

        vm.SelectKind(vm.KindOptions.Single(o => o.Filter == StoryKindFilter.Memories));
        vm.Rows.OfType<OverviewNoteRow>().Should().ContainSingle(n => n.Title == "Heute keine Rückblicke");
    });

    [Fact]
    public Task Suche_findet_Ort_Person_und_Jahr() => OnUiAsync(async () =>
    {
        var kit = Kit();
        var vm = kit.CreateOverview();
        await vm.LoadAsync();

        vm.SearchText = "graz";
        AllCards(vm).Distinct().Should().Equal(2);
        vm.SearchText = "Anna";
        AllCards(vm).Distinct().Should().Equal(4);
        vm.SearchText = "2024";
        AllCards(vm).Distinct().Should().Equal(3);
        vm.SearchText = "wien 2024";
        AllCards(vm).Distinct().Should().Equal(3);
        vm.SearchText = "Paris";
        AllCards(vm).Should().BeEmpty();
        vm.Rows.OfType<OverviewNoteRow>().Single().Title.Should().Contain("Paris");
    });

    [Fact]
    public Task Jahres_Chip_filtert_die_Ereignisse() => OnUiAsync(async () =>
    {
        var kit = Kit();
        kit.Stories.Add(FakeStoryService.Card(7, StoryKind.Event, "2023", year: 2023));
        var vm = kit.CreateOverview();
        await vm.LoadAsync();
        vm.SelectKind(vm.KindOptions.Single(o => o.Filter == StoryKindFilter.Events));

        var years = vm.Rows.OfType<OverviewYearsRow>().Single().Chips;
        years.Select(c => c.Label).Should().Equal("Alle", "2025", "2024", "2023");
        years[0].IsSelected.Should().BeTrue();
        vm.SelectYear(years.Single(c => c.Label == "2024"));

        CardsAfter(vm, "Reisen & Ereignisse").Should().Equal(3);
        vm.Rows.OfType<OverviewYearsRow>().Single().Chips.Single(c => c.IsSelected).Label.Should().Be("2024");
    });

    [Fact]
    public Task Rueckblick_Banner_nur_wenn_es_heute_einen_gibt_und_oeffnen_speichert_ihn() => OnUiAsync(async () =>
    {
        var kit = Kit();
        kit.Stories.Memories.Add(Memory());
        var vm = kit.CreateOverview();
        await vm.LoadAsync();

        var banner = vm.Rows[0].Should().BeOfType<OverviewMemoryRow>().Subject;
        banner.Memory.Kicker.Should().Be("Heute vor 3 Jahren");
        banner.Memory.MetaText.Should().Be("Wien · 48 Fotos · 2 Videos");
        await vm.RequestMemoryAsync(banner.Memory);
        banner.Memory.Image1.Should().NotBeNull();
        kit.Images.RequestedLarge.Should().Equal(501);

        await vm.OpenMemoryAsync(banner.Memory);

        kit.Stories.Calls.Should().Contain("openMemory:R:D:2022-10-21");
        vm.IsStoryOpen.Should().BeTrue();
        vm.CurrentStory!.Card!.Kind.Should().Be(StoryKind.Memory);
    });

    [Fact]
    public Task Monats_Rueckblick_allein_ergibt_kein_Banner() => OnUiAsync(async () =>
    {
        var kit = Kit();
        kit.Stories.Memories.Add(Memory("R:M:2025-10", StoryMemoryKind.ThisMonth));
        var vm = kit.CreateOverview();
        await vm.LoadAsync();

        vm.MemoryBanner.Should().BeNull();
        vm.Rows.OfType<OverviewMemoryRow>().Should().BeEmpty();
        vm.SelectKind(vm.KindOptions.Single(o => o.Filter == StoryKindFilter.Memories));
        vm.Rows.OfType<OverviewMemoryRow>().Should().ContainSingle("im Filter „Rückblicke“ stehen alle von heute");
    });

    [Fact]
    public Task Neu_Zaehler_sinkt_nach_dem_Oeffnen() => OnUiAsync(async () =>
    {
        var kit = Kit();
        var vm = kit.CreateOverview();
        await vm.LoadAsync();
        vm.Overview.NewCount.Should().Be(2);

        await vm.OpenCardAsync(vm.Rows.OfType<OverviewCardRow>().SelectMany(r => r.Cards).First(c => c.Id == 2));
        kit.Stories.Seen.Should().Equal(2);
        vm.CloseStory();
        await vm.LoadAsync();

        vm.Overview.NewCount.Should().Be(1);
        kit.Badge.Text.Should().Be("1 neu");
        kit.Badge.HasNew.Should().BeTrue();
        CardsAfter(vm, "Neu").Should().Equal(1);
    });

    [Fact]
    public Task Nachricht_laedt_gebuendelt_neu() => OnUiAsync(async () =>
    {
        var kit = Kit();
        var vm = kit.CreateOverview();
        await vm.LoadAsync();
        var before = kit.Stories.Calls.Count(c => c == "overview");

        kit.Stories.Cards.Add(FakeStoryService.Card(8, StoryKind.Event, "Neu erkannt", seen: false));
        kit.Messenger.Send(new StoriesChangedMessage(1, StoriesChangeKind.Detected, []));
        kit.Messenger.Send(new StoriesChangedMessage(1, StoriesChangeKind.Detected, []));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (vm.Overview.NewCount != 3 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        vm.Overview.NewCount.Should().Be(3);
        kit.Stories.Calls.Count(c => c == "overview").Should().Be(before + 1, "zwei Nachrichten, ein Neuladen");
    });

    [Fact]
    public Task Anheften_Ausblenden_und_Wieder_zeigen() => OnUiAsync(async () =>
    {
        var kit = Kit();
        var vm = kit.CreateOverview();
        await vm.LoadAsync();
        var card3 = AllCardVms(vm).First(c => c.Id == 3);

        await vm.TogglePinAsync(card3);
        await vm.LoadAsync();
        CardsAfter(vm, "Angeheftet").Should().BeEquivalentTo([1, 3]);

        await vm.HideAsync(AllCardVms(vm).First(c => c.Id == 3));
        await vm.LoadAsync();
        AllCards(vm).Should().NotContain(3);
        vm.HiddenCount.Should().Be(2);
        vm.StatusText.Should().Contain("ausgeblendet");

        vm.ShowHidden = true;
        await vm.LoadAsync();
        Sections(vm).Should().Equal("Ausgeblendet");
        var hidden = AllCardVms(vm).ToList();
        hidden.Select(c => c.Id).Should().BeEquivalentTo([3, 6]);
        hidden.Should().OnlyContain(c => c.IsHidden);

        await vm.UnhideAsync(hidden.Single(c => c.Id == 3));
        kit.Stories.Calls.Should().Contain("hide:3:False");
        AllCards(vm).Should().Equal(6);
        vm.ShowMore(vm.Rows.OfType<OverviewSectionRow>().Single());
        await vm.LoadAsync();
        vm.ShowHidden.Should().BeFalse();
        AllCards(vm).Should().Contain(3);
    });

    [Fact]
    public Task Diashow_einer_Karte_startet_mit_den_Highlights() => OnUiAsync(async () =>
    {
        var kit = Kit();
        var vm = kit.CreateOverview();
        await vm.LoadAsync();

        await vm.StartCardSlideshowAsync(AllCardVms(vm).First(c => c.Id == 1));

        var start = kit.Slideshow.Starts.Should().ContainSingle().Subject;
        start.Ids.Should().Equal(kit.Stories.Highlights[1].Select(h => h.FileId));
        start.Start.Should().Be(0);
        start.Title.Should().Be("12.–19. Juli 2025");
    });

    [Theory]
    [InlineData(1000, 4)]
    [InlineData(700, 2)]
    [InlineData(200, 1)]
    public void Spalten_nach_Breite(double width, int columns) => StoriesViewModel.ColumnsFor(width).Should().Be(columns);

    [Fact]
    public Task Sichtbare_Kartenreihe_laedt_Vorschaubilder_und_Personen() => OnUiAsync(async () =>
    {
        var kit = Kit();
        var vm = kit.CreateOverview();
        vm.SetLayoutWidth(1000);
        await vm.LoadAsync();
        kit.Images.Requested.Should().BeEmpty("Bilder erst für sichtbare Reihen");

        var row = vm.Rows.OfType<OverviewCardRow>().First();
        await vm.RequestRowAsync(row);

        row.Cards.Should().OnlyContain(c => c.Image1 != null && c.Avatars.Count == 2);
        kit.Images.Requested.Should().HaveCount(row.Cards.Count * 3);
        row.CardWidth.Should().Be(Math.Floor((1000 - 16 * 3) / 4.0));
    });

    [Fact]
    public Task Story_aus_Auswahl_zeigt_den_Hinweis_und_Galerie_Bruecke_oeffnet_die_neue_Story() => OnUiAsync(async () =>
    {
        var kit = Kit();
        var vm = kit.CreateOverview();
        await vm.LoadAsync();
        vm.StoryFromSelectionHintCommand.Execute(null);
        vm.StatusText.Should().Contain("Als Story");

        var id = await kit.Bridge.CreateFromSelectionAsync([11, 12, 13], "Mein Ausflug", ask: false);
        await Task.Delay(50);

        kit.Navigation.Tags.Should().Contain("stories");
        vm.CurrentStory.Should().NotBeNull();
        vm.CurrentStory!.StoryId.Should().Be(id!.Value);
        kit.Stories.Created!.Value.FileIds.Should().Equal(11, 12, 13);
    });

    private static List<string> Sections(StoriesViewModel vm) => vm.Rows.OfType<OverviewSectionRow>().Select(s => s.Title).ToList();

    private static IEnumerable<StoryCardViewModel> AllCardVms(StoriesViewModel vm) => vm.Rows.OfType<OverviewCardRow>().SelectMany(r => r.Cards);

    private static List<int> AllCards(StoriesViewModel vm) => AllCardVms(vm).Select(c => c.Id).ToList();

    /// <summary>Karten-Ids der Reihen direkt nach dem Abschnitt <paramref name="title"/>.</summary>
    private static List<int> CardsAfter(StoriesViewModel vm, string title)
    {
        var index = vm.Rows.ToList().FindIndex(r => r is OverviewSectionRow s && s.Title == title);
        index.Should().BeGreaterThanOrEqualTo(0, title);
        return vm.Rows.Skip(index + 1).TakeWhile(r => r is not OverviewSectionRow).OfType<OverviewCardRow>().SelectMany(r => r.Cards).Select(c => c.Id).ToList();
    }
}
