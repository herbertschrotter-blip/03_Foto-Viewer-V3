using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Lumora.Services.Assistant;

/// <summary>Eine Person, wie sie die Sprach-KI sieht: nur Anzeigename, Rufname, Beziehung, „ich“ — kein Geburtsdatum, keine Fotos.</summary>
public sealed record IntentVocabularyPerson(string Name, string? Nickname, string? Relation, bool IsMe);

/// <summary>
/// Alles, was die Sprach-KI zu einem Satz bekommt (ADR-0063): der Satz, das heutige Datum, die Personen (Anzeigename,
/// Rufname, Beziehung), die Namen von Motiven, Tags und Kategorien des aktuellen Bereichs und die erlaubten Aktionen.
/// NIE Fotos, Dateinamen, Pfade, Koordinaten, Geburtsdaten oder Zählungen.
/// </summary>
public sealed record IntentPrompt(
    string Sentence,
    DateOnly Today,
    IReadOnlyList<IntentVocabularyPerson> Persons,
    IReadOnlyList<string> Terms)
{
    /// <summary>Höchstens so viele Personen bzw. Begriffe gehen an die Sprach-KI (Kontextlänge kleiner Modelle).</summary>
    public const int MaxPersons = 150;

    public const int MaxTerms = 300;

    public const int MaxSentenceLength = 300;
}

