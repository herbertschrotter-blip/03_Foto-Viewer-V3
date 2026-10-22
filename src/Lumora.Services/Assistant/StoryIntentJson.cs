using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Lumora.Services.Assistant;

/// <summary>Ergebnis der Prüfung einer KI-Antwort.</summary>
/// <param name="Truncated">Die Antwort brach mitten im JSON ab (Längengrenze, Zeitlimit).</param>
public sealed record IntentParseResult(StoryIntent? Intent, string? Error, bool Truncated = false)
{
    public bool IsValid => Intent is not null;

    public static IntentParseResult Fail(string error, bool truncated = false) => new(null, error, truncated);
}

/// <summary>
/// Drahtformat der Absicht (ADR-0063): JSON-Schema für Ollamas <c>format</c> (erzwingt die Form schon beim Erzeugen),
/// strikte Prüfung der Antwort und Rückweg (Verlauf). Alle Felder sind Pflicht im Schema; „keine Angabe“ ist ein leerer
/// Text, 0 bzw. −1 (Alter) — kleine Modelle halten sich zuverlässiger an feste Typen als an <c>null</c>. Die Prüfung lehnt
/// unbekannte Felder, falsche Typen, unbekannte Aufzählungswerte und Werte außerhalb der Grenzen ab; fehlende Felder
/// (Modelle ohne Schema-Unterstützung) gelten als „keine Angabe“, nur <c>action</c> ist Pflicht.
/// </summary>
public static class StoryIntentJson
{
    public const int MaxPersons = 6;
    public const int MaxTerms = 8;
    public const int MaxTextLength = 60;
    public const int MaxBest = 1000;
    public const int MaxAge = 120;
    public const int MinYear = 1800;

    /// <summary>Wörter für „Urlaub/Reise“ — als Begriff genannt, werden sie zur Reise-Erkennung (ADR-0063).</summary>
    public static IReadOnlyList<string> TripWords { get; } = ["urlaub", "urlaube", "urlaubsfotos", "urlaubsbilder", "reise", "reisen", "ferien", "ferienfotos", "reisefotos"];

    private static readonly string[] Actions = ["story", "collage", "album", "slideshow", "gallery"];
    private static readonly string[] Matches = ["all", "any"];
    private static readonly string[] OthersValues = ["any", "none"];
    private static readonly string[] MediaValues = ["all", "photos", "videos"];

    private static readonly HashSet<string> KnownFields = new(StringComparer.Ordinal)
    {
        "action", "persons", "personMatch", "others", "terms", "termMatch", "trip", "dateFrom", "dateTo", "lastYears",
        "ageFrom", "ageTo", "agePerson", "everyYearFrom", "everyYearTo", "media", "best", "bestWanted", "minRating", "favorites",
    };

    /// <summary>JSON-Schema (Draft 2020-12, Teilmenge, die Ollama in eine Grammatik übersetzt).</summary>
    public static string Schema { get; } = BuildSchema();

    private static string BuildSchema()
    {
        static string Str() => "{\"type\":\"string\"}";
        static string Int() => "{\"type\":\"integer\"}";
        static string Bool() => "{\"type\":\"boolean\"}";
        static string Enum(params string[] values) => "{\"type\":\"string\",\"enum\":[" + string.Join(",", values.Select(v => "\"" + v + "\"")) + "]}";
        var props = new (string Name, string Type)[]
        {
            ("action", Enum(Actions)),
            ("persons", "{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{\"name\":" + Str() + ",\"relation\":" + Str() + ",\"me\":" + Bool()
                        + "},\"required\":[\"name\",\"relation\",\"me\"],\"additionalProperties\":false}}"),
            ("personMatch", Enum(Matches)),
            ("others", Enum(OthersValues)),
            ("terms", "{\"type\":\"array\",\"items\":" + Str() + "}"),
            ("termMatch", Enum(Matches)),
            ("trip", Bool()),
            ("dateFrom", Str()),
            ("dateTo", Str()),
            ("lastYears", Int()),
            ("ageFrom", Int()),
            ("ageTo", Int()),
            ("agePerson", Str()),
            ("everyYearFrom", Str()),
            ("everyYearTo", Str()),
            ("media", Enum(MediaValues)),
            ("best", Int()),
            ("bestWanted", Bool()),
            ("minRating", Int()),
            ("favorites", Bool()),
        };
        var sb = new StringBuilder("{\"type\":\"object\",\"properties\":{");
        sb.Append(string.Join(",", props.Select(p => "\"" + p.Name + "\":" + p.Type)));
        sb.Append("},\"required\":[").Append(string.Join(",", props.Select(p => "\"" + p.Name + "\""))).Append("],\"additionalProperties\":false}");
        return sb.ToString();
    }

