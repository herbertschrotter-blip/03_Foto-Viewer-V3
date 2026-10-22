using Lumora.Domain.Entities;
using Lumora.Services.Assistant;
using Lumora.Services.Stories;
using Lumora.Tests.Stories;

namespace Lumora.Tests.Assistant;

/// <summary>
/// Auflösung in Lumora (ADR-0063): Personen über Namen/Beziehungen/Aliase mit Rückfrage bei Mehrdeutigkeit, Zeiträume
/// relativ zum Geburtsdatum und wiederkehrende Tage, Begriffe gegen Tags/Kategorien/Motive/Orte (Mehrdeutigkeit mit
/// Zahlen, „nicht gefunden“), Urlaub über die Reise-Erkennung samt Zuhause-Rückfrage, „die besten N“.
/// </summary>
public sealed class StoryIntentResolverTests
{
    private static readonly DateOnly Today = AssistantWorld.Today;

    private static async Task<AssistantResolution> ResolveAsync(AssistantWorld a, StoryIntent intent, AssistantAnswers? answers = null)
        => await a.Resolver.ResolveAsync(intent, await a.W.Scope.GetCurrentAsync(), await a.VocabularyAsync(), Today, answers);

    [Fact]
    public async Task Name_und_Beziehung_werden_eindeutig_aufgeloest()
    {
        using var a = await AssistantWorld.CreateAsync();
        var files = a.W.Add(StoryWorld.Burst(StoryWorld.Utc(2020, 5, 1), 4));
        var anna = await a.PersonAsync("Anna", files, relation: "Schwester");
        var franz = await a.PersonAsync("Franz", files.Take(2), relation: "Vater");
        var me = await a.PersonAsync("Herbert", files.Take(1), isMe: true);

        var r = await ResolveAsync(a, new StoryIntent
        {
            Persons = [new IntentPerson(Relation: "meine Schwester"), new IntentPerson(Relation: "Papa"), new IntentPerson(IsMe: true), new IntentPerson("anna")],
        });

        r.Questions.Should().BeEmpty();
        r.Plan.Persons.Select(p => p.Id).Should().Equal(anna, franz, me);
        r.Plan.Persons[0].FileCount.Should().Be(4);
    }

    [Fact]
    public async Task Mehrdeutige_Beziehung_fragt_nach_und_die_Antwort_wird_gemerkt()
    {
        using var a = await AssistantWorld.CreateAsync();
        var files = a.W.Add(StoryWorld.Burst(StoryWorld.Utc(2020, 12, 25), 6));
        var maria = await a.PersonAsync("Maria", files.Take(4), relation: "Oma (mütterlicherseits)");
        var theresia = await a.PersonAsync("Theresia", files.Skip(4), relation: "Oma (väterlicherseits)");
        var intent = new StoryIntent { Persons = [new IntentPerson(Relation: "Oma")], EveryYearFrom = new MonthDay(12, 24), EveryYearTo = new MonthDay(12, 26) };

        var first = await ResolveAsync(a, intent);

        var question = first.Question!;
        question.Kind.Should().Be(QuestionKind.Person);
        question.Title.Should().Be("Wer ist „Oma“?");
        question.Choices.Select(c => c.Label).Should().StartWith(["Maria", "Theresia"]).And.Contain("Beide").And.Contain("Ohne diese Person");
        question.Choices[0].Detail.Should().Be("Oma (mütterlicherseits) · 4 Fotos");
        first.Plan.Time!.Description.Should().Be("jedes Jahr 24.–26. Dezember");

        // Antwort „Beide“ → als Alias gemerkt, der nächste Satz fragt nicht mehr
        await a.Assistant.AnswerAsync(question, question.Choices.Single(c => c.Label == "Beide"), new AssistantAnswers());
        var second = await ResolveAsync(a, intent);
        second.Questions.Should().BeEmpty();
        second.Plan.Persons.Select(p => p.Id).Should().BeEquivalentTo([maria, theresia]);
        (await a.Store.GetAliasesAsync(await a.GroupAsync())).Should().ContainSingle().Which.Term.Should().Be("oma");

        // Vergessen → wieder Rückfrage; „Ohne diese Person“ nur für diese Anfrage
        await a.Store.DeleteAliasAsync(await a.GroupAsync(), "Oma");
        var answers = new AssistantAnswers();
        await a.Assistant.AnswerAsync(question, question.Choices.Single(c => c.Value == "skip"), answers);
        var third = await ResolveAsync(a, intent, answers);
        third.Questions.Should().BeEmpty();
        third.Plan.Persons.Should().BeEmpty();
    }

