using System.Globalization;
using System.Text.RegularExpressions;

namespace Lumora.Services.Assistant;

/// <summary>
/// Regelbasierter Rückfall ohne Sprach-KI (ADR-0063) — NUR für klare Sätze: bekannte Namen/Beziehungen/„ich“, Begriffe
/// aus dem Wortschatz, Jahre („2022“, „2019–2021“), „die letzten N Jahre“, „die besten N“, Urlaub, Fotos/Videos und das
/// Aktionswort (Collage, Album, Diashow, zeigen). Bleibt ein Wort übrig, das kein Füllwort ist, gibt er auf (null) —
/// lieber „dafür braucht es die Sprach-KI“ als raten. Dient auch als Attrappe im Rauchtest.
/// </summary>
public static partial class SimpleIntentParser
{
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "mit", "von", "vom", "und", "aus", "der", "die", "das", "den", "dem", "des", "alle", "allen", "aller", "alles", "fotos", "foto",
        "bilder", "bild", "aufnahmen", "meine", "meiner", "meinem", "meinen", "mein", "unsere", "unserer", "unser", "eine", "einer", "ein", "einem",
        "einen", "im", "in", "am", "an", "auf", "bei", "beim", "zu", "zum", "zur", "fur", "erstelle", "erstell", "erstellen", "mach", "mache",
        "machen", "zeige", "zeig", "zeigen", "bitte", "story", "storys", "collage", "album", "diashow", "galerie", "als", "jahr", "jahre",
        "jahren", "jahres", "letzten", "letzte", "besten", "schonsten", "schonste", "nur", "videos", "video", "oder", "mir", "uns", "wir", "ich",
        "mich", "bis", "seit", "ab", "zwischen", "sowie", "auch", "abspielen", "spiele", "spiel", "neue", "neues", "neuen", "ganz", "jedes",
    };

    private static readonly Dictionary<string, int> NumberWords = new(StringComparer.Ordinal)
    {
        ["zwei"] = 2, ["drei"] = 3, ["vier"] = 4, ["funf"] = 5, ["sechs"] = 6, ["sieben"] = 7, ["acht"] = 8, ["neun"] = 9, ["zehn"] = 10,
        ["zwanzig"] = 20, ["dreissig"] = 30, ["funfzig"] = 50, ["hundert"] = 100,
    };

    /// <summary>Absicht für einen klaren Satz oder null.</summary>
    public static StoryIntent? TryParse(IntentPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        var text = " " + AssistantText.Normalize(prompt.Sentence.Replace(',', ' ').Replace(':', ' ').Replace('.', ' ').Replace('!', ' ').Replace('?', ' ')) + " ";
        if (text.Trim().Length == 0)
        {
            return null;
        }

        var consumed = new List<(int Start, int Length)>();
        bool Take(string phrase)
        {
            var needle = " " + phrase + " ";
            var index = text.IndexOf(needle, StringComparison.Ordinal);
            if (index < 0 || phrase.Length == 0)
            {
                return false;
            }

            consumed.Add((index + 1, phrase.Length));
            text = text[..(index + 1)] + new string('#', phrase.Length) + text[(index + 1 + phrase.Length)..];
            return true;
        }

        // Aktion
        var action = IntentAction.Story;
        if (Take("collage"))
        {
            action = IntentAction.Collage;
        }
        else if (Take("diashow") || Take("abspielen"))
        {
            action = IntentAction.Slideshow;
        }
        else if (Take("album"))
        {
            action = IntentAction.Album;
        }
        else if (Take("galerie") || Take("zeige") || Take("zeig") || Take("zeigen"))
        {
            action = IntentAction.Gallery;
        }

        // die besten N / letzte N Jahre
        int? best = null;
        var bestWanted = false;
        var bestMatch = BestRegex().Match(text);
        if (bestMatch.Success)
        {
            bestWanted = true;
            if (ParseNumber(bestMatch.Groups[2].Value) is { } n)
            {
                best = n;
            }

            text = text[..bestMatch.Index] + new string('#', bestMatch.Length) + text[(bestMatch.Index + bestMatch.Length)..];
        }

        var lastYears = 0;
        var lastMatch = LastYearsRegex().Match(text);
        if (lastMatch.Success && ParseNumber(lastMatch.Groups[1].Value) is { } years and > 0 and <= 100)
        {
            lastYears = years;
            text = text[..lastMatch.Index] + new string('#', lastMatch.Length) + text[(lastMatch.Index + lastMatch.Length)..];
        }

        // Jahre
        DateOnly? from = null, to = null;
        var yearMatches = YearRegex().Matches(text);
        var yearValues = yearMatches.Select(m => int.Parse(m.Value, CultureInfo.InvariantCulture)).Where(y => y >= 1800 && y <= prompt.Today.Year + 1).ToList();
        if (yearValues.Count > 0)
        {
            from = new DateOnly(yearValues.Min(), 1, 1);
            to = new DateOnly(yearValues.Max(), 12, 31);
            text = YearRegex().Replace(text, m => new string('#', m.Length));
        }

        // Personen (längste Namen zuerst)
        var persons = new List<IntentPerson>();
        foreach (var person in prompt.Persons.SelectMany(p => new[] { p.Name, p.Nickname }.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => (Text: n!, Person: p)))
                     .OrderByDescending(x => x.Text.Length))
        {
            if (Take(AssistantText.Normalize(person.Text)) && persons.All(p => !AssistantText.Same(p.Name, person.Person.Name)))
            {
                persons.Add(new IntentPerson(person.Person.Name));
            }
        }

        foreach (var relation in prompt.Persons.Select(p => p.Relation).Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => AssistantText.Normalize(r))
                     .SelectMany(r => AssistantText.Variants(r).Prepend(r)).Distinct().OrderByDescending(r => r.Length))
        {
            if (Take(relation))
            {
                persons.Add(new IntentPerson(Relation: relation));
            }
        }

        if (Take("ich") | Take("mir") | Take("mich") | Take("wir"))
        {
            persons.Add(new IntentPerson(IsMe: true));
        }

        // Urlaub
        var trip = false;
        foreach (var word in StoryIntentJson.TripWords)
        {
            trip |= Take(word);
        }

        // Begriffe aus dem Wortschatz (auch Mehrzahl)
        var terms = new List<string>();
        foreach (var term in prompt.Terms.OrderByDescending(t => t.Length))
        {
            var normalized = AssistantText.Normalize(term);
            if (Take(normalized) || TakeStem(normalized))
            {
                terms.Add(term);
            }
        }

        bool TakeStem(string normalized)
        {
            foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!word.Contains('#', StringComparison.Ordinal) && AssistantText.Stem(word) == AssistantText.Stem(normalized) && word.Length >= 3)
                {
                    return Take(word);
                }
            }

            return false;
        }

        var media = IntentMedia.All;
        if (text.Contains(" nur videos ", StringComparison.Ordinal) || (text.Contains(" videos ", StringComparison.Ordinal) && !text.Contains(" fotos ", StringComparison.Ordinal)))
        {
            media = IntentMedia.Videos;
        }

        // Rest: nur Füllwörter erlaubt
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.All(c => c is '#' or '-' or '–') || StopWords.Contains(word))
            {
                continue;
            }

            return null;
        }

        if (persons.Count == 0 && terms.Count == 0 && !trip)
        {
            return null;
        }

        return new StoryIntent
        {
            Action = action,
            Persons = persons,
            Terms = terms,
            Trip = trip,
            DateFrom = lastYears > 0 ? null : from,
            DateTo = lastYears > 0 ? null : to,
            LastYears = lastYears,
            Media = media,
            Best = best,
            BestWanted = bestWanted,
        };
    }

    private static int? ParseNumber(string text)
        => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : NumberWords.TryGetValue(text, out var w) ? w : null;

    [GeneratedRegex(@"(besten|schonsten)(?: (\d+|zwei|drei|vier|funf|sechs|sieben|acht|neun|zehn|zwanzig|dreissig|funfzig|hundert))?(?= )")]
    private static partial Regex BestRegex();

    [GeneratedRegex(@"letzten (\d+|zwei|drei|vier|funf|sechs|sieben|acht|neun|zehn|zwanzig) jahren?(?= )")]
    private static partial Regex LastYearsRegex();

    [GeneratedRegex(@"(?<=[ \-–])\d{4}(?=[ \-–])")]
    private static partial Regex YearRegex();
}