/// <summary>
/// Baut die Nachrichten an die Sprach-KI (ADR-0063): Systemtext mit Regeln, Wortschatz und Beispielen, Benutzertext = der
/// Satz. Die Beispiele zeigen das Drahtformat (<see cref="StoryIntentJson"/>); sie enthalten keine echten Daten.
/// </summary>
public static class IntentPromptBuilder
{
    public static string System(IntentPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        var vocabulary = new
        {
            heute = prompt.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            personen = prompt.Persons.Take(IntentPrompt.MaxPersons).Select(p => new { name = p.Name, rufname = p.Nickname ?? string.Empty, beziehung = p.Relation ?? string.Empty, ich = p.IsMe }),
            begriffe = prompt.Terms.Take(IntentPrompt.MaxTerms),
            aktionen = new[] { "story", "collage", "album", "slideshow", "gallery" },
        };
        var sb = new StringBuilder();
        sb.AppendLine("Du übersetzt einen deutschen Satz über Fotos in eine Suchabsicht als JSON. Du siehst keine Fotos.");
        sb.AppendLine("Antworte NUR mit dem JSON nach dem Schema. Erfinde nichts: keine Titel, keine Beschreibungen, keine Texte.");
        sb.AppendLine("Regeln:");
        sb.AppendLine("- action: collage (Collage), album (Album), slideshow (Diashow/abspielen), gallery (nur zeigen/anzeigen), sonst story.");
        sb.AppendLine("- persons: jede genannte Person. Bekannter Name → name. Beziehung („meine Schwester“, „Oma“, „Papa“) → relation (ohne „mein/meine“). „ich/mir/mich/wir“ → me=true.");
        sb.AppendLine("- personMatch: all, wenn alle zusammen auf dem Foto sein sollen (Standard, „mit A und B“); any bei „A oder B“.");
        sb.AppendLine("- others: none nur bei „nur wir“, „allein“, „nur die beiden“; sonst any.");
        sb.AppendLine("- terms: Orte (Städte, Länder), Dinge, Tiere, Motive, Anlässe und Themen als einzelne Begriffe, möglichst wie im Wortschatz geschrieben. „am Meer“ → \"Meer\". Keine Personen, keine Zeitangaben, keine Füllwörter.");
        sb.AppendLine("- trip: true bei Urlaub, Reise, Ferien (dann NICHT zusätzlich als Begriff).");
        sb.AppendLine("- dateFrom/dateTo: JJJJ-MM-TT für feste Zeiträume („2022“ → 2022-01-01 bis 2022-12-31, „Sommer 2019“ → 2019-06-01 bis 2019-08-31). Sonst leer.");
        sb.AppendLine("- lastYears: Zahl bei „die letzten N Jahre“ (sonst 0). Rechne NICHT selbst in Daten um.");
        sb.AppendLine("- ageFrom/ageTo: Alter in Jahren („bis 20 Jahre“ → ageFrom 0, ageTo 20; Kindheit → 0 bis 12; Jugend → 13 bis 19), sonst -1. agePerson: wessen Alter (Name, Beziehung oder „ich“), sonst leer.");
        sb.AppendLine("- everyYearFrom/everyYearTo: wiederkehrende Tage als MM-TT („Weihnachten“ → 12-24 bis 12-26, „Silvester“ → 12-31 bis 12-31, „Geburtstag“ nicht raten). Sonst leer.");
        sb.AppendLine("- media: photos bei „Fotos/Bilder“ nur wenn Videos ausdrücklich nicht gemeint sind, videos bei „Videos“, sonst all.");
        sb.AppendLine("- best: Zahl bei „die besten/schönsten N“ (sonst 0); bestWanted: true bei „die besten/schönsten“ auch ohne Zahl.");
        sb.AppendLine("- minRating: Mindest-Sterne bei „ab 4 Sternen“ (sonst 0); favorites: true bei „Favoriten“.");
        sb.AppendLine("- Felder ohne Angabe: leerer Text, 0, -1 bzw. false.");
        sb.AppendLine("Wortschatz (nur Namen):");
        sb.AppendLine(JsonSerializer.Serialize(vocabulary, new JsonSerializerOptions { Encoder = global::System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        sb.AppendLine("Beispiel: „Diashow: Papa und ich, die besten 50“ →");
        sb.AppendLine(StoryIntentJson.ToJson(new StoryIntent
        {
            Action = IntentAction.Slideshow,
            Persons = [new IntentPerson(Relation: "Papa"), new IntentPerson(IsMe: true)],
            Best = 50,
            BestWanted = true,
        }));
        sb.AppendLine("Beispiel: „Weihnachten bei Oma, alle Jahre“ →");
        sb.AppendLine(StoryIntentJson.ToJson(new StoryIntent
        {
            Persons = [new IntentPerson(Relation: "Oma")],
            EveryYearFrom = new MonthDay(12, 24),
            EveryYearTo = new MonthDay(12, 26),
        }));
        return sb.ToString();
    }

    /// <summary>Der Satz, gekürzt auf <see cref="IntentPrompt.MaxSentenceLength"/> Zeichen.</summary>
    public static string User(IntentPrompt prompt)
    {
        var text = prompt.Sentence.Trim();
        return text.Length <= IntentPrompt.MaxSentenceLength ? text : text[..IntentPrompt.MaxSentenceLength];
    }
}

/// <summary>Antwort eines Sprachmodells.</summary>
/// <param name="Json">Antworttext (soll JSON nach <see cref="StoryIntentJson.Schema"/> sein).</param>
/// <param name="CutOff">Das Modell hat wegen der Längengrenze aufgehört.</param>
public sealed record IntentModelResponse(string Json, bool CutOff = false);

/// <summary>Sprachmodell nicht verfügbar bzw. Fehler — Text für den Benutzer.</summary>
public sealed class IntentModelException(string message, IntentModelFailure failure, Exception? inner = null) : Exception(message, inner)
{
    public IntentModelFailure Failure { get; } = failure;
}

public enum IntentModelFailure
{
    ServerUnreachable = 0,
    ModelMissing = 1,
    Timeout = 2,
    ServerError = 3,
}

/// <summary>
/// Übersetzt einen Satz in das Drahtformat der Absicht (ADR-0063). Die Ollama-Implementierung spricht nur mit
/// <c>http://127.0.0.1:11434</c>; Tests und der Rauchtest setzen Attrappen ein.
/// </summary>
public interface IIntentModel
{
    /// <summary>Wirft <see cref="IntentModelException"/> (Server weg, Modell fehlt, Zeitlimit) bzw. <see cref="OperationCanceledException"/>.</summary>
    Task<IntentModelResponse> InterpretAsync(IntentPrompt prompt, CancellationToken ct);
}
