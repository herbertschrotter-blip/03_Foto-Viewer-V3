using Lumora.Domain.Enums;
using Lumora.Services.Messaging;
using Lumora.Services.Stories;

namespace Lumora.Tests.Stories;

/// <summary>
/// Erkennung im Katalog (ADR-0058): Ereignisse/Reisen/Personen/Orte entstehen in der DB, eine Neuberechnung — auch
/// inkrementell nach neuen Fotos am Rand — findet die Storys über Signatur bzw. gemeinsame Dateien wieder und behält
/// Benutzer-Zustand; ausgeblendete Personen bekommen keine Storys.
/// </summary>
public sealed class StoryIndexServiceTests
{
    [Fact]
    public async Task Volle_Erkennung_legt_Ereignisse_Reise_und_Tagesabschnitte_an()
    {
        using var w = await StoryWorld.CreateAsync();
        w.Add(StoryWorld.Burst(StoryWorld.Utc(2025, 7, 10, 9), 20, TestPlaces.Graz));
        var trip = w.Add(StoryWorld.Burst(StoryWorld.Utc(2025, 7, 12, 9), 10, TestPlaces.Grado)
            .Concat(StoryWorld.Burst(StoryWorld.Utc(2025, 7, 13, 9), 10, TestPlaces.Triest, videos: 2)));
        w.Add(StoryWorld.Burst(StoryWorld.Utc(2025, 7, 20, 9), 5, TestPlaces.Graz)); // zu klein

        var report = await w.Index.RunFullAsync(await w.GroupIdAsync());
        report.Inserted.Should().Be(2);
        var all = w.All();
        all.Select(s => s.Kind).Should().Equal(StoryKind.Event, StoryKind.Trip);
        var tripId = all[1].Id;
        w.Items(tripId).Should().BeEquivalentTo(trip);
        w.Scalar("SELECT COUNT(DISTINCT Day) FROM StoryItems WHERE StoryId = $s", ("$s", tripId)).Should().Be(2, "Tagesabschnitte je Kalendertag");
        w.Scalar("SELECT VideoCount FROM Stories WHERE Id = $s", ("$s", tripId)).Should().Be(2);
        w.Scalar("SELECT FileCount FROM Stories WHERE Id = $s", ("$s", tripId)).Should().Be(20);
        w.Scalar("SELECT COUNT(*) FROM Stories WHERE CoverFileId IS NULL").Should().Be(0, "jede Story bekommt ein Titelbild");
        w.Scalar("SELECT COUNT(*) FROM Stories WHERE IsSeen = 0").Should().Be(0, "die erste Erkennung zählt nicht als „neu“");

        (await w.Index.RunFullAsync(await w.GroupIdAsync())).Should().Match<StoryIndexReport>(r => r.Inserted == 0 && r.Changed == 0 && r.Deleted == 0,
            "eine zweite Berechnung ohne Änderung ändert nichts");
    }

    [Fact]
    public async Task Neuberechnung_nach_neuen_Fotos_am_Rand_behaelt_Benutzer_Zustand()
    {
        using var w = await StoryWorld.CreateAsync();
        var group = await w.GroupIdAsync();
        var first = w.Add(StoryWorld.Burst(StoryWorld.Utc(2025, 9, 6, 12), 20, TestPlaces.Graz));
        w.Add(StoryWorld.Burst(StoryWorld.Utc(2024, 1, 1, 12), 15, TestPlaces.Graz)); // weit weg, darf unberührt bleiben
        await w.Index.RunFullAsync(group);
        var story = w.All().Single(s => w.Items(s.Id).Contains(first[0]));
        var oldSignature = story.Signature;
        var updatedBefore = w.Scalar("SELECT UpdatedAtUtc FROM Stories WHERE Signature LIKE 'E:%' AND Id <> $s", ("$s", story.Id));

        await w.Stories.SetPinnedAsync(story.Id, true);
        await w.Stories.SetCustomTitleAsync(story.Id, "  Hochzeit  ");
        await w.Stories.SetCoverAsync(story.Id, first[5]);
        await w.Stories.SetHiddenAsync(story.Id, true);

        // Neue Fotos davor (gleicher Nachmittag) und danach: Anfang verschiebt sich, Signatur ändert sich.
        var edge = w.Add(StoryWorld.Burst(StoryWorld.Utc(2025, 9, 6, 9), 6, TestPlaces.Graz, TimeSpan.FromMinutes(30))
            .Concat(StoryWorld.Burst(StoryWorld.Utc(2025, 9, 6, 16), 3, TestPlaces.Graz)));
        var report = await w.Index.RunIncrementalAsync(group, edge);
        report.Files.Should().BeLessThan(35, "inkrementell liest nur das Fenster um die neuen Fotos");

        var after = w.All().Single(s => s.Id == story.Id);
        after.Signature.Should().NotBe(oldSignature, "der Anfang hat sich verschoben");
        w.Items(story.Id).Should().HaveCount(29);
        var card = (await w.Stories.GetHiddenAsync()).Single(c => c.Id == story.Id);
        card.IsPinned.Should().BeTrue();
        card.Title.Should().Be("Hochzeit");
        card.CoverFileId.Should().Be(first[5]);
        card.CoverIsManual.Should().BeTrue();
        w.Scalar("SELECT UpdatedAtUtc FROM Stories WHERE Signature LIKE 'E:%' AND Id <> $s", ("$s", story.Id)).Should().Be(updatedBefore);

        // Volle Neuberechnung mit kürzerer Pause trennt den Abend ab: der Zustand bleibt.
        await w.Settings.SaveAsync(StorySettings.Default with { PauseHours = 1 });
        await w.Index.RunFullAsync(group);
        w.Items(story.Id).Should().HaveCount(26, "mit 1 h Pause fallen die drei Fotos am Abend heraus (zu wenige für eine eigene Story)");
        (await w.Stories.GetAsync(story.Id)).Should().NotBeNull("die Story mit Zustand wird wiedergefunden")
            .And.Match<StoryCard>(c => c.IsPinned && c.Title == "Hochzeit" && c.IsHidden);
    }