    /// <summary>Prüft die Antwort der Sprach-KI streng; <paramref name="today"/> begrenzt Jahreszahlen.</summary>
    public static IntentParseResult Parse(string? json, DateOnly today, bool cutOff = false)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return IntentParseResult.Fail("Die Sprach-KI hat nichts geantwortet.", cutOff);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
        }
        catch (JsonException)
        {
            var truncated = cutOff || !json.TrimEnd().EndsWith('}');
            return IntentParseResult.Fail(truncated
                ? "Die Antwort der Sprach-KI ist abgeschnitten — bitte erneut versuchen oder den Satz kürzen."
                : "Die Antwort der Sprach-KI ist kein gültiges JSON.", truncated);
        }

        using (document)
        {
            try
            {
                return new IntentParseResult(Read(document.RootElement, today), null, false);
            }
            catch (FormatException ex)
            {
                return IntentParseResult.Fail("Die Antwort der Sprach-KI passt nicht zum Schema: " + ex.Message, cutOff);
            }
        }
    }

    private static StoryIntent Read(JsonElement root, DateOnly today)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("kein Objekt");
        }

        foreach (var property in root.EnumerateObject())
        {
            if (!KnownFields.Contains(property.Name))
            {
                throw new FormatException($"unbekanntes Feld „{property.Name}“");
            }
        }

        if (!root.TryGetProperty("action", out var actionElement))
        {
            throw new FormatException("„action“ fehlt");
        }

        var action = (IntentAction)Array.IndexOf(Actions, EnumValue(actionElement, "action", Actions));

        var persons = new List<IntentPerson>();
        if (Get(root, "persons") is { } personsElement)
        {
            if (personsElement.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException("„persons“ ist keine Liste");
            }

            foreach (var item in personsElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    throw new FormatException("Eintrag in „persons“ ist kein Objekt");
                }

                foreach (var p in item.EnumerateObject())
                {
                    if (p.Name is not ("name" or "relation" or "me"))
                    {
                        throw new FormatException($"unbekanntes Feld „persons.{p.Name}“");
                    }
                }

                var name = Text(Get(item, "name"), "persons.name");
                var relation = Text(Get(item, "relation"), "persons.relation");
                var me = Bool(Get(item, "me"), "persons.me");
                if (name is null && relation is null && !me)
                {
                    continue; // leerer Eintrag: nichts gesagt
                }

                persons.Add(new IntentPerson(name, relation, me));
            }
        }

        if (persons.Count > MaxPersons)
        {
            throw new FormatException($"mehr als {MaxPersons} Personen");
        }

        var terms = new List<string>();
        var trip = Bool(Get(root, "trip"), "trip");
        if (Get(root, "terms") is { } termsElement)
        {
            if (termsElement.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException("„terms“ ist keine Liste");
            }

            foreach (var item in termsElement.EnumerateArray())
            {
                if (Text(item, "terms") is not { } term)
                {
                    continue;
                }

                if (TripWords.Contains(AssistantText.Normalize(term)))
                {
                    trip = true; // „Urlaub“ als Begriff → Reise-Erkennung
                    continue;
                }

                if (!terms.Any(t => AssistantText.Same(t, term)))
                {
                    terms.Add(term);
                }
            }
        }

        if (terms.Count > MaxTerms)
        {
            throw new FormatException($"mehr als {MaxTerms} Begriffe");
        }

        var dateFrom = Date(Get(root, "dateFrom"), "dateFrom", today);
        var dateTo = Date(Get(root, "dateTo"), "dateTo", today);
        if (dateFrom is { } f && dateTo is { } t && f > t)
        {
            throw new FormatException("„dateFrom“ liegt nach „dateTo“");
        }

        var lastYears = Int(Get(root, "lastYears"), "lastYears", 0, 0, 100);
        var ageFrom = Age(Get(root, "ageFrom"), "ageFrom");
        var ageTo = Age(Get(root, "ageTo"), "ageTo");
        if (ageFrom is { } af && ageTo is { } at && af > at)
        {
            throw new FormatException("„ageFrom“ ist größer als „ageTo“");
        }

        MonthDay? everyFrom = null, everyTo = null;
        var everyFromText = Text(Get(root, "everyYearFrom"), "everyYearFrom");
        var everyToText = Text(Get(root, "everyYearTo"), "everyYearTo");
        if (everyFromText is not null || everyToText is not null)
        {
            if (!MonthDay.TryParse(everyFromText ?? everyToText, out var a) || !MonthDay.TryParse(everyToText ?? everyFromText, out var b))
            {
                throw new FormatException("„everyYearFrom/To“ ist kein Tag im Format MM-TT");
            }

            (everyFrom, everyTo) = (a, b);
        }

        var best = Int(Get(root, "best"), "best", 0, 0, MaxBest);
        return new StoryIntent
        {
            Action = action,
            Persons = persons,
            PersonMatch = Enum(Get(root, "personMatch"), "personMatch", Matches) == 1 ? IntentMatch.Any : IntentMatch.All,
            Others = Enum(Get(root, "others"), "others", OthersValues) == 1 ? IntentOthers.None : IntentOthers.Any,
            Terms = terms,
            TermMatch = Enum(Get(root, "termMatch"), "termMatch", Matches) == 1 ? IntentMatch.Any : IntentMatch.All,
            Trip = trip,
            DateFrom = dateFrom,
            DateTo = dateTo,
            LastYears = lastYears,
            AgeFrom = ageFrom,
            AgeTo = ageTo,
            AgePerson = Text(Get(root, "agePerson"), "agePerson"),
            EveryYearFrom = everyFrom,
            EveryYearTo = everyTo,
            Media = (IntentMedia)Math.Max(0, Enum(Get(root, "media"), "media", MediaValues)),
            Best = best > 0 ? best : null,
            BestWanted = Bool(Get(root, "bestWanted"), "bestWanted") || best > 0,
            MinRating = Int(Get(root, "minRating"), "minRating", 0, 0, 5),
            FavoritesOnly = Bool(Get(root, "favorites"), "favorites"),
        };
    }

    /// <summary>Absicht im Drahtformat (Verlauf, Tests, Rauchtest-Attrappe).</summary>
    public static string ToJson(StoryIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("action", Actions[(int)intent.Action]);
            w.WriteStartArray("persons");
            foreach (var p in intent.Persons)
            {
                w.WriteStartObject();
                w.WriteString("name", p.Name ?? string.Empty);
                w.WriteString("relation", p.Relation ?? string.Empty);
                w.WriteBoolean("me", p.IsMe);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteString("personMatch", Matches[(int)intent.PersonMatch]);
            w.WriteString("others", OthersValues[(int)intent.Others]);
            w.WriteStartArray("terms");
            foreach (var term in intent.Terms)
            {
                w.WriteStringValue(term);
            }

            w.WriteEndArray();
            w.WriteString("termMatch", Matches[(int)intent.TermMatch]);
            w.WriteBoolean("trip", intent.Trip);
            w.WriteString("dateFrom", intent.DateFrom?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty);
            w.WriteString("dateTo", intent.DateTo?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty);
            w.WriteNumber("lastYears", intent.LastYears);
            w.WriteNumber("ageFrom", intent.AgeFrom ?? -1);
            w.WriteNumber("ageTo", intent.AgeTo ?? -1);
            w.WriteString("agePerson", intent.AgePerson ?? string.Empty);
            w.WriteString("everyYearFrom", intent.EveryYearFrom?.ToString() ?? string.Empty);
            w.WriteString("everyYearTo", intent.EveryYearTo?.ToString() ?? string.Empty);
            w.WriteString("media", MediaValues[(int)intent.Media]);
            w.WriteNumber("best", intent.Best ?? 0);
            w.WriteBoolean("bestWanted", intent.BestWanted);
            w.WriteNumber("minRating", intent.MinRating);
            w.WriteBoolean("favorites", intent.FavoritesOnly);
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static JsonElement? Get(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value : null;

    private static string? Text(JsonElement? element, string field)
    {
        if (element is not { } e)
        {
            return null;
        }

        if (e.ValueKind != JsonValueKind.String)
        {
            throw new FormatException($"„{field}“ ist kein Text");
        }

        var text = e.GetString()?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (text.Length > MaxTextLength)
        {
            throw new FormatException($"„{field}“ ist zu lang");
        }

        return text;
    }

    private static bool Bool(JsonElement? element, string field)
        => element switch
        {
            null => false,
            { ValueKind: JsonValueKind.True } => true,
            { ValueKind: JsonValueKind.False } => false,
            _ => throw new FormatException($"„{field}“ ist kein Wahrheitswert"),
        };

    private static int Int(JsonElement? element, string field, int fallback, int min, int max)
    {
        if (element is not { } e)
        {
            return fallback;
        }

        if (e.ValueKind != JsonValueKind.Number || !e.TryGetInt32(out var value))
        {
            throw new FormatException($"„{field}“ ist keine ganze Zahl");
        }

        if (value < min || value > max)
        {
            throw new FormatException($"„{field}“ liegt außerhalb von {min}–{max}");
        }

        return value;
    }

    private static int? Age(JsonElement? element, string field)
    {
        var value = Int(element, field, -1, -1, MaxAge);
        return value < 0 ? null : value;
    }

    private static int Enum(JsonElement? element, string field, string[] values)
        => element is { } e ? Array.IndexOf(values, EnumValue(e, field, values)) : 0;

    private static string EnumValue(JsonElement element, string field, string[] values)
    {
        if (element.ValueKind != JsonValueKind.String || element.GetString() is not { } text || !values.Contains(text, StringComparer.Ordinal))
        {
            throw new FormatException($"„{field}“ hat keinen erlaubten Wert");
        }

        return text;
    }

    private static DateOnly? Date(JsonElement? element, string field, DateOnly today)
    {
        if (Text(element, field) is not { } text)
        {
            return null;
        }

        if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            throw new FormatException($"„{field}“ ist kein Datum im Format JJJJ-MM-TT");
        }

        if (date.Year < MinYear || date.Year > today.Year + 1)
        {
            throw new FormatException($"„{field}“ liegt außerhalb von {MinYear}–{today.Year + 1}");
        }

        return date;
    }
}