    [Fact]
    public async Task Unbekannte_Person_fragt_statt_zu_raten()
    {
        using var a = await AssistantWorld.CreateAsync();
        var files = a.W.Add(StoryWorld.Burst(StoryWorld.Utc(2020, 5, 1), 3));
        await a.PersonAsync("Anna", files);

        var r = await ResolveAsync(a, new StoryIntent { Persons = [new IntentPerson("Kevin")] });

        r.Plan.Persons.Should().BeEmpty();
        r.Question!.Title.Should().Be("Wer ist „Kevin“?");
        r.Question.Text.Should().Contain("kenne ich in Ihren Personen nicht");
        r.Question.Choices.Select(c => c.Label).Should().Contain("Anna").And.Contain("Ohne diese Person");
    }

    [Fact]
    public async Task Zeitraum_aus_dem_Geburtsdatum()
    {
        using var a = await AssistantWorld.CreateAsync();
        var files = a.W.Add(StoryWorld.Burst(StoryWorld.Utc(2000, 5, 1), 2));
        await a.PersonAsync("Anna", files, relation: "Schwester", birth: PersonBirthDate.Create(1985, 3, 12, 2026));
        await a.PersonAsync("Max", files, birth: PersonBirthDate.Create(1990, null, null, 2026));
        await a.PersonAsync("Lea", files);

        var anna = await ResolveAsync(a, new StoryIntent { Persons = [new IntentPerson(Relation: "Schwester")], AgeFrom = 0, AgeTo = 20 });
        anna.Plan.Time!.From.Should().Be(new DateOnly(1985, 3, 12));
        anna.Plan.Time.To.Should().Be(new DateOnly(2006, 3, 11), "bis einschließlich 20 Jahre = bis zum Tag vor dem 21. Geburtstag");
        anna.Plan.Time.Source.Should().Be("Geburt bis 20 Jahre (aus dem Geburtsdatum von Anna)");

        var max = await ResolveAsync(a, new StoryIntent { Persons = [new IntentPerson("Max")], AgeFrom = 13, AgeTo = 19 });
        max.Plan.Time!.From.Should().Be(new DateOnly(2003, 1, 1), "nur das Jahr bekannt: frühester möglicher Tag");
        max.Plan.Time.To.Should().Be(new DateOnly(2010, 12, 30));

        var lea = await ResolveAsync(a, new StoryIntent { Persons = [new IntentPerson("Lea")], AgeTo = 10 });
        lea.Question!.Kind.Should().Be(QuestionKind.BirthDate);
        lea.Plan.Time.Should().BeNull("ohne Geburtsdatum kein geratener Zeitraum");
    }

    [Fact]
    public async Task Letzte_Jahre_relativ_zu_heute_und_wiederkehrende_Tage()
    {
        using var a = await AssistantWorld.CreateAsync();
        var christmas = a.W.Add([new StoryFile(StoryWorld.Utc(2019, 12, 25)), new StoryFile(StoryWorld.Utc(2023, 12, 24, 20)), new StoryFile(StoryWorld.Utc(2023, 12, 26, 23))]);
        var other = a.W.Add([new StoryFile(StoryWorld.Utc(2023, 12, 27)), new StoryFile(StoryWorld.Utc(2021, 10, 21)), new StoryFile(StoryWorld.Utc(2021, 10, 22))]);

        var last = await ResolveAsync(a, new StoryIntent { LastYears = 5 });
        last.Plan.Time!.From.Should().Be(new DateOnly(2021, 10, 22));
        last.Plan.Time.To.Should().Be(Today);
        last.Plan.Time.Description.Should().Be("22.10.2021 – 21.10.2026");
        var lastResult = await a.Assistant.EvaluateAsync(last.Plan);
        lastResult.FileIds.Should().BeEquivalentTo([christmas[1], christmas[2], other[0], other[2]], "21.10.2021 liegt vor dem Zeitraum");

        var every = await ResolveAsync(a, new StoryIntent { EveryYearFrom = new MonthDay(12, 24), EveryYearTo = new MonthDay(12, 26) });
        (await a.Assistant.EvaluateAsync(every.Plan)).FileIds.Should().BeEquivalentTo(christmas);

        var newYear = new PlanTime(null, null, new MonthDay(12, 31), new MonthDay(1, 1), "x", null);
        newYear.Contains(new DateOnly(2020, 12, 31)).Should().BeTrue();
        newYear.Contains(new DateOnly(2021, 1, 1)).Should().BeTrue();
        newYear.Contains(new DateOnly(2021, 1, 2)).Should().BeFalse();
    }

