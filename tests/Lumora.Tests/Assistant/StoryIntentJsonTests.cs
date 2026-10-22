using System.Text.Json;
using Lumora.Services.Assistant;

namespace Lumora.Tests.Assistant;

/// <summary>
/// Drahtformat der Absicht (ADR-0063): Schema, strikte Prüfung (gültig, unbekannte Felder, falsche Typen/Werte,
/// abgeschnitten), Rückweg, Text-Vergleich und der regelbasierte Rückfall für klare Sätze.
/// </summary>
public sealed class StoryIntentJsonTests
{
    private static readonly DateOnly Today = new(2026, 10, 21);

    private const string Herbert = """
        {"action":"album","persons":[{"name":"Nicole","relation":"","me":false},{"name":"Nina","relation":"","me":false}],
         "personMatch":"all","others":"any","terms":[],"termMatch":"all","trip":true,"dateFrom":"","dateTo":"","lastYears":5,
         "ageFrom":-1,"ageTo":-1,"agePerson":"","everyYearFrom":"","everyYearTo":"","media":"all","best":0,"bestWanted":true,
         "minRating":0,"favorites":false}
        """;

    [Fact]
    public void Gueltige_Antwort_fuer_Herberts_Satz()
    {
        var result = StoryIntentJson.Parse(Herbert, Today);

        result.IsValid.Should().BeTrue(result.Error);
        var intent = result.Intent!;
        intent.Action.Should().Be(IntentAction.Album);
        intent.Persons.Select(p => p.Name).Should().Equal("Nicole", "Nina");
        intent.PersonMatch.Should().Be(IntentMatch.All);
        intent.Trip.Should().BeTrue();
        intent.LastYears.Should().Be(5);
        intent.BestWanted.Should().BeTrue();
        intent.Best.Should().BeNull("„die schönsten“ ohne Zahl");
        intent.HasAge.Should().BeFalse();
    }

    [Theory]
    [InlineData("""{"action":"story","titel":"Schöner Sommer"}""", "unbekanntes Feld")]
    [InlineData("""{"action":"poster"}""", "keinen erlaubten Wert")]
    [InlineData("""{"persons":[]}""", "„action“ fehlt")]
    [InlineData("""{"action":"story","best":"viele"}""", "keine ganze Zahl")]
    [InlineData("""{"action":"story","ageFrom":5,"ageTo":2}""", "größer")]
    [InlineData("""{"action":"story","dateFrom":"22.10.2021"}""", "JJJJ-MM-TT")]
    [InlineData("""{"action":"story","dateFrom":"2030-01-01"}""", "außerhalb")]
    [InlineData("""{"action":"story","everyYearFrom":"13-40","everyYearTo":"12-26"}""", "MM-TT")]
    [InlineData("""{"action":"story","persons":[{"name":"A","age":3}]}""", "persons.age")]
    [InlineData("""{"action":"story","media":"bilder"}""", "keinen erlaubten Wert")]
    [InlineData("""[1,2]""", "kein Objekt")]
    public void Ungueltige_Antworten_werden_abgelehnt(string json, string error)
    {
        var result = StoryIntentJson.Parse(json, Today);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain(error);
        result.Truncated.Should().BeFalse();
    }

