using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Domain.Enums;
using Lumora.Services.Assistant;
using Lumora.Services.Library;
using Lumora.Tests.Stories;

namespace Lumora.Tests.Assistant;

/// <summary>
/// Story-Assistent als Ganzes (ADR-0063): Herberts Beispielsatz, was die Sprach-KI sieht (nur Namen, nie Fotos/Pfade/
/// Geburtsdaten), Rückfall ohne Ollama, „die besten N“ mit Vorschlag, Story/Album mit sachlicher Überschrift, Verlauf,
/// Protokoll ohne Satz, Bereich und Bibliotheks-Trennung inkl. Verknüpfen/Lösen.
/// </summary>
public sealed class StoryAssistantServiceTests
{
    private const string HerbertsSatz = "Erstelle mir ein Album mit den schönsten Urlaubsfotos der letzten fünf Jahre mit Nicole und Nina";

    [Fact]
    public async Task Herberts_Satz_Album_mit_den_schoensten_Urlaubsfotos_der_letzten_fuenf_Jahre_mit_Nicole_und_Nina()
    {
        using var a = await AssistantWorld.CreateAsync();
        // zu Hause in Graz, Reisen 2019 (zu alt), 2023 und 2025; Nicole und Nina gemeinsam bzw. einzeln auf Fotos
        a.W.Add(StoryWorld.Burst(StoryWorld.Utc(2023, 6, 1, 9), 30, TestPlaces.Graz));
        var old = a.W.Add(StoryWorld.Burst(StoryWorld.Utc(2019, 7, 12, 9), 12, TestPlaces.Rovinj).Concat(StoryWorld.Burst(StoryWorld.Utc(2019, 7, 13, 9), 12, TestPlaces.Rovinj)));
        var t2023 = a.W.Add(StoryWorld.Burst(StoryWorld.Utc(2023, 8, 1, 9), 20, TestPlaces.Grado).Concat(StoryWorld.Burst(StoryWorld.Utc(2023, 8, 2, 9), 20, TestPlaces.Triest)));
        var t2025 = a.W.Add(StoryWorld.Burst(StoryWorld.Utc(2025, 7, 12, 9), 30, TestPlaces.Rovinj).Concat(StoryWorld.Burst(StoryWorld.Utc(2025, 7, 13, 9), 30, TestPlaces.Rovinj)));
        var homeWithBoth = a.W.Add(StoryWorld.Burst(StoryWorld.Utc(2024, 3, 1, 9), 5, TestPlaces.Graz));
        var together = t2023.Take(30).Concat(t2025.Take(50)).Concat(old).Concat(homeWithBoth).ToList();
        await a.PersonAsync("Nicole", together.Concat(t2023.Skip(30)));
        await a.PersonAsync("Nina", together);
        await a.SetHomeGrazAsync();
        await a.W.Index.RunFullAsync(await a.GroupAsync());
        a.Model.Answer = _ => new IntentModelResponse("""
            {"action":"album","persons":[{"name":"Nicole","relation":"","me":false},{"name":"Nina","relation":"","me":false}],
             "personMatch":"all","others":"any","terms":["Urlaubsfotos"],"termMatch":"all","trip":true,"dateFrom":"","dateTo":"",
             "lastYears":5,"ageFrom":-1,"ageTo":-1,"agePerson":"","everyYearFrom":"","everyYearTo":"","media":"all","best":0,
             "bestWanted":true,"minRating":0,"favorites":false}
            """);

        var understanding = await a.Assistant.UnderstandAsync(HerbertsSatz);

        understanding.FromRules.Should().BeFalse();
        var plan = understanding.Resolution.Plan;
        understanding.Resolution.Questions.Should().BeEmpty();
        plan.Action.Should().Be(IntentAction.Album);
        plan.Persons.Select(p => p.Name).Should().Equal("Nicole", "Nina");
        plan.PersonMatch.Should().Be(IntentMatch.All, "Standard: alle zusammen auf dem Foto");
        plan.Trip!.UseDetection.Should().BeTrue();
        plan.Terms.Should().BeEmpty("„Urlaubsfotos“ ist die Reise-Erkennung, kein Begriff");
        plan.Time!.Description.Should().Be("22.10.2021 – 21.10.2026", "„letzte fünf Jahre“ als konkreter Zeitraum");
        plan.BestSuggested.Should().BeTrue();

        var result = await a.Assistant.EvaluateAsync(plan);
        result.FileIds.Should().BeEquivalentTo(t2023.Take(30).Concat(t2025.Take(50)), "nur Reisen, nur beide zusammen, nur die letzten fünf Jahre");
        a.Assistant.SuggestBest(result.Count).Should().Be(24, "80 Treffer → 10 % = 8 → mindestens 24");
        var best = await a.Assistant.EvaluateAsync(plan with { Best = 24 });
        best.Selected.Should().HaveCountLessThanOrEqualTo(24).And.OnlyContain(id => result.FileIds.Contains(id));
        best.Years.Should().Be((2023, 2025));
        a.Assistant.BuildTitle(plan, best).Should().Be("Nicole und Nina · Urlaub 2023–2025");

        var mindestensEine = await a.Assistant.EvaluateAsync(plan with { PersonMatch = IntentMatch.Any });
        mindestensEine.Count.Should().Be(t2023.Count + 50, "umschaltbar auf „mindestens eine“");

        var album = await a.Assistant.CreateAlbumAsync(plan, best);
        album.Name.Should().Be("Nicole und Nina · Urlaub 2023–2025");
        (await a.W.Albums.GetFileIdsAsync(album.Id)).Should().BeEquivalentTo(best.Selected);

        // Was die Sprach-KI gesehen hat: nur der Satz, heute, Namen — keine Geburtsdaten, Pfade, Koordinaten
        var prompt = a.Model.Prompts.Single();
        prompt.Sentence.Should().Be(HerbertsSatz);
        prompt.Today.Should().Be(AssistantWorld.Today);
        prompt.Persons.Select(p => p.Name).Should().BeEquivalentTo(["Nicole", "Nina"]);
        var system = IntentPromptBuilder.System(prompt);
        system.Should().NotContain(a.W.Fx.RootDir).And.NotContain("45.6").And.NotContain(".jpg");
    }