    [Fact]
    public async Task Personen_Storys_nach_Schwelle_ausgeblendete_Personen_nicht()
    {
        using var w = await StoryWorld.CreateAsync();
        var group = await w.GroupIdAsync();
        var y2019 = w.Add(Enumerable.Range(0, 32).Select(i => new StoryFile(StoryWorld.Utc(2019, 1 + i % 12, 1 + i % 27))));
        var few = w.Add(Enumerable.Range(0, 20).Select(i => new StoryFile(StoryWorld.Utc(2020, 1 + i % 12, 2))));
        var anna = await w.AddPersonAsync("Anna", y2019);
        await w.AddPersonAsync("Max", few);
        var hidden = await w.AddPersonAsync("Versteckt", y2019, hidden: true);

        await w.Index.RunFullAsync(group);
        var persons = w.All().Where(s => s.Kind == StoryKind.Person).ToList();
        persons.Should().ContainSingle().Which.Signature.Should().Be($"P:{anna}:2019-2019");
        var card = await w.Stories.GetAsync(persons[0].Id);
        card!.Title.Should().Be("Anna · 2019");
        card.Subtitle.Should().Be("32 Fotos mit Anna");
        w.Scalar("SELECT COUNT(*) FROM Stories WHERE PersonId = $p", ("$p", hidden)).Should().Be(0);

        // Person ausblenden → ihre Story verschwindet bei der nächsten Berechnung
        w.Execute("UPDATE Persons SET IsHidden = 1 WHERE Id = $p", ("$p", anna));
        await w.Index.RunFullAsync(group);
        w.All().Where(s => s.Kind == StoryKind.Person).Should().BeEmpty();
    }

    [Fact]
    public async Task Orte_Storys_und_zu_Hause_automatisch()
    {
        using var w = await StoryWorld.CreateAsync();
        var group = await w.GroupIdAsync();
        foreach (var year in new[] { 2014, 2015, 2016, 2017 })
        {
            w.Add(StoryWorld.Burst(StoryWorld.Utc(year, 3, 1), 30, TestPlaces.Graz)); // häufigster Ort = zu Hause
        }

        foreach (var year in new[] { 2014, 2019, 2025 })
        {
            w.Add(StoryWorld.Burst(StoryWorld.Utc(year, 7, 1), 5, TestPlaces.Grado));
        }

        await w.Index.RunFullAsync(group);
        var place = w.All().Single(s => s.Kind == StoryKind.Place);
        place.Signature.Should().Be("O:" + TestPlaces.Grado);
        var card = (await w.Stories.GetAsync(place.Id))!;
        card.Title.Should().Be("Grado · 2014–2025");
        card.Subtitle.Should().Be("3 Besuche · Italien");
        (await w.Stories.GetOverviewAsync()).Places.Should().ContainSingle(c => c.Id == place.Id);
    }

    [Fact]
    public async Task Hintergrund_Dienst_rechnet_nach_Nachrichten_nur_das_Betroffene_nach()
    {
        using var w = await StoryWorld.CreateAsync();
        var ids = w.Add(StoryWorld.Burst(StoryWorld.Utc(2025, 5, 1, 9), 14, TestPlaces.Graz));
        w.Index.RequestFiles(w.LibraryId, ids);
        var report = await w.Index.RunPendingAsync();
        report.Inserted.Should().Be(1);

        var more = w.Add(StoryWorld.Burst(StoryWorld.Utc(2025, 5, 1, 11), 3, TestPlaces.Graz));
        w.Index.RequestFiles(w.LibraryId, more);
        (await w.Index.RunPendingAsync()).Changed.Should().Be(1);
        w.Items(w.All().Single().Id).Should().HaveCount(17);
    }
}
