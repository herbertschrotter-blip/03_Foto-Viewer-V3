using CommunityToolkit.Mvvm.Messaging;
using Lumora.Domain.Enums;
using Lumora.Services.Messaging;
using Lumora.Services.Stories;

namespace Lumora.Tests.Stories;

/// <summary>
/// Story-Dienst (ADR-0058): Übersicht (Angeheftet, Neu, Jahre, Ausgeblendete), Details mit Tagesabschnitten, manuelle
/// Zu-/Abgänge über die Neuberechnung, „Story aus Auswahl“, „Als Album speichern“, Rückblicke, Titelbild, Einstellungen.
/// </summary>
public sealed class StoryServiceTests
{
    [Fact]
    public async Task Uebersicht_Neu_Angeheftet_Jahre_und_Ausgeblendete()
    {
        using var w = await StoryWorld.CreateAsync();
        var group = await w.GroupIdAsync();
        w.Add(StoryWorld.Burst(StoryWorld.Utc(2024, 12, 24, 15), 14, TestPlaces.Graz));
        w.Add(StoryWorld.Burst(StoryWorld.Utc(2025, 5, 3, 9), 13, TestPlaces.Wien)
            .Concat(StoryWorld.Burst(StoryWorld.Utc(2025, 5, 4, 9), 13, TestPlaces.Wien)));
        w.Add(StoryWorld.Burst(StoryWorld.Utc(2023, 1, 1, 9), 40, TestPlaces.Graz)); // Graz = zu Hause
        await w.Index.RunFullAsync(group);

        var overview = await w.Stories.GetOverviewAsync();
        overview.EventsAndTrips.Should().HaveCount(3);
        overview.EventsAndTrips[0].Kind.Should().Be(StoryKind.Trip, "neueste zuerst");
        overview.EventsAndTrips[0].Title.Should().Be("3.–4. Mai 2025");
        overview.EventsAndTrips[0].Subtitle.Should().Be("Wien", "Österreich ist zu Hause — kein Land dahinter");
        overview.EventsAndTrips[0].KindLabel.Should().Be("Reise");
        overview.EventsAndTrips[1].Title.Should().Be("Dienstag, 24. Dezember 2024");
        overview.Years.Should().Equal(new StoryYearCount(2025, 1), new StoryYearCount(2024, 1), new StoryYearCount(2023, 1));
        overview.New.Should().BeEmpty("die erste Erkennung zählt nicht als neu");
        overview.TotalFiles.Should().Be(80);

        // Neue Story nach der ersten Erkennung: „Neu“, bis sie gesehen ist.
        var later = w.Add(StoryWorld.Burst(StoryWorld.Utc(2025, 9, 21, 9), 12, TestPlaces.Rovinj));
        await w.Index.RunIncrementalAsync(group, later);
        overview = await w.Stories.GetOverviewAsync();
        var fresh = overview.New.Should().ContainSingle().Subject;
        fresh.KindLabel.Should().Be("Ausflug");
        fresh.Subtitle.Should().Be("Rovinj — Kroatien");
        overview.NewCount.Should().Be(1);
        await w.Stories.MarkSeenAsync([fresh.Id]);
        (await w.Stories.GetOverviewAsync()).New.Should().BeEmpty();

        // Anheften, ausblenden
        var trip = overview.EventsAndTrips.Single(c => c.Kind == StoryKind.Trip);
        await w.Stories.SetPinnedAsync(trip.Id, true);
        var christmas = overview.EventsAndTrips.Single(c => c.FirstDay == new DateOnly(2024, 12, 24));
        await w.Stories.SetHiddenAsync(christmas.Id, true);
        overview = await w.Stories.GetOverviewAsync();
        overview.Pinned.Should().ContainSingle(c => c.Id == trip.Id);
        overview.HiddenCount.Should().Be(1);
        overview.EventsAndTrips.Should().NotContain(c => c.Id == christmas.Id);
        (await w.Stories.GetHiddenAsync()).Should().ContainSingle(c => c.Id == christmas.Id);
        await w.Stories.SetHiddenAsync(christmas.Id, false);
        (await w.Stories.GetOverviewAsync()).HiddenCount.Should().Be(0);

        // Arten abschalten blendet sie aus (ohne sie zu löschen)
        await w.Settings.SaveAsync(StorySettings.Default with { EventsEnabled = false });
        (await w.Stories.GetOverviewAsync()).EventsAndTrips.Should().BeEmpty();
        await w.Index.RunFullAsync(group);
        w.All().Count(s => s.Kind is StoryKind.Event or StoryKind.Trip).Should().Be(4, "abgeschaltete Arten bleiben gespeichert");
    }

