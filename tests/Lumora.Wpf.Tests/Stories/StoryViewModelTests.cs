using Lumora.Domain.Enums;
using Lumora.Services.Stories;
using Lumora.Stories;
using Lumora.Wpf.Tests.Fixtures;

namespace Lumora.Wpf.Tests.Stories;

/// <summary>
/// Story-Ansicht (ADR-0060): Laden markiert als gesehen, Kopf, Highlights ↔ Alle Fotos, Tagesabschnitte mit Collage-Blöcken
/// bzw. dichter Collage (Nicht-Highlights blass), Tag aufklappen, lange Reisen eingeklappt, Tages-Sprung, Diashow mit der
/// richtigen Liste, Lightbox, Titel ändern/zurücksetzen, Titelbild wählen, Fotos entfernen, Anheften, Album, Ausblenden,
/// Collage-Menüpunkt deaktiviert, Abschnitte nach Tageszeit bei einem Tag.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class StoryViewModelTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static Task OnUiAsync(Func<Task> scenario)
        => TestUiThread.Dispatcher.InvokeAsync(scenario).Task.Unwrap().WaitAsync(Timeout);

    private static (StoryTestKit Kit, StoryCard Card) Trip(int days = 3, int perDay = 9, bool seen = false)
    {
        var kit = new StoryTestKit();
        var card = kit.Stories.Add(FakeStoryService.Card(1, StoryKind.Trip, "12.–19. Juli 2025", seen: seen, days: days), dayCount: days, perDay: perDay);
        return (kit, card);
    }

    [Fact]
    public Task Laden_zeigt_Kopf_Tage_und_markiert_als_gesehen() => OnUiAsync(async () =>
    {
        var (kit, _) = Trip();
        var vm = kit.CreateStory(1);

        await vm.LoadAsync();

        kit.Stories.Seen.Should().Equal(1);
        vm.Title.Should().Be("12.–19. Juli 2025");
        vm.Kicker.Should().Be("Reise · 3 Tage");
        vm.Section.Should().Be("Reisen & Ereignisse");
        vm.PersonsText.Should().Be("Anna, Max");
        vm.HighlightCount.Should().Be(9);
        vm.AllCount.Should().Be(27);
        vm.DayChips.Select(c => c.Index).Should().Equal(1, 2, 3);
        vm.DayChips[0].IsSelected.Should().BeTrue();
        vm.Rows[0].Should().BeOfType<StoryCoverRow>();
        vm.Rows[1].Should().BeOfType<StoryBarSpacerRow>();
        vm.Rows.OfType<StoryDayHeaderRow>().Select(h => h.Kicker).Should().Equal("Tag 1", "Tag 2", "Tag 3");
        vm.Rows.OfType<StoryDayHeaderRow>().First().CountsText.Should().Be("9 Fotos · 3 ausgewählt");
        vm.Rows.Last().Should().BeOfType<StoryEndRow>();
        vm.EndText.Should().Be("9 von 27 Fotos als Highlights · 10 ähnliche weggelassen");
        vm.CoverImage.Should().NotBeNull();
        kit.Images.RequestedLarge.Should().Contain(100, "Titelbild groß");
    });

    [Fact]
    public Task Gesehene_Story_wird_nicht_erneut_markiert() => OnUiAsync(async () =>
    {
        var (kit, _) = Trip(seen: true);
        await kit.CreateStory(1).LoadAsync();
        kit.Stories.Seen.Should().BeEmpty();
    });

    [Fact]
    public Task Highlights_als_Collage_Bloecke_Alle_Fotos_dicht_mit_blassen_Nicht_Highlights() => OnUiAsync(async () =>
    {
        var (kit, _) = Trip();
        var vm = kit.CreateStory(1);
        vm.SetLayoutWidth(1000);
        await vm.LoadAsync();

        var blocks = vm.Rows.OfType<StoryTilesRow>().ToList();
        blocks.Should().OnlyContain(b => !b.IsDense);
        blocks.SelectMany(b => b.Tiles).Select(t => t.FileId).Should().BeEquivalentTo(kit.Stories.Highlights[1].Select(h => h.FileId));
        blocks.SelectMany(b => b.Tiles).Should().OnlyContain(t => t.IsHighlight && t.Opacity == 1.0 && t.Reason == "scharf · Favorit");
        blocks.SelectMany(b => b.Tiles).Where(t => t.FileId % 2 == 0).Should().OnlyContain(t => t.SimilarText == "+2 ähnliche");
        vm.Rows.OfType<StoryDayFooterRow>().Select(f => f.Text).Should().AllBe("Alle 9 Fotos dieses Abschnitts");

        vm.ShowAllPhotos();

        vm.IsAllMode.Should().BeTrue();
        var dense = vm.Rows.OfType<StoryTilesRow>().ToList();
        dense.Should().OnlyContain(r => r.IsDense);
        var tiles = dense.SelectMany(r => r.Tiles).ToList();
        tiles.Should().HaveCount(27);
        tiles.Where(t => !t.IsHighlight).Should().OnlyContain(t => t.Opacity < 0.5, "Nicht-Highlights blasser");
        tiles.Where(t => t.IsHighlight).Should().OnlyContain(t => t.Opacity == 1.0);
        var rights = dense.Select(row => row.Tiles.Max(t => t.X + t.Width)).ToList();
        rights.Should().OnlyContain(r => r <= 1000.5, "nie breiter als die Seite");
        rights.Count(r => Math.Abs(r - 1000) <= 1.0).Should().BeGreaterThan(0, "gefüllte Reihen randvoll");

        vm.EndText.Should().Be("Blasse Kacheln sind nicht in den Highlights.");
        vm.ShowHighlights();
        vm.Rows.OfType<StoryTilesRow>().Should().OnlyContain(r => !r.IsDense);
    });

    [Fact]
    public Task Videos_zeigen_Dauer_und_Favoriten_den_Stern() => OnUiAsync(async () =>
    {
        var (kit, _) = Trip(days: 1, perDay: 20);
        var vm = kit.CreateStory(1);
        await vm.LoadAsync();
        vm.ShowAllPhotos();

        var tiles = vm.Rows.OfType<StoryTilesRow>().SelectMany(r => r.Tiles).ToList();
        tiles.Where(t => t.FileId % 10 == 5).Should().OnlyContain(t => t.IsVideo && t.DurationText == "0:42");
        tiles.Where(t => t.FileId % 7 == 0).Should().OnlyContain(t => t.IsFavorite);
        tiles.Single(t => t.FileId == 105).AutomationText.Should().Be("105.mp4, Video");
    });

    [Fact]
    public Task Tag_aufklappen_zeigt_alle_Fotos_des_Tages() => OnUiAsync(async () =>
    {
        var (kit, _) = Trip();
        var vm = kit.CreateStory(1);
        await vm.LoadAsync();

        vm.ToggleDay(vm.Rows.OfType<StoryDayFooterRow>().First());

        var dayOneRows = RowsOfDay(vm, 1);
        dayOneRows.OfType<StoryTilesRow>().Should().OnlyContain(r => r.IsDense);
        dayOneRows.OfType<StoryTilesRow>().SelectMany(r => r.Tiles).Should().HaveCount(9);
        dayOneRows.OfType<StoryDayFooterRow>().Single().Text.Should().Be("Nur Highlights zeigen");
        RowsOfDay(vm, 2).OfType<StoryTilesRow>().Should().OnlyContain(r => !r.IsDense, "andere Tage bleiben bei den Highlights");

        vm.ToggleDay(dayOneRows.OfType<StoryDayFooterRow>().Single());
        RowsOfDay(vm, 1).OfType<StoryTilesRow>().Should().OnlyContain(r => !r.IsDense);
        vm.LastScrollRow.Should().Be(vm.Rows.ToList().FindIndex(r => r is StoryDayHeaderRow h && h.Day.Index == 1));
    });

    [Fact]
    public Task Lange_Reise_ersten_Tage_offen_Rest_eingeklappt_Tages_Sprung_klappt_auf() => OnUiAsync(async () =>
    {
        var (kit, _) = Trip(days: 8, perDay: 4);
        var vm = kit.CreateStory(1);
        await vm.LoadAsync();

        vm.Rows.OfType<StoryDayHeaderRow>().Select(h => h.Day.Index).Should().Equal(1, 2, 3);
        var collapsed = vm.Rows.OfType<StoryCollapsedRow>().Single();
        collapsed.Title.Should().Be("5 weitere Tage");
        collapsed.Text.Should().StartWith(vm.Days[3].Chip + " – " + vm.Days[7].Chip).And.Contain("Grado");
        vm.DayChips.Should().HaveCount(8);

        var chip = vm.DayChips.Single(c => c.Index == 6);
        var requested = new List<int>();
        vm.ScrollRequested += requested.Add;
        vm.JumpToDay(chip);

        vm.ShowAllDays.Should().BeTrue();
        vm.Rows.OfType<StoryCollapsedRow>().Should().BeEmpty();
        vm.Rows.OfType<StoryDayHeaderRow>().Should().HaveCount(8);
        var target = vm.Rows.ToList().FindIndex(r => r is StoryDayHeaderRow h && h.Day.Index == 6);
        requested.Should().Equal(target);
        vm.LastScrollRow.Should().Be(target);
        chip.IsSelected.Should().BeTrue();
        vm.DayChips.Count(c => c.IsSelected).Should().Be(1);
    });

    [Fact]
    public Task Diashow_startet_mit_Highlights_bzw_allen_ab_dem_Anfang() => OnUiAsync(async () =>
    {
        var (kit, _) = Trip();
        var vm = kit.CreateStory(1);
        await vm.LoadAsync();

        vm.StartSlideshow();
        vm.ShowAllPhotos();
        vm.StartSlideshow();

        kit.Slideshow.Starts.Should().HaveCount(2);
        kit.Slideshow.Starts[0].Ids.Should().Equal(kit.Stories.Highlights[1].Select(h => h.FileId));
        kit.Slideshow.Starts[1].Ids.Should().Equal(kit.Stories.Details[1].FileIds);
        kit.Slideshow.Starts.Should().OnlyContain(s => s.Start == 0 && s.Title == "12.–19. Juli 2025");
    });

    [Fact]
    public Task Doppelklick_oeffnet_die_Lightbox_mit_den_Fotos_der_Story() => OnUiAsync(async () =>
    {
        var (kit, _) = Trip();
        var vm = kit.CreateStory(1);
        await vm.LoadAsync();
        var tile = vm.Rows.OfType<StoryTilesRow>().SelectMany(r => r.Tiles).First(t => t.FileId == 109);

        vm.OpenTile(tile);

        var open = kit.Lightbox.Opens.Should().ContainSingle().Subject;
        open.Entries.Select(e => e.FileId).Should().Equal(kit.Stories.Details[1].FileIds);
        open.Entries[open.Start].FileId.Should().Be(109);
    });

    [Fact]
    public Task Titel_aendern_ist_nur_eigener_Text_leer_setzt_zurueck() => OnUiAsync(async () =>
    {
        var (kit, _) = Trip();
        var vm = kit.CreateStory(1);
        await vm.LoadAsync();

        await vm.SetTitleAsync("  Sommer in Grado ");
        vm.Title.Should().Be("Sommer in Grado");
        vm.HasCustomTitle.Should().BeTrue();
        vm.AutoTitle.Should().Be("12.–19. Juli 2025");

        await vm.ResetTitleAsync();
        vm.Title.Should().Be("12.–19. Juli 2025");
        vm.HasCustomTitle.Should().BeFalse();
        kit.Stories.Calls.Should().ContainInOrder("title:1:Sommer in Grado", "title:1:<null>");

        await vm.SetTitleAsync("   ");
        kit.Stories.Calls.Last().Should().Be("title:1:<null>", "Leertext = sachliche Überschrift");
    });

    [Fact]
    public Task Titelbild_waehlen_per_Klick_und_automatisch() => OnUiAsync(async () =>
    {
        var (kit, _) = Trip();
        var vm = kit.CreateStory(1);
        await vm.LoadAsync();
        var tile = vm.Rows.OfType<StoryTilesRow>().SelectMany(r => r.Tiles).First(t => t.FileId == 103);

        await vm.TileClickAsync(tile);
        kit.Stories.Calls.Should().NotContain(c => c.StartsWith("cover:", StringComparison.Ordinal), "ohne Modus wirkt ein Klick nicht");

        vm.PickCover();
        vm.IsPickingCover.Should().BeTrue();
        vm.PickHint.Should().Contain("Titelbild");
        await vm.TileClickAsync(tile);

        vm.PickMode.Should().Be(StoryPickMode.None);
        kit.Stories.Calls.Should().Contain("cover:1:103");
        vm.Card!.CoverFileId.Should().Be(103);
        await vm.AutomaticCoverAsync();
        kit.Stories.Calls.Should().Contain("cover:1:auto");
    });

    [Fact]
    public Task Fotos_entfernen_markiert_und_entfernt_nur_die_markierten() => OnUiAsync(async () =>
    {
        var (kit, _) = Trip();
        var vm = kit.CreateStory(1);
        await vm.LoadAsync();
        vm.BeginRemove();
        vm.ConfirmRemoveCommand.CanExecute(null).Should().BeFalse();
        var tiles = vm.Rows.OfType<StoryTilesRow>().SelectMany(r => r.Tiles).Take(2).ToList();

        foreach (var tile in tiles)
        {
            await vm.TileClickAsync(tile);
        }

        vm.MarkedCount.Should().Be(2);
        tiles.Should().OnlyContain(t => t.IsMarked);
        vm.ConfirmRemoveCommand.CanExecute(null).Should().BeTrue();
        await vm.ConfirmRemoveAsync();

        var removed = kit.Stories.Calls.Single(c => c.StartsWith("remove:1:", StringComparison.Ordinal))["remove:1:".Length..].Split(",").Select(int.Parse);
        removed.Should().BeEquivalentTo(tiles.Select(t => t.FileId));
        vm.PickMode.Should().Be(StoryPickMode.None);
        vm.StatusText.Should().Contain("bleiben in der Galerie");
    });

    [Fact]
    public Task Anheften_Album_Ausblenden_und_Collage_deaktiviert() => OnUiAsync(async () =>
    {
        var (kit, _) = Trip();
        var closed = 0;
        var vm = kit.CreateStory(1, () => closed++);
        await vm.LoadAsync();

        vm.PinText.Should().Be("Anheften");
        await vm.TogglePinAsync();
        vm.IsPinned.Should().BeTrue();
        vm.PinText.Should().Be("Lösen");

        await vm.SaveAsAlbumAsync();
        kit.Stories.Calls.Should().Contain("album:1:True", "im Modus Highlights die Highlights");
        vm.StatusText.Should().Contain("Als Album");

        vm.SaveCollageCommand.CanExecute(null).Should().BeFalse("Welle 6b");
        vm.CanSaveCollage.Should().BeFalse();
        vm.CollageHint.Should().Be("Welle 6b");
        vm.CanDelete.Should().BeFalse("automatische Storys lassen sich nur ausblenden");

        await vm.HideAsync();
        kit.Stories.Calls.Should().Contain("hide:1:True");
        closed.Should().Be(1);
    });

    [Fact]
    public Task Eigene_Story_laesst_sich_nach_Rueckfrage_loeschen() => OnUiAsync(async () =>
    {
        var kit = new StoryTestKit();
        kit.Stories.Add(FakeStoryService.Card(9, StoryKind.Custom, "12. Juli 2025", customTitle: "Meins"));
        kit.Dialogs.ConfirmAnswer = true;
        var closed = false;
        var vm = kit.CreateStory(9, () => closed = true);
        await vm.LoadAsync();

        vm.CanDelete.Should().BeTrue();
        await vm.DeleteAsync();

        kit.Dialogs.Confirmations.Should().Equal("Story löschen");
        kit.Stories.Calls.Should().Contain("delete:9");
        closed.Should().BeTrue();
    });

    [Fact]
    public Task Ein_Tag_wird_nach_Tageszeit_geteilt() => OnUiAsync(async () =>
    {
        var kit = new StoryTestKit();
        kit.Stories.Add(FakeStoryService.Card(4, StoryKind.Memory, "Freitag, 21. Oktober 2022", year: 2022), dayCount: 1, perDay: 10);
        var vm = kit.CreateStory(4);
        await vm.LoadAsync();

        // FakeStoryMedia: Uhrzeit 8 + (Id % 100) % 14 → 400–403 Vormittag, 404–408 Nachmittag, 409 Abend
        vm.Days.Select(d => d.Kicker).Should().Equal("Vormittag", "Nachmittag", "Abend");
        vm.Days.Sum(d => d.FileIds.Count).Should().Be(10);
        vm.DayChips.Select(c => c.Label).Should().Equal("Vormittag · Grado", "Nachmittag · Grado", "Abend · Grado");  // Ort des Tages (ohne Ortsdienst je Abschnitt)
        vm.Rows.OfType<StoryDayHeaderRow>().Should().OnlyContain(h => h.Title == kit.Stories.Details[4].Days[0].Title);
        vm.Days.Select(d => d.FileIds.Count).Should().Equal(4, 5, 1);
    });

    [Fact]
    public Task Fehlende_Story_meldet_NotFound() => OnUiAsync(async () =>
    {
        var vm = new StoryTestKit().CreateStory(77);
        await vm.LoadAsync();
        vm.NotFound.Should().BeTrue();
        vm.Rows.Should().BeEmpty();
    });

    [Fact]
    public Task Sichtbare_Zeile_fordert_ihre_Vorschaubilder_an_grosse_Plaetze_gross() => OnUiAsync(async () =>
    {
        var (kit, _) = Trip(days: 1, perDay: 30);
        kit.Images.Deliver = false;
        var vm = kit.CreateStory(1);
        await vm.LoadAsync();
        var block = vm.Rows.OfType<StoryTilesRow>().First(r => r.Tiles.Any(t => t.IsLarge));

        vm.RequestRowImages(block);

        kit.Images.RequestedLarge.Should().Contain(block.Tiles.Where(t => t.IsLarge).Select(t => t.FileId));
        kit.Images.Requested.Should().BeEquivalentTo(block.Tiles.Where(t => !t.IsLarge).Select(t => t.FileId));
    });

    private static List<StoryRow> RowsOfDay(StoryViewModel vm, int day)
    {
        var rows = vm.Rows.ToList();
        var start = rows.FindIndex(r => r is StoryDayHeaderRow h && h.Day.Index == day);
        return rows.Skip(start + 1).TakeWhile(r => r is not (StoryDayHeaderRow or StoryCollapsedRow or StoryEndRow)).ToList();
    }
}