    [Fact]
    public async Task Ohne_Ollama_verstehen_Regeln_klare_Saetze_sonst_der_Weg_in_die_Einstellungen()
    {
        using var a = await AssistantWorld.CreateAsync();
        var files = a.W.Add(StoryWorld.Burst(StoryWorld.Utc(2019, 5, 1), 3));
        await a.PersonAsync("Anna", files);
        a.Model.Answer = _ => throw new IntentModelException("Ollama läuft nicht.", IntentModelFailure.ServerUnreachable);

        var simple = await a.Assistant.UnderstandAsync("Anna 2019");
        simple.FromRules.Should().BeTrue();
        (await a.Assistant.EvaluateAsync(simple.Resolution.Plan)).FileIds.Should().BeEquivalentTo(files);

        var act = () => a.Assistant.UnderstandAsync("Weihnachten bei Oma, alle Jahre");
        (await act.Should().ThrowAsync<AssistantException>()).Which.NeedsSetup.Should().BeTrue();

        a.Model.Answer = _ => new IntentModelResponse("{\"action\":\"story\",\"persons\":[{\"name\":\"An", CutOff: true);
        (await a.Assistant.UnderstandAsync("Anna 2019")).FromRules.Should().BeTrue("abgeschnittene Antwort → Regeln, wenn der Satz klar ist");
        await FluentActions.Awaiting(() => a.Assistant.UnderstandAsync("irgendwas Unklares")).Should().ThrowAsync<AssistantException>().WithMessage("*abgeschnitten*");
    }