    [Fact]
    public async Task Details_mit_Tagen_Personen_und_Highlights()
    {
        using var w = await StoryWorld.CreateAsync();
        w.Add(StoryWorld.Burst(StoryWorld.Utc(2020, 1, 1), 40, TestPlaces.Graz));
        var day1 = w.Add(StoryWorld.Burst(StoryWorld.Utc(2025, 7, 12, 9), 20, TestPlaces.Grado, videos: 1));
        var day2 = w.Add(StoryWorld.Burst(StoryWorld.Utc(2025, 7, 13, 9), 15, TestPlaces.Triest));
        await w.AddPersonAsync("Anna", day1.Take(5));
        await w.Index.RunFullAsync(await w.GroupIdAsync());
        var trip = (await w.Stories.GetOverviewAsync()).EventsAndTrips.Single(c => c.Kind == StoryKind.Trip);

        var details = (await w.Stories.GetDetailsAsync(trip.Id))!;
        details.Kicker.Should().Be("Reise · 2 Tage");
        details.Card.Title.Should().Be("12.–13. Juli 2025");
        details.Card.Subtitle.Should().Be("Grado · Triest — Italien");
        details.Days.Should().HaveCount(2);
        details.Days[0].Title.Should().Be("Samstag, 12. Juli");
        details.Days[0].ShortTitle.Should().Be("Sa 12.");
        details.Days[0].Places.Should().Be("Grado");
        details.Days[0].FileIds.Should().Equal(day1, "Aufnahme-Reihenfolge");
        details.Days[0].VideoCount.Should().Be(1);
        details.Days[1].FileIds.Should().Equal(day2);
        details.Persons.Should().ContainSingle().Which.Should().Be(new StoryPerson(details.Persons[0].PersonId, "Anna", 5));
        details.Places.Select(p => p.Name).Should().Equal("Grado", "Triest");

        var highlights = await w.Stories.GetHighlightsAsync(trip.Id);
        highlights.Should().HaveCount(6, "15 % von 35 = 5, mindestens 6");
        highlights.Select(h => h.FileId).Should().BeInAscendingOrder("Aufnahme-Reihenfolge");
        (await w.Stories.GetPreviewFileIdsAsync([trip.Id]))[trip.Id].Should().HaveCount(3).And.StartWith(trip.CoverFileId!.Value);
        (await w.Stories.GetStoriesForFileAsync(day2[0])).Should().ContainSingle(c => c.Id == trip.Id);
    }

