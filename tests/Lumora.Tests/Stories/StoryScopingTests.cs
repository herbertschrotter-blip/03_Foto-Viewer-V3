using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Services.Library;
using Lumora.Services.Stories;

namespace Lumora.Tests.Stories;

/// <summary>
/// Bibliotheks-Trennung der Storys (ADR-0058 nach ADR-0048): Storys gehören der Gruppe, eine einzeln geöffnete
/// Bibliothek zeigt/zählt nur ihre Dateien, „Alle verknüpften“ die ganze Gruppe; Verknüpfen führt Storys gleicher
/// Signatur zusammen, Lösen kopiert sie samt Zustand, eine leere Gruppe nimmt ihre Storys mit.
/// </summary>
public sealed class StoryScopingTests
{
    [Fact]
    public async Task Storys_getrennt_verknuepft_zusammengefuehrt_und_beim_Loesen_kopiert()
    {
        using var w = await StoryWorld.CreateAsync();
        var libraries = w.Fx.CreateLibraryService();
        using var linking = new LibraryLinkService(w.Fx.ContextFactory, w.Catalog, NullLogger<LibraryLinkService>.Instance, w.Fx.Messenger);
        var a = w.LibraryId;
        var b = (await libraries.CreateAsync("Zweite", w.Fx.OutsideDir)).Id;
        var groupA = await w.GroupIdAsync(a);
        var groupB = await w.GroupIdAsync(b);
        groupB.Should().NotBe(groupA);

        var julyA = w.Add(StoryWorld.Burst(StoryWorld.Utc(2025, 7, 12, 9), 14), a);
        var julyB = w.Add(StoryWorld.Burst(StoryWorld.Utc(2025, 7, 12, 9), 13), b); // gleicher Anfang = gleiche Signatur
        w.Add(StoryWorld.Burst(StoryWorld.Utc(2024, 2, 2, 9), 15), b);
        await w.Index.RunAllAsync();

        await libraries.SetActiveAsync(a);
        (await w.Stories.GetOverviewAsync()).EventsAndTrips.Should().ContainSingle().Which.FileCount.Should().Be(14);
        await libraries.SetActiveAsync(b);
        var inB = (await w.Stories.GetOverviewAsync()).EventsAndTrips;
        inB.Should().HaveCount(2, "Storys aus A sind in B unsichtbar");
        var julyStoryB = inB.Single(c => c.FirstDay.Month == 7).Id;
        await w.Stories.SetPinnedAsync(julyStoryB, true);
        await w.Stories.SetCustomTitleAsync(julyStoryB, "Sommerfest");
        (await w.Stories.GetAsync(w.All(groupA).Single().Id)).Should().BeNull("eine Story aus A gibt es in B nicht");

        // Verknüpfen: A nimmt B auf, gleiche Signatur → eine Story mit den Dateien beider Seiten und dem Zustand von B
        await linking.LinkAsync(a, b);
        var group = await w.GroupIdAsync(a);
        w.All(groupB).Should().BeEmpty();
        var merged = w.All(group);
        merged.Should().HaveCount(2);
        var july = merged.Single(s => w.Items(s.Id).Contains(julyA[0])).Id;
        w.Items(july).Should().BeEquivalentTo(julyA.Concat(julyB));
        await w.Index.RunFullAsync(group);
        w.All(group).Should().HaveCount(2, "die Neuberechnung findet die zusammengeführten Storys wieder");

        await libraries.SetActiveAsync(a);
        var single = await w.Stories.GetOverviewAsync();
        single.EventsAndTrips.Should().ContainSingle("einzeln geöffnet nur Storys mit Dateien aus A").Which
            .Should().Match<StoryCard>(c => c.Id == july && c.FileCount == 14 && c.IsPinned && c.Title == "Sommerfest");
        await w.Scope.ShowAllLinkedAsync(group);
        var all = await w.Stories.GetOverviewAsync();
        all.EventsAndTrips.Should().HaveCount(2);
        all.EventsAndTrips.Single(c => c.Id == july).FileCount.Should().Be(27, "„Alle verknüpften“ zählt die ganze Gruppe");

        // Lösen: B bekommt Kopien (mit Zustand) samt ihren Dateien, A behält seine
        await linking.UnlinkAsync(b);
        var newGroupB = await w.GroupIdAsync(b);
        newGroupB.Should().NotBe(group);
        w.Items(july).Should().BeEquivalentTo(julyA);
        var copies = w.All(newGroupB);
        copies.Should().HaveCount(2);
        var julyCopy = copies.Single(s => w.Items(s.Id).Contains(julyB[0])).Id;
        w.Items(julyCopy).Should().BeEquivalentTo(julyB);
        await libraries.SetActiveAsync(b);
        (await w.Stories.GetAsync(julyCopy)).Should().Match<StoryCard>(c => c.IsPinned && c.Title == "Sommerfest");
        w.All(group).Should().ContainSingle("die Story nur aus B ist in A weg (leer)");
        await w.Index.RunAllAsync();
        w.All(newGroupB).Should().HaveCount(2);

        // B entfernen: ihre Gruppe ist leer → ihre Storys gehen mit, A bleibt unberührt
        await libraries.DeleteAsync(b);
        w.All(newGroupB).Should().BeEmpty();
        w.All(group).Should().ContainSingle();
    }

    [Fact]
    public async Task Verknuepfen_mit_gleichnamiger_Person_behaelt_den_Zustand_ihrer_Personen_Story()
    {
        using var w = await StoryWorld.CreateAsync();
        var libraries = w.Fx.CreateLibraryService();
        using var linking = new LibraryLinkService(w.Fx.ContextFactory, w.Catalog, NullLogger<LibraryLinkService>.Instance, w.Fx.Messenger);
        var a = w.LibraryId;
        var b = (await libraries.CreateAsync("Zweite", w.Fx.OutsideDir)).Id;
        var inA = w.Add(Enumerable.Range(0, 31).Select(i => new StoryFile(StoryWorld.Utc(2019, 1 + i % 12, 1 + i % 25))), a);
        var inB = w.Add(Enumerable.Range(0, 31).Select(i => new StoryFile(StoryWorld.Utc(2019, 1 + i % 12, 2 + i % 25, 14))), b);
        await w.AddPersonAsync("Anna", inA, groupId: await w.GroupIdAsync(a));
        await w.AddPersonAsync("Anna", inB, groupId: await w.GroupIdAsync(b));
        await w.Index.RunAllAsync();
        await libraries.SetActiveAsync(b);
        var storyB = (await w.Stories.GetOverviewAsync()).Persons.Should().ContainSingle().Subject;
        await w.Stories.SetPinnedAsync(storyB.Id, true);
        await w.Stories.SetCustomTitleAsync(storyB.Id, "Anna im Jahr 2019");

        await linking.LinkAsync(a, b); // gleichnamige Personen werden eine
        var group = await w.GroupIdAsync(a);
        await w.Index.RunFullAsync(group);
        await w.Scope.ShowAllLinkedAsync(group);
        var merged = (await w.Stories.GetOverviewAsync()).Persons.Should().ContainSingle().Subject;
        merged.Should().Match<StoryCard>(c => c.IsPinned && c.Title == "Anna im Jahr 2019" && c.FileCount == 62,
            "die Personen-Story der zusammengeführten Person geht nicht per Kaskade verloren");
    }
}