    [Fact]
    public async Task Story_mit_sachlicher_Ueberschrift_Verlauf_und_kein_Satz_im_Protokoll()
    {
        using var a = await AssistantWorld.CreateAsync();
        var files = a.W.Add(StoryWorld.Burst(StoryWorld.Utc(2019, 5, 1), 4).Concat(StoryWorld.Burst(StoryWorld.Utc(2020, 5, 1), 4)));
        await a.PersonAsync("Anna", files);
        const string sentence = "Geheime Story mit Anna 2019 bis 2020";
        a.Model.Answer = p => new IntentModelResponse(StoryIntentJson.ToJson(new StoryIntent
        {
            Persons = [new IntentPerson("Anna")],
            DateFrom = new DateOnly(2019, 1, 1),
            DateTo = new DateOnly(2020, 12, 31),
        }));

        var u = await a.Assistant.UnderstandAsync(sentence);
        var result = await a.Assistant.EvaluateAsync(u.Resolution.Plan);
        var id = await a.Assistant.CreateStoryAsync(u.Resolution.Plan, result);
        await a.Assistant.RememberAsync(u.Sentence, u.Intent, IntentAction.Story, result.Count);

        var story = await a.W.Stories.GetAsync(id);
        story!.Kind.Should().Be(StoryKind.Custom);
        story.Title.Should().Be("Anna · 2019–2020");
        story.FileCount.Should().Be(8);
        var history = await a.Assistant.GetHistoryAsync();
        history.Should().ContainSingle().Which.Should().Match<AssistantHistoryItem>(h => h.Sentence == sentence && h.ResultCount == 8);
        StoryIntentJson.Parse(history[0].IntentJson, AssistantWorld.Today).Intent!.Persons.Single().Name.Should().Be("Anna");
        a.Logger.Lines.Should().Contain(l => l.Contains("8 Treffer")).And.NotContain(l => l.Contains("Geheime") || l.Contains("Anna"));

        // „Verlauf merken“ aus → nichts gemerkt
        await a.Assistant.ClearHistoryAsync();
        await a.Settings.SaveAsync(AssistantSettings.Default with { RememberHistory = false });
        await a.Assistant.RememberAsync(u.Sentence, u.Intent, IntentAction.Story, 8);
        (await a.Assistant.GetHistoryAsync()).Should().BeEmpty();
        (await a.Settings.LoadAsync()).RememberHistory.Should().BeFalse();
    }

    [Fact]
    public async Task Bereich_Tags_Personen_und_Dateien_anderer_Bibliotheken_sind_unsichtbar_bis_zum_Verknuepfen()
    {
        using var a = await AssistantWorld.CreateAsync();
        var libraries = a.W.Fx.CreateLibraryService();
        using var linking = new LibraryLinkService(a.W.Fx.ContextFactory, a.W.Catalog, NullLogger<LibraryLinkService>.Instance, a.W.Fx.Messenger);
        var libA = a.W.LibraryId;
        var libB = (await libraries.CreateAsync("Zweite", a.W.Fx.OutsideDir)).Id;
        var groupB = await a.W.GroupIdAsync(libB);
        var inA = a.W.Add(StoryWorld.Burst(StoryWorld.Utc(2022, 5, 1), 3), libA);
        var inB = a.W.Add(StoryWorld.Burst(StoryWorld.Utc(2022, 5, 1), 4), libB);
        await a.TagAsync("Hund", inA);
        await a.TagAsync("Hund", inB, groupB);
        var lea = await a.PersonAsync("Lea", inB, groupId: groupB);
        await a.Store.SetAliasAsync(groupB, "Tochter", [lea]);
        await a.Store.AddHistoryAsync(groupB, "Satz in B", new StoryIntent(), IntentAction.Story, 4);

        await libraries.SetActiveAsync(libA);
        var vocabularyA = await a.VocabularyAsync();
        vocabularyA.Persons.Should().BeEmpty("Lea gehört zu B");
        vocabularyA.Aliases.Should().BeEmpty();
        (await a.Assistant.GetHistoryAsync()).Should().BeEmpty("der Verlauf gehört zur Gruppe von B");
        var hund = await a.Assistant.MatchTermAsync("Hund");
        hund.Matches.Should().ContainSingle().Which.Count.Should().Be(3, "nur Dateien und Tag des Bereichs");
        (await a.Assistant.EvaluateAsync(new AssistantPlan { Terms = [hund] })).FileIds.Should().BeEquivalentTo(inA);

        // Verknüpfen: gemerkte Antwort und Verlauf wandern mit, „Alle verknüpften“ zählt die Gruppe
        await linking.LinkAsync(libA, libB);
        var group = await a.W.GroupIdAsync(libA);
        (await a.Store.GetAliasesAsync(group)).Should().ContainSingle().Which.Term.Should().Be("tochter");
        (await a.Store.GetHistoryAsync(group)).Should().ContainSingle();
        await a.W.Scope.ShowAllLinkedAsync(group);
        (await a.Assistant.EvaluateAsync(new AssistantPlan { Terms = [await a.Assistant.MatchTermAsync("Hund")] })).Count.Should().Be(7);

        // Lösen: B bekommt Kopien (Antwort zeigt auf die kopierte Person)
        await linking.UnlinkAsync(libB);
        var newGroupB = await a.W.GroupIdAsync(libB);
        newGroupB.Should().NotBe(group);
        var copied = await a.Store.GetAliasesAsync(newGroupB);
        copied.Should().ContainSingle().Which.PersonIds.Single().Should().NotBe(lea, "die Kopie zeigt auf die kopierte Person");
        (await a.Store.GetHistoryAsync(newGroupB)).Should().ContainSingle();
        (await a.Store.GetAliasesAsync(group)).Should().ContainSingle("A behält seine");
    }