    [Fact]
    public async Task Manuelle_Zu_und_Abgaenge_ueberstehen_die_Neuberechnung_Titelbild_setzen_und_zuruecksetzen()
    {
        using var w = await StoryWorld.CreateAsync();
        var group = await w.GroupIdAsync();
        var files = w.Add(StoryWorld.Burst(StoryWorld.Utc(2025, 6, 1, 9), 15));
        var other = w.Add(StoryWorld.Burst(StoryWorld.Utc(2025, 3, 1, 9), 2));
        await w.Index.RunFullAsync(group);
        var story = w.All().Single().Id;

        (await w.Stories.AddFilesAsync(story, other)).Should().Be(2);
        (await w.Stories.AddFilesAsync(story, [files[14]])).Should().Be(0, "zählt schon (automatisch)");
        w.Scalar("SELECT Origin FROM StoryItems WHERE StoryId = $s AND FileId = $f", ("$s", story), ("$f", files[14])).Should().Be(1, "bleibt auch, wenn die Erkennung sie nicht mehr zählt");
        (await w.Stories.RemoveFilesAsync(story, [files[0], files[1]])).Should().Be(2);
        (await w.Stories.GetAsync(story))!.FileCount.Should().Be(15);
        await w.Index.RunFullAsync(group);
        w.Items(story).Should().BeEquivalentTo(files.Skip(2).Concat(other), "Ausschlüsse bleiben draußen, Hinzugefügte bleiben drin");
        (await w.Stories.GetAsync(story))!.FirstDay.Should().Be(new DateOnly(2025, 3, 1), "der Zeitraum folgt auch den hinzugefügten Fotos");

        await w.Stories.SetCoverAsync(story, files[7]);
        (await w.Stories.GetAsync(story))!.Should().Match<StoryCard>(c => c.CoverFileId == files[7] && c.CoverIsManual);
        await FluentActions.Awaiting(() => w.Stories.SetCoverAsync(story, files[0])).Should().ThrowAsync<InvalidOperationException>("ausgeschlossen");
        await w.Stories.SetCoverAsync(story, null);
        var reset = (await w.Stories.GetAsync(story))!;
        reset.CoverIsManual.Should().BeFalse();
        reset.CoverFileId.Should().NotBeNull();
        await FluentActions.Awaiting(() => w.Stories.DeleteAsync(story)).Should().ThrowAsync<InvalidOperationException>("automatische Storys nur ausblenden");
    }