    [Fact]
    public async Task Begriffe_treffen_Tag_Kategorie_Motiv_und_Ort_mit_Zahlen()
    {
        using var a = await AssistantWorld.CreateAsync();
        var grado = a.W.Add(StoryWorld.Burst(StoryWorld.Utc(2024, 7, 1), 5, TestPlaces.Grado));
        var home = a.W.Add(StoryWorld.Burst(StoryWorld.Utc(2024, 8, 1), 4, TestPlaces.Graz));
        await a.TagAsync("Grado", home.Take(2));          // Tag gleichen Namens wie der Ort
        await a.CategoryAsync("Anlass", "Hochzeit", home.Skip(2));
        await a.MotifAsync("Sonnenuntergang", grado.Take(3));
        await a.CategoryAsync("Reisen", "Italien", grado);

        var r = await ResolveAsync(a, new StoryIntent { Terms = ["Grado", "Hochzeit", "Sonnenuntergänge", "Barcelona"] });

        r.Plan.Terms.Should().HaveCount(4);
        var gradoTerm = r.Plan.Terms[0];
        gradoTerm.Matches.Select(m => m.Text).Should().Equal("Tag (2)", "Ort (5)");
        gradoTerm.Matches.Single(m => m.Kind == TermMatchKind.Place).Label.Should().Be("Grado, Italien");
        r.Plan.Terms[1].Matches.Should().ContainSingle().Which.Should().Match<TermMatch>(m => m.Kind == TermMatchKind.CategoryValue && m.Count == 2 && m.Label == "Anlass: Hochzeit");
        r.Plan.Terms[2].Matches.Should().ContainSingle().Which.Kind.Should().Be(TermMatchKind.Motif);
        r.Plan.Terms[3].NotFound.Should().BeTrue("nicht gefunden wird gezeigt statt still weggelassen");

        // Standard: Vereinigung der Treffer eines Begriffs; „Grado“ allein = Tag ODER Ort
        var onlyGrado = r.Plan with { Terms = [gradoTerm] };
        (await a.Assistant.EvaluateAsync(onlyGrado)).Count.Should().Be(7);
        var onlyTag = onlyGrado with { Terms = [gradoTerm with { Matches = gradoTerm.Matches.Select(m => m with { Selected = m.Kind == TermMatchKind.Tag }).ToList() }] };
        (await a.Assistant.EvaluateAsync(onlyTag)).Count.Should().Be(2);

        // nicht gefundene Begriffe wirken nicht (sonst gäbe es nie Treffer)
        var withMissing = r.Plan with { Terms = [r.Plan.Terms[2], r.Plan.Terms[3]] };
        (await a.Assistant.EvaluateAsync(withMissing)).Count.Should().Be(3);

        // Land des Bereichs
        var italy = await ResolveAsync(a, new StoryIntent { Terms = ["Italien"] });
        italy.Plan.Terms[0].Matches.Select(m => m.Kind).Should().Contain(TermMatchKind.Country).And.Contain(TermMatchKind.CategoryValue);
    }

    [Fact]
    public async Task Urlaub_ohne_Zuhause_fragt_nach_und_mit_Zuhause_zaehlt_die_Reise_Erkennung()
    {
        using var a = await AssistantWorld.CreateAsync();
        a.W.Add(StoryWorld.Burst(StoryWorld.Utc(2025, 7, 10, 9), 20, TestPlaces.Graz));
        var trip = a.W.Add(StoryWorld.Burst(StoryWorld.Utc(2025, 7, 12, 9), 10, TestPlaces.Grado).Concat(StoryWorld.Burst(StoryWorld.Utc(2025, 7, 13, 9), 10, TestPlaces.Triest)));
        await a.W.Index.RunFullAsync(await a.GroupAsync());
        var reisen = await a.CategoryAsync("Reisen", "Kroatien", trip.Take(1));
        var intent = new StoryIntent { Trip = true };

        var first = await ResolveAsync(a, intent);
        first.Question!.Kind.Should().Be(QuestionKind.Home);
        first.Question.Choices[0].Label.Should().Be("Zuhause ist Graz");
        first.Plan.Trip!.UseDetection.Should().BeFalse("ohne Antwort nicht raten");
        first.Plan.Trip.Extra.Should().ContainSingle().Which.Kind.Should().Be(TermMatchKind.Category, "Kategorie „Reisen“ wird zusätzlich angeboten");

        await a.Assistant.AnswerAsync(first.Question, first.Question.Choices[0], new AssistantAnswers());
        (await a.W.Settings.LoadAsync()).Home!.PlaceId.Should().Be(TestPlaces.Graz, "die Antwort speichert das Zuhause");
        var second = await ResolveAsync(a, intent);
        second.Questions.Should().BeEmpty();
        second.Plan.Trip!.UseDetection.Should().BeTrue();
        second.Plan.Trip.DetectionCount.Should().Be(20);
        (await a.Assistant.EvaluateAsync(second.Plan)).FileIds.Should().BeEquivalentTo(trip);
        reisen.Category.Should().BePositive();
    }
}