    [Fact]
    public void Abgeschnittene_Antwort_wird_erkannt()
    {
        var cut = Herbert[..80];

        var result = StoryIntentJson.Parse(cut, Today);
        result.IsValid.Should().BeFalse();
        result.Truncated.Should().BeTrue();
        result.Error.Should().Contain("abgeschnitten");

        StoryIntentJson.Parse("{\"action\":\"story\"}", Today, cutOff: false).IsValid.Should().BeTrue();
        StoryIntentJson.Parse("", Today).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Fehlende_Felder_und_null_gelten_als_keine_Angabe()
    {
        var result = StoryIntentJson.Parse("""{"action":"collage","persons":null,"best":null,"terms":["Meer"]}""", Today);

        result.IsValid.Should().BeTrue(result.Error);
        result.Intent!.Persons.Should().BeEmpty();
        result.Intent.Terms.Should().Equal("Meer");
    }

    [Fact]
    public void Urlaub_als_Begriff_wird_zur_Reise_Erkennung()
    {
        var result = StoryIntentJson.Parse("""{"action":"story","terms":["Urlaubsfotos","Strand","strand"]}""", Today);

        result.Intent!.Trip.Should().BeTrue();
        result.Intent.Terms.Should().Equal(["Strand"], "doppelte Begriffe zählen einmal");
    }

    [Fact]
    public void Hin_und_zurueck_bleibt_gleich_und_das_Schema_ist_gueltiges_JSON()
    {
        var intent = new StoryIntent
        {
            Action = IntentAction.Slideshow,
            Persons = [new IntentPerson(Relation: "Papa"), new IntentPerson(IsMe: true)],
            PersonMatch = IntentMatch.Any,
            Others = IntentOthers.None,
            Terms = ["Meer"],
            DateFrom = new DateOnly(2019, 1, 1),
            DateTo = new DateOnly(2021, 12, 31),
            AgeFrom = 0,
            AgeTo = 20,
            AgePerson = "Anna",
            EveryYearFrom = new MonthDay(12, 24),
            EveryYearTo = new MonthDay(12, 26),
            Media = IntentMedia.Photos,
            Best = 50,
            BestWanted = true,
            MinRating = 4,
            FavoritesOnly = true,
        };

        var back = StoryIntentJson.Parse(StoryIntentJson.ToJson(intent), Today).Intent;

        back.Should().BeEquivalentTo(intent);
        using var schema = JsonDocument.Parse(StoryIntentJson.Schema);
        schema.RootElement.GetProperty("required").GetArrayLength().Should().Be(20);
        schema.RootElement.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
    }

    [Theory]
    [InlineData("Sonnenuntergänge", "Sonnenuntergang", true)]
    [InlineData("Hunde", "Hund", true)]
    [InlineData("STRAßE", "strasse", true)]
    [InlineData("Graz", "Grado", false)]
    public void Begriffe_vergleichen_ohne_Akzente_und_Endungen(string a, string b, bool same)
        => AssistantText.Matches(a, b).Should().Be(same);

    [Theory]
    [InlineData("Oma (mütterlicherseits)", "Oma", true)]
    [InlineData("Großmutter", "Oma", true)]
    [InlineData("Vater", "Papa", true)]
    [InlineData("Schwester", "meine Schwester", true)]
    [InlineData("Onkel", "Oma", false)]
    public void Beziehungen_mit_Synonymen(string relation, string word, bool matches)
        => AssistantText.RelationMatches(relation, word).Should().Be(matches);

    [Fact]
    public void Regeln_verstehen_klare_Saetze()
    {
        IntentPrompt Prompt(string sentence) => new(sentence, Today,
            [new IntentVocabularyPerson("Anna", null, "Schwester", false), new IntentVocabularyPerson("Franz", "Papa", "Vater", false)],
            ["Hund", "Meer"]);

        var name = SimpleIntentParser.TryParse(Prompt("Anna 2019"));
        name!.Persons.Should().ContainSingle().Which.Name.Should().Be("Anna");
        name.DateFrom.Should().Be(new DateOnly(2019, 1, 1));
        name.DateTo.Should().Be(new DateOnly(2019, 12, 31));

        var album = SimpleIntentParser.TryParse(Prompt("Album mit allen Fotos vom Hund 2022"));
        album!.Action.Should().Be(IntentAction.Album);
        album.Terms.Should().Equal("Hund");

        var show = SimpleIntentParser.TryParse(Prompt("Diashow: Papa und ich, die besten 50"));
        show!.Action.Should().Be(IntentAction.Slideshow);
        show.Persons.Should().HaveCount(2);
        show.Persons.Should().Contain(p => p.IsMe);
        show.Best.Should().Be(50);

        var years = SimpleIntentParser.TryParse(Prompt("Anna und Franz 2019-2021, die letzten zwei Jahre"));
        years.Should().NotBeNull();
        years!.LastYears.Should().Be(2);

        SimpleIntentParser.TryParse(Prompt("Weihnachten bei Oma, alle Jahre")).Should().BeNull("unklarer Satz → Sprach-KI nötig");
        SimpleIntentParser.TryParse(Prompt("2019")).Should().BeNull("ohne Person oder Begriff nicht eindeutig");
    }
}