    [Fact]
    public async Task Story_aus_Auswahl_und_als_Album_speichern()
    {
        using var w = await StoryWorld.CreateAsync();
        var ids = w.Add(StoryWorld.Burst(StoryWorld.Utc(2025, 8, 1, 9), 3, TestPlaces.Grado)
            .Concat(StoryWorld.Burst(StoryWorld.Utc(2025, 8, 4, 9), 4, TestPlaces.Triest)));
        var changes = new List<StoriesChangedMessage>();
        w.Fx.Messenger.Register<StoriesChangedMessage>(changes, (r, m) => ((List<StoriesChangedMessage>)r).Add(m));

        var id = await w.Stories.CreateFromSelectionAsync(ids, "Sommer am Meer");
        changes.Should().ContainSingle(m => m.Kind == StoriesChangeKind.Created && m.StoryIds.Contains(id));
        var card = (await w.Stories.GetAsync(id))!;
        card.Kind.Should().Be(StoryKind.Custom);
        card.Title.Should().Be("Sommer am Meer");
        card.AutoTitle.Should().Be("1.–4. August 2025");
        card.FileCount.Should().Be(7);
        await w.Index.RunFullAsync(await w.GroupIdAsync());
        w.Items(id).Should().BeEquivalentTo(ids, "die Erkennung fasst eigene Storys nie an");
        (await w.Stories.GetOverviewAsync()).Custom.Should().ContainSingle(c => c.Id == id);

        var album = await w.Stories.SaveAsAlbumAsync(id);
        album.Name.Should().Be("Sommer am Meer");
        (await w.Albums.GetFileIdsAsync(album.Id)).Should().BeEquivalentTo(ids);
        var second = await w.Stories.SaveAsAlbumAsync(id, highlightsOnly: true);
        second.Name.Should().Be("Sommer am Meer (2)", "Albumnamen sind je Gruppe eindeutig");
        (await w.Albums.GetFileIdsAsync(second.Id)).Should().HaveCount(6);

        (await w.Stories.RemoveFilesAsync(id, ids)).Should().Be(7);
        (await w.Stories.GetAsync(id)).Should().BeNull("eine leere eigene Story geht");
        await FluentActions.Awaiting(() => w.Stories.CreateFromSelectionAsync([999_999])).Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Rueckblicke_je_Tag_dynamisch_und_beim_Oeffnen_gespeichert()
    {
        using var w = await StoryWorld.CreateAsync();
        var today = StoryWorld.Today; // 21.10.2026
        var threeYears = w.Add(StoryWorld.Burst(StoryWorld.Utc(2023, 10, 21, 9), 7, TestPlaces.Wien));
        w.Add(StoryWorld.Burst(StoryWorld.Utc(2025, 10, 21, 9), 5)); // zu wenige
        w.Add(StoryWorld.Burst(StoryWorld.Utc(2019, 10, 3, 9), 6)); // gleicher Monat, anderer Tag

        var memories = await w.Stories.GetMemoriesAsync();
        var day = memories.Should().ContainSingle(m => m.Kind == StoryMemoryKind.OnThisDay).Subject;
        day.Kicker.Should().Be("Heute vor 3 Jahren");
        day.Title.Should().Be("Samstag, 21. Oktober 2023");
        day.FileCount.Should().Be(7);
        day.Key.Should().Be("R:D:2023-10-21");
        day.StoryId.Should().BeNull();
        var month = memories.Should().ContainSingle(m => m.Kind == StoryMemoryKind.ThisMonth).Subject;
        month.Kicker.Should().Be("Oktober in früheren Jahren");
        month.Title.Should().Be("Oktober · 2019–2025");
        month.FileCount.Should().Be(18);
        (await w.Stories.GetMemoriesAsync(today.AddDays(1))).Should().NotContain(m => m.Kind == StoryMemoryKind.OnThisDay, "jeder Tag hat seine eigenen Rückblicke");

        var storyId = await w.Stories.OpenMemoryAsync(day.Key);
        w.Items(storyId).Should().BeEquivalentTo(threeYears);
        await w.Stories.SetPinnedAsync(storyId, true);
        (await w.Stories.OpenMemoryAsync(day.Key)).Should().Be(storyId, "derselbe Rückblick wird wiederverwendet");
        var overview = await w.Stories.GetOverviewAsync();
        overview.Pinned.Should().ContainSingle(c => c.Id == storyId && c.Kind == StoryKind.Memory);
        overview.Memories.Single(m => m.Kind == StoryMemoryKind.OnThisDay).Should().Match<StoryMemory>(m => m.StoryId == storyId && m.IsPinned);
        (await w.Stories.GetDetailsAsync(storyId))!.Kicker.Should().Be("Heute vor 3 Jahren");
        await w.Index.RunFullAsync(await w.GroupIdAsync());
        (await w.Stories.GetAsync(storyId)).Should().NotBeNull("die Erkennung lässt gespeicherte Rückblicke stehen");
        await w.Stories.DeleteAsync(storyId);
        (await w.Stories.GetAsync(storyId)).Should().BeNull();

        await w.Settings.SaveAsync(StorySettings.Default with { MemoriesEnabled = false });
        (await w.Stories.GetMemoriesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Einstellungen_werden_gespeichert_und_begrenzt()
    {
        using var w = await StoryWorld.CreateAsync();
        (await w.Settings.LoadAsync()).Should().Be(StorySettings.Default.Normalize());
        var custom = new StorySettings(PlacesEnabled: false, PauseHours: 3, MinFiles: 8, Home: new StoryHomeSetting(TestPlaces.Graz, 47.07, 15.44), MinPersonFiles: 50, HighlightShare: 0.3);
        await w.Settings.SaveAsync(custom);
        (await w.Settings.LoadAsync()).Should().Be(custom.Normalize());
        await w.Settings.SaveAsync(StorySettings.Default with { PauseHours = 500, HighlightShare = 5 });
        var clamped = await w.Settings.LoadAsync();
        clamped.PauseHours.Should().Be(48);
        clamped.HighlightShare.Should().Be(0.6);
        StoryHomeSetting.Parse("geo:47.5,15.25").Should().Be(new StoryHomeSetting(null, 47.5, 15.25));
        StoryHomeSetting.Parse("auto").IsAutomatic.Should().BeTrue();
        w.Scalar("SELECT COUNT(*) FROM Settings WHERE Key LIKE 'stories.%'").Should().Be(9);
    }
}