    [Fact]
    public async Task Versteckter_Katalog_hat_eigenen_Wortschatz_Verlauf_und_eigene_Antworten()
    {
        using var normal = await AssistantWorld.CreateAsync();
        using var hidden = await AssistantWorld.CreateAsync(); // eigener Katalog je Sitzung (ADR-0049)
        hidden.W.Catalog.SetHidden(true);
        var secret = hidden.W.Add(StoryWorld.Burst(StoryWorld.Utc(2022, 5, 1), 3));
        await hidden.TagAsync("Geheimtag", secret);
        await hidden.CategoryAsync("Geheimkategorie", "Wert", secret);
        var person = await hidden.PersonAsync("Verborgen", secret);
        await hidden.Store.SetAliasAsync(await hidden.GroupAsync(), "oma", [person]);
        hidden.Model.Answer = _ => new IntentModelResponse(StoryIntentJson.ToJson(new StoryIntent { Terms = ["Geheimtag"] }));
        var u = await hidden.Assistant.UnderstandAsync("Fotos mit Geheimtag");
        await hidden.Assistant.RememberAsync(u.Sentence, u.Intent, IntentAction.Story, 3);

        var promptNames = hidden.Model.Prompts.Single().Terms;
        promptNames.Should().Contain(["Geheimtag", "Geheimkategorie", "Wert"], "die Sprach-KI bekommt die Namen der Tags und Kategorien des versteckten Katalogs");
        u.Resolution.Plan.Terms.Single().Matches.Single().Should().Match<TermMatch>(m => m.Kind == TermMatchKind.Tag && m.Count == 3);
        (await hidden.Assistant.GetHistoryAsync()).Should().ContainSingle();

        var visible = await normal.VocabularyAsync();
        visible.Tags.Should().BeEmpty();
        visible.Categories.Should().BeEmpty();
        visible.Persons.Should().BeEmpty();
        visible.Aliases.Should().BeEmpty();
        (await normal.Assistant.GetHistoryAsync()).Should().BeEmpty("Verlauf und Antworten liegen nur im versteckten Katalog");
        normal.Model.Answer = _ => new IntentModelResponse(StoryIntentJson.ToJson(new StoryIntent { Terms = ["Geheimtag"] }));
        (await normal.Assistant.UnderstandAsync("Fotos mit Geheimtag")).Resolution.Plan.Terms.Single().NotFound.Should().BeTrue();
    }

    [Fact]
    public async Task Verlauf_wird_begrenzt_und_gleiche_Saetze_nur_aufgefrischt()
    {
        using var a = await AssistantWorld.CreateAsync();
        var group = await a.GroupAsync();
        for (var i = 0; i < AssistantStore.MaxHistory + 5; i++)
        {
            await a.Store.AddHistoryAsync(group, "Satz " + i, new StoryIntent(), IntentAction.Story, i);
        }

        await a.Store.AddHistoryAsync(group, "Satz 54", new StoryIntent { Action = IntentAction.Slideshow }, IntentAction.Slideshow, 99);
        a.W.Scalar("SELECT COUNT(*) FROM AssistantHistory").Should().Be(AssistantStore.MaxHistory);
        var latest = await a.Store.GetHistoryAsync(group, 3);
        latest.Should().HaveCount(3);
        latest.Should().ContainSingle(h => h.Sentence == "Satz 54").Which.Should().Match<AssistantHistoryItem>(h => h.ResultCount == 99 && h.Action == IntentAction.Slideshow);
        (await a.Store.ClearHistoryAsync(group)).Should().Be(AssistantStore.MaxHistory);
    }
}
