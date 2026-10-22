using System.Globalization;
using Lumora.Services.Library;
using Lumora.Services.Places;
using Lumora.Services.Stories;

namespace Lumora.Services.Assistant;

/// <summary>
/// Löst die Absicht der Sprach-KI in Lumora auf (ADR-0063) — nichts davon sieht die KI:
/// <list type="bullet">
/// <item>Personen über Name/Rufname bzw. Beziehung (mit Synonymen) und gemerkte Antworten; mehrdeutig oder unbekannt →
/// Rückfrage mit Gesicht und Fotozahl statt Raten.</item>
/// <item>Zeiträume absolut, „letzte N Jahre“ relativ zu heute, Alter aus dem Geburtsdatum der Person (fehlt es →
/// Rückfrage), wiederkehrende Tage jedes Jahr.</item>
/// <item>Begriffe gegen Tags, Kategorien (Name und Werte), Motive, Personen und Orte (Offline-Ortssuche, Länder und
/// Regionen des Bereichs) — mehrere Treffer werden vereinigt und mit Zahlen gezeigt, keiner → „nicht gefunden“.</item>
/// <item>Urlaub/Reise über die Reise-Erkennung der Storys plus passende Tags/Kategorien; ohne eingestelltes Zuhause →
/// Rückfrage.</item>
/// </list>
/// </summary>
public sealed class StoryIntentResolver(
    AssistantQueries queries,
    IReverseGeocoder? geocoder = null,
    IPlaceLookup? places = null,
    IStorySettingsStore? storySettings = null)
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    public async Task<AssistantResolution> ResolveAsync(
        StoryIntent intent, LibraryScopeInfo scope, AssistantVocabulary vocabulary, DateOnly today, AssistantAnswers? answers = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(vocabulary);
        answers ??= new AssistantAnswers();
        var questions = new List<AssistantQuestion>();

        // ---- Personen
        var persons = new List<AssistantPerson>();
        foreach (var wanted in intent.Persons)
        {
            var (found, question) = ResolvePerson(wanted, vocabulary, answers);
            if (question is not null)
            {
                questions.Add(question);
            }

            foreach (var p in found.Where(p => persons.All(x => x.Id != p.Id)))
            {
                persons.Add(p);
            }
        }

        // ---- Zeitraum
        var (time, timeQuestion) = ResolveTime(intent, persons, vocabulary, today, answers);
        if (timeQuestion is not null)
        {
            questions.Add(timeQuestion);
        }

        // ---- Begriffe
        IReadOnlyList<PlaceInfo>? scopePlaces = null;
        var terms = new List<PlanTerm>();
        foreach (var text in intent.Terms)
        {
            ct.ThrowIfCancellationRequested();
            if (intent.Persons.Any(p => AssistantText.Same(p.Text, text)))
            {
                continue; // schon als Person aufgelöst
            }

            scopePlaces ??= await ReadScopePlacesAsync(scope, ct).ConfigureAwait(false);
            terms.Add(new PlanTerm(text, await MatchTermAsync(text, scope, vocabulary, scopePlaces, ct).ConfigureAwait(false)));
        }

        // ---- Urlaub/Reise
        PlanTrip? trip = null;
        if (intent.Trip)
        {
            // Tags und Kategorien (Name bzw. Wert) mit Urlaubs-Wörtern — je Art einmal gezählt
            bool Trip(string name) => StoryIntentJson.TripWords.Any(w => AssistantText.Matches(name, w));
            var extra = new List<TermMatch>();
            foreach (var (kind, items) in new (TermMatchKind, List<(int Id, string Label)>)[]
                     {
                         (TermMatchKind.Tag, vocabulary.Tags.Where(t => Trip(t.Name)).Select(t => (t.Id, t.Name)).ToList()),
                         (TermMatchKind.Category, vocabulary.Categories.Where(c => Trip(c.Name)).Select(c => (c.Id, c.Name)).ToList()),
                         (TermMatchKind.CategoryValue, vocabulary.CategoryValues.Where(v => Trip(v.Name)).Select(v => (v.Id, v.CategoryName + ": " + v.Name)).ToList()),
                     })
            {
                if (items.Count > 0)
                {
                    extra.Add(await CountedAsync(new TermMatch(kind, string.Join(", ", items.Select(i => i.Label).Distinct()), items.Select(i => i.Id).ToList(), 0), scope, ct).ConfigureAwait(false));
                }
            }

            var useDetection = !answers.Skipped.Contains(HomeKey);
            if (useDetection && storySettings is not null)
            {
                var settings = await storySettings.LoadAsync(ct).ConfigureAwait(false);
                if (settings.HomeOrAutomatic.IsAutomatic)
                {
                    questions.Add(await HomeQuestionAsync(scope, extra.Count > 0, ct).ConfigureAwait(false));
                    useDetection = false; // bis zur Antwort nicht raten
                }
            }

            var detection = useDetection ? await queries.CountAsync(scope, new TermMatch(TermMatchKind.TripDetection, "Reise", [], 0), ct).ConfigureAwait(false) : 0;
            trip = new PlanTrip(useDetection, detection, extra);
        }

        var plan = new AssistantPlan
        {
            Action = intent.Action,
            AlsoStory = intent.Action == IntentAction.Collage,
            Persons = persons,
            PersonMatch = intent.PersonMatch,
            Others = intent.Others,
            Terms = terms,
            TermMatch = intent.TermMatch,
            Trip = trip,
            Time = time,
            Media = intent.Action == IntentAction.Collage && intent.Media == IntentMedia.All ? IntentMedia.Photos : intent.Media,
            Best = intent.Best,
            BestSuggested = intent.Best is null && intent.BestWanted,
            MinRating = intent.MinRating,
            FavoritesOnly = intent.FavoritesOnly,
        };
        return new AssistantResolution(plan, questions);
    }

    /// <summary>Schlüssel der Rückfrage „Zuhause“.</summary>
    public const string HomeKey = "home";

    /// <summary>Schlüssel einer Personen-Rückfrage (= gemerkter Begriff).</summary>
    public static string PersonKey(IntentPerson person) => person.IsMe && string.IsNullOrWhiteSpace(person.Name) && string.IsNullOrWhiteSpace(person.Relation)
        ? "ich"
        : AssistantText.Normalize(person.Text);

    // ---- Personen ---------------------------------------------------------------------------------------------------

    private static (IReadOnlyList<AssistantPerson> Found, AssistantQuestion? Question) ResolvePerson(IntentPerson wanted, AssistantVocabulary vocabulary, AssistantAnswers answers)
    {
        var key = PersonKey(wanted);
        if (answers.Skipped.Contains("person:" + key))
        {
            return ([], null);
        }

        var byId = vocabulary.Persons.ToDictionary(p => p.Id);
        if (answers.Persons.TryGetValue(key, out var answered))
        {
            return (answered.Where(byId.ContainsKey).Select(id => byId[id]).ToList(), null);
        }

        if (vocabulary.Aliases.TryGetValue(key, out var alias) && alias.Any(byId.ContainsKey))
        {
            return (alias.Where(byId.ContainsKey).Select(id => byId[id]).ToList(), null);
        }

        List<AssistantPerson> candidates;
        string title;
        if (key == "ich")
        {
            candidates = vocabulary.Persons.Where(p => p.IsMe).ToList();
            title = "Wer sind Sie?";
        }
        else if (!string.IsNullOrWhiteSpace(wanted.Name))
        {
            candidates = vocabulary.Persons.Where(p => p.NameMatches(wanted.Name)).ToList();
            if (candidates.Count == 0)
            {
                candidates = ByRelation(vocabulary, wanted.Name); // die KI hat eine Beziehung als Namen geliefert
            }

            if (candidates.Count > 1 && !string.IsNullOrWhiteSpace(wanted.Relation))
            {
                var narrowed = candidates.Where(p => AssistantText.RelationMatches(p.Relation, wanted.Relation)).ToList();
                candidates = narrowed.Count > 0 ? narrowed : candidates;
            }

            title = $"Wer ist „{wanted.Name.Trim()}“?";
        }
        else
        {
            candidates = ByRelation(vocabulary, wanted.Relation ?? string.Empty);
            title = $"Wer ist „{wanted.Relation?.Trim()}“?";
        }

        if (candidates.Count == 1)
        {
            return (candidates, null);
        }

        var many = candidates.Count > 1;
        var shown = (many ? candidates : vocabulary.Persons.ToList()).OrderByDescending(p => p.FileCount).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).Take(8).ToList();
        var choices = shown.Select(p => new QuestionChoice(p.Name, Detail(p), [p.Id], null, p.FaceId, p.FaceLibraryId)).ToList();
        if (many && candidates.Count <= 4)
        {
            choices.Add(new QuestionChoice(candidates.Count == 2 ? "Beide" : "Alle " + candidates.Count.ToString(German), "eine Auswahl mit allen", candidates.Select(p => p.Id).ToList()));
        }

        choices.Add(new QuestionChoice("Ohne diese Person", "weiter ohne sie", [], "skip"));
        var text = key == "ich"
            ? "In Ihren Personen ist niemand als „Das bin ich“ markiert. Die Antwort merkt sich Lumora für „ich“ in künftigen Sätzen (änderbar unter Einstellungen › Assistent)."
            : many
                ? $"In Ihren Personen passen {candidates.Count.ToString(German)} auf „{wanted.Text}“. Die Antwort merkt sich Lumora für „{wanted.Text}“ in künftigen Sätzen (änderbar unter Einstellungen › Assistent)."
                : $"„{wanted.Text}“ kenne ich in Ihren Personen nicht. Wen meinen Sie? Die Antwort merkt sich Lumora (änderbar unter Einstellungen › Assistent).";
        return ([], new AssistantQuestion(QuestionKind.Person, key, title, text, choices));
    }

    private static List<AssistantPerson> ByRelation(AssistantVocabulary vocabulary, string relation)
        => vocabulary.Persons.Where(p => AssistantText.RelationMatches(p.Relation, relation)
                                         || (p.Nickname is { } nick && AssistantText.RelationMatches(nick, relation))).ToList();

    private static string Detail(AssistantPerson p)
    {
        var count = p.FileCount.ToString("N0", German) + (p.FileCount == 1 ? " Foto" : " Fotos");
        return string.IsNullOrWhiteSpace(p.Relation) ? count : p.Relation + " · " + count;
    }

    // ---- Zeitraum ---------------------------------------------------------------------------------------------------

    private static (PlanTime? Time, AssistantQuestion? Question) ResolveTime(StoryIntent intent, IReadOnlyList<AssistantPerson> persons, AssistantVocabulary vocabulary, DateOnly today, AssistantAnswers answers)
    {
        if (!intent.HasTime)
        {
            return (null, null);
        }

        DateOnly? from = intent.DateFrom, to = intent.DateTo;
        var sources = new List<string>();
        AssistantQuestion? question = null;

        if (intent.LastYears > 0)
        {
            var start = today.AddYears(-intent.LastYears).AddDays(1);
            from = Max(from, start);
            to = Min(to, today);
            sources.Add(intent.LastYears == 1 ? "letztes Jahr bis heute" : $"letzte {intent.LastYears.ToString(German)} Jahre bis heute");
        }

        if (intent.HasAge)
        {
            var person = AgePerson(intent.AgePerson, persons, vocabulary);
            var key = "birth:" + (person?.Id.ToString(CultureInfo.InvariantCulture) ?? "?");
            if (answers.Skipped.Contains(key))
            {
                // ohne Zeitraum aus dem Alter weiter
            }
            else if (person?.BirthDate is { } birth)
            {
                var (ageFrom, ageTo) = AgeRange(birth, intent.AgeFrom ?? 0, intent.AgeTo);
                from = Max(from, ageFrom);
                to = ageTo is null ? to : Min(to, ageTo);
                var ages = intent.AgeTo is { } a
                    ? (intent.AgeFrom is > 0 ? $"{intent.AgeFrom.Value.ToString(German)} bis {a.ToString(German)} Jahre" : $"Geburt bis {a.ToString(German)} Jahre")
                    : $"ab {(intent.AgeFrom ?? 0).ToString(German)} Jahren";
                sources.Add($"{ages} (aus dem Geburtsdatum von {person.Name})");
            }
            else if (person is not null)
            {
                question = new AssistantQuestion(
                    QuestionKind.BirthDate, key, $"Wann ist {person.Name} geboren?",
                    $"Für „{AgeText(intent)}“ braucht Lumora das Geburtsdatum von {person.Name} — es ist nicht eingetragen. Bitte unter Personen › Details eintragen (auch nur das Jahr) und den Satz erneut prüfen.",
                    [new QuestionChoice("Ohne Zeitraum weiter", "alle Jahre", [], "skip")]);
            }
        }

        MonthDay? everyFrom = intent.EveryYearFrom, everyTo = intent.EveryYearTo;
        if (from is null && to is null && everyFrom is null)
        {
            return (null, question);
        }

        if (from is { } f && to is { } t && f > t)
        {
            sources.Add("Zeitraum ohne Überschneidung");
        }

        var description = Describe(from, to, everyFrom, everyTo);
        return (new PlanTime(from, to, everyFrom, everyTo, description, sources.Count == 0 ? null : string.Join(" · ", sources)), question);
    }

    private static AssistantPerson? AgePerson(string? text, IReadOnlyList<AssistantPerson> persons, AssistantVocabulary vocabulary)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return persons.FirstOrDefault() ?? vocabulary.Persons.FirstOrDefault(p => p.IsMe);
        }

        if (AssistantText.Normalize(text) is "ich" or "mir" or "mich" or "mein" or "meiner" or "meine")
        {
            return persons.FirstOrDefault(p => p.IsMe) ?? vocabulary.Persons.FirstOrDefault(p => p.IsMe)
                   ?? (vocabulary.Aliases.TryGetValue("ich", out var me) ? vocabulary.Persons.FirstOrDefault(p => me.Contains(p.Id)) : null);
        }

        return persons.FirstOrDefault(p => p.NameMatches(text) || AssistantText.RelationMatches(p.Relation, text))
               ?? (vocabulary.Aliases.TryGetValue(AssistantText.Normalize(text), out var ids) ? vocabulary.Persons.FirstOrDefault(p => ids.Contains(p.Id)) : null)
               ?? persons.FirstOrDefault();
    }

    /// <summary>
    /// Zeitraum zu einem Alter: frühester möglicher Tag (ungefähres Datum: frühester Geburtstag) bis letzter Tag vor dem
    /// (<paramref name="ageTo"/> + 1)-ten Geburtstag (ungefähr: spätester Geburtstag). Ohne <paramref name="ageTo"/> offen.
    /// </summary>
    public static (DateOnly From, DateOnly? To) AgeRange(Domain.Entities.PersonBirthDate birth, int ageFrom, int? ageTo)
    {
        var earliest = new DateOnly(birth.Year, birth.Month ?? 1, birth.Day ?? 1);
        var latest = birth.Day is { } d && birth.Month is { } m
            ? new DateOnly(birth.Year, m, d)
            : birth.Month is { } month
                ? new DateOnly(birth.Year, month, DateTime.DaysInMonth(birth.Year, month))
                : new DateOnly(birth.Year, 12, 31);
        var from = AddYears(earliest, Math.Max(0, ageFrom));
        DateOnly? to = ageTo is { } a ? AddYears(latest, a + 1).AddDays(-1) : null;
        return (from, to);
    }

    private static DateOnly AddYears(DateOnly date, int years)
        => date.Month == 2 && date.Day == 29 && !DateTime.IsLeapYear(date.Year + years) ? new DateOnly(date.Year + years, 3, 1) : date.AddYears(years);

    private static string AgeText(StoryIntent intent)
        => intent.AgeTo is { } a ? $"bis {a.ToString(German)} Jahre" : $"ab {(intent.AgeFrom ?? 0).ToString(German)} Jahren";

    /// <summary>„1985 – 2005“ (ganze Jahre), „22.10.2021 – 23.10.2026“, „jedes Jahr 24.–26. Dezember“, „ab 2019“.</summary>
    public static string Describe(DateOnly? from, DateOnly? to, MonthDay? everyFrom, MonthDay? everyTo)
    {
        var parts = new List<string>();
        if (everyFrom is { } a && everyTo is { } b)
        {
            var first = new DateOnly(2000, a.Month, a.Day);
            var last = new DateOnly(2000, b.Month, b.Day);
            parts.Add(a == b
                ? "jedes Jahr am " + first.ToString("d. MMMM", German)
                : a.Month == b.Month && a.Day < b.Day
                    ? $"jedes Jahr {a.Day.ToString(German)}.–{last.ToString("d. MMMM", German)}"
                    : $"jedes Jahr {first.ToString("d. MMMM", German)} – {last.ToString("d. MMMM", German)}");
        }

        if (from is not null || to is not null)
        {
            var wholeYears = (from is null || (from.Value.Month == 1 && from.Value.Day == 1)) && (to is null || (to.Value.Month == 12 && to.Value.Day == 31));
            string F(DateOnly d) => d.ToString("dd.MM.yyyy", German);
            parts.Add((from, to) switch
            {
                ({ } f, { } t) when wholeYears => f.Year == t.Year ? f.Year.ToString(German) : $"{f.Year.ToString(German)} – {t.Year.ToString(German)}",
                ({ } f, { } t) => $"{F(f)} – {F(t)}",
                ({ } f, null) => wholeYears ? $"ab {f.Year.ToString(German)}" : $"ab {F(f)}",
                (null, { } t) => wholeYears ? $"bis {t.Year.ToString(German)}" : $"bis {F(t)}",
                _ => string.Empty,
            });
        }

        return string.Join(", ", parts.Where(p => p.Length > 0));
    }

    private static DateOnly? Max(DateOnly? a, DateOnly b) => a is { } x && x > b ? x : b;

    private static DateOnly? Min(DateOnly? a, DateOnly? b) => a is null ? b : b is null ? a : a < b ? a : b;

    // ---- Begriffe ---------------------------------------------------------------------------------------------------

    private async Task<IReadOnlyList<PlaceInfo>> ReadScopePlacesAsync(LibraryScopeInfo scope, CancellationToken ct)
    {
        if (places is null)
        {
            return [];
        }

        var ids = await queries.ReadPlaceIdsAsync(scope, ct).ConfigureAwait(false);
        return ids.Select(places.GetPlace).Where(p => p is not null).Select(p => p!).ToList();
    }

    /// <summary>Treffer eines Begriffs: Katalog (Tag, Kategorie, Wert, Motiv, Person) und Orte; Zähler im Bereich.</summary>
    public async Task<IReadOnlyList<TermMatch>> MatchTermAsync(string text, LibraryScopeInfo scope, AssistantVocabulary vocabulary, IReadOnlyList<PlaceInfo> scopePlaces, CancellationToken ct)
    {
        var matches = (await MatchCatalogAsync(text, scope, vocabulary, ct).ConfigureAwait(false)).ToList();

        // Länder und Regionen, die im Bereich vorkommen
        var countryIds = scopePlaces.Where(p => AssistantText.Same(p.Country, text) || AssistantText.Same(p.CountryCode, text) && text.Trim().Length == 2).Select(p => p.PlaceId).ToList();
        if (countryIds.Count > 0)
        {
            matches.Add(await CountedAsync(new TermMatch(TermMatchKind.Country, scopePlaces.First(p => countryIds.Contains(p.PlaceId)).Country, countryIds, 0), scope, ct).ConfigureAwait(false));
        }

        var regionIds = scopePlaces.Where(p => p.Region is { } r && AssistantText.Same(r, text)).Select(p => p.PlaceId).ToList();
        if (regionIds.Count > 0)
        {
            matches.Add(await CountedAsync(new TermMatch(TermMatchKind.Region, scopePlaces.First(p => regionIds.Contains(p.PlaceId)).Region!, regionIds, 0), scope, ct).ConfigureAwait(false));
        }

        // Ort über die Offline-Ortssuche (nur bei genau passendem Namen — „Hund“ ist kein „Hundsdorf“)
        if (countryIds.Count == 0 && regionIds.Count == 0 && geocoder is not null)
        {
            var hit = geocoder.Search(text, 5).FirstOrDefault(h => AssistantText.Same(h.Place.Name, text));
            if (hit.Place is not null)
            {
                var label = hit.Place.Name + (string.IsNullOrWhiteSpace(hit.Place.Country) ? string.Empty : ", " + hit.Place.Country);
                matches.Add(await CountedAsync(new TermMatch(TermMatchKind.Place, label, [hit.Place.PlaceId], 0, true, hit.Latitude, hit.Longitude, AssistantQueries.PlaceRadiusKm), scope, ct).ConfigureAwait(false));
            }
        }

        // Treffer ohne Fotos im Bereich nur zeigen, wenn es keine anderen gibt
        var withFiles = matches.Where(m => m.Count > 0).ToList();
        return withFiles.Count > 0 ? withFiles : matches;
    }

    /// <summary>Katalog-Treffer (Tag, Kategorie, Kategorie-Wert, Motiv, Person) mit Zählern.</summary>
    private async Task<List<TermMatch>> MatchCatalogAsync(string text, LibraryScopeInfo scope, AssistantVocabulary vocabulary, CancellationToken ct)
    {
        var raw = new List<TermMatch>();
        void Add(TermMatchKind kind, IEnumerable<(int Id, string Label)> items)
        {
            var list = items.ToList();
            if (list.Count > 0)
            {
                raw.Add(new TermMatch(kind, string.Join(", ", list.Select(i => i.Label).Distinct()), list.Select(i => i.Id).Distinct().ToList(), 0));
            }
        }

        Add(TermMatchKind.Tag, vocabulary.Tags.Where(t => AssistantText.Matches(t.Name, text)).Select(t => (t.Id, t.Name)));
        Add(TermMatchKind.Category, vocabulary.Categories.Where(c => AssistantText.Matches(c.Name, text)).Select(c => (c.Id, c.Name)));
        Add(TermMatchKind.CategoryValue, vocabulary.CategoryValues.Where(v => AssistantText.Matches(v.Name, text)).Select(v => (v.Id, v.CategoryName + ": " + v.Name)));
        Add(TermMatchKind.Motif, vocabulary.Motifs.Where(m => AssistantText.Matches(m.Name, text)).Select(m => (m.Id, m.Name)));
        Add(TermMatchKind.Person, vocabulary.Persons.Where(p => p.NameMatches(text)).Select(p => (p.Id, p.Name)));

        var counted = new List<TermMatch>(raw.Count);
        foreach (var match in raw)
        {
            counted.Add(await CountedAsync(match, scope, ct).ConfigureAwait(false));
        }

        return counted;
    }

    private async Task<TermMatch> CountedAsync(TermMatch match, LibraryScopeInfo scope, CancellationToken ct)
        => match with { Count = await queries.CountAsync(scope, match, ct).ConfigureAwait(false) };

    // ---- Zuhause ----------------------------------------------------------------------------------------------------

    private async Task<AssistantQuestion> HomeQuestionAsync(LibraryScopeInfo scope, bool hasCatalogTrips, CancellationToken ct)
    {
        var choices = new List<QuestionChoice>();
        var home = await queries.ReadDetectedHomeAsync(scope, ct).ConfigureAwait(false);
        if (home is not null)
        {
            var name = home.PlaceId is { } id && places?.GetPlace(id) is { } p ? p.Name : "der häufigste Ort Ihrer Fotos";
            var value = home.PlaceId is { } pid
                ? "place:" + pid.ToString(CultureInfo.InvariantCulture)
                : "geo:" + home.Latitude.ToString("R", CultureInfo.InvariantCulture) + "," + home.Longitude.ToString("R", CultureInfo.InvariantCulture);
            choices.Add(new QuestionChoice($"Zuhause ist {name}", "als Zuhause speichern (Einstellungen › Storys)", [], value));
        }

        choices.Add(new QuestionChoice("In Einstellungen › Storys festlegen", "Zuhause selbst wählen", [], "settings"));
        choices.Add(new QuestionChoice(hasCatalogTrips ? "Nur Tags/Kategorien" : "Ohne Reise-Erkennung", "Reisen nicht über das Zuhause erkennen", [], "skip"));
        return new AssistantQuestion(
            QuestionKind.Home, HomeKey, "Wo ist Ihr Zuhause?",
            "Urlaub erkennt Lumora als mehrere Tage weg von zu Hause (Reise-Erkennung der Storys). Ein Zuhause ist noch nicht festgelegt — Lumora rät es nicht.",
            choices);
    }
}
