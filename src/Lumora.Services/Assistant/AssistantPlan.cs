using Lumora.Domain.Entities;

namespace Lumora.Services.Assistant;

/// <summary>Person der Gruppe, wie der Assistent sie auflöst (Geburtsdatum bleibt in Lumora, geht nie an die Sprach-KI).</summary>
/// <param name="FileCount">Fotos und Videos mit bestätigtem Gesicht im aktuellen Bereich.</param>
/// <param name="FaceId">Titel-Gesicht (sonst ein bestätigtes) für das Rundbild der Rückfrage.</param>
/// <param name="FaceLibraryId">Bibliothek der Datei des Gesichts (Ausschnitt-Cache).</param>
public sealed record AssistantPerson(
    int Id,
    string Name,
    string FirstName,
    string LastName,
    string? Nickname,
    string? Relation,
    PersonBirthDate? BirthDate,
    bool IsMe,
    int FileCount,
    int? FaceId = null,
    int? FaceLibraryId = null)
{
    /// <summary>Passt der Name aus dem Satz (Anzeige-, Vor-, Ruf-, Nachname oder „Vor Nach“)?</summary>
    public bool NameMatches(string text)
        => AssistantText.Same(Name, text) || AssistantText.Same(FirstName, text) || AssistantText.Same(Nickname, text)
           || AssistantText.Same(FirstName + " " + LastName, text) || (LastName.Length > 0 && AssistantText.Same(LastName, text));
}

/// <summary>Benannter Begriff des Katalogs (Tag, Kategorie, Motiv).</summary>
public sealed record AssistantNamedItem(int Id, string Name);

/// <summary>Wert einer Kategorie.</summary>
public sealed record AssistantCategoryValue(int Id, int CategoryId, string Name, string CategoryName);

/// <summary>
/// Wortschatz des aktuellen Bereichs (ADR-0063): Personen, Tags, Kategorien samt Werten, Motiv-Begriffe und gemerkte
/// Antworten — alles aus der Gruppe des Bereichs (ADR-0048), im versteckten Modus aus dem versteckten Katalog.
/// </summary>
public sealed record AssistantVocabulary(
    IReadOnlyList<AssistantPerson> Persons,
    IReadOnlyList<AssistantNamedItem> Tags,
    IReadOnlyList<AssistantNamedItem> Categories,
    IReadOnlyList<AssistantCategoryValue> CategoryValues,
    IReadOnlyList<AssistantNamedItem> Motifs,
    IReadOnlyDictionary<string, IReadOnlyList<int>> Aliases)
{
    public static AssistantVocabulary Empty { get; } = new([], [], [], [], [], new Dictionary<string, IReadOnlyList<int>>());

    /// <summary>Was die Sprach-KI sieht: Personen (Name, Rufname, Beziehung, ich) und die Namen der Begriffe.</summary>
    public IntentPrompt ToPrompt(string sentence, DateOnly today)
        => new(
            sentence,
            today,
            Persons.OrderByDescending(p => p.FileCount).Take(IntentPrompt.MaxPersons)
                .Select(p => new IntentVocabularyPerson(p.Name, p.Nickname, p.Relation, p.IsMe)).ToList(),
            Motifs.Select(m => m.Name).Concat(Tags.Select(t => t.Name)).Concat(Categories.Select(c => c.Name)).Concat(CategoryValues.Select(v => v.Name))
                .Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).Take(IntentPrompt.MaxTerms).ToList());
}

/// <summary>Worauf ein Begriff passt (ADR-0063, Nachtrag: Tags und Kategorien).</summary>
public enum TermMatchKind
{
    Tag = 0,
    Category = 1,
    CategoryValue = 2,
    Place = 3,
    Region = 4,
    Country = 5,
    Motif = 6,
    Person = 7,

    /// <summary>Reise-Erkennung der Storys (Kind = Reise).</summary>
    TripDetection = 8,
}

/// <summary>Ein Treffer eines Begriffs im Katalog, mit Anzahl im Bereich; <see cref="Selected"/> = wirkt (Standard: alle, ODER).</summary>
/// <param name="Ids">Tag-, Kategorie-, Wert-, Motiv- bzw. Personen-Ids; bei Ländern/Regionen die Orte (GeoNames-Ids).</param>
/// <param name="Latitude">Ortsmitte (nur <see cref="TermMatchKind.Place"/>).</param>
public sealed record TermMatch(TermMatchKind Kind, string Label, IReadOnlyList<int> Ids, int Count, bool Selected = true, double? Latitude = null, double? Longitude = null, double RadiusKm = 0)
{
    public string KindLabel => Kind switch
    {
        TermMatchKind.Tag => "Tag",
        TermMatchKind.Category => "Kategorie",
        TermMatchKind.CategoryValue => "Kategorie",
        TermMatchKind.Place => "Ort",
        TermMatchKind.Region => "Region",
        TermMatchKind.Country => "Land",
        TermMatchKind.Motif => "Motiv",
        TermMatchKind.Person => "Person",
        _ => "Reise-Erkennung",
    };

    /// <summary>„Tag (212)“.</summary>
    public string Text => $"{KindLabel} ({Count:N0})";
}

/// <summary>Ein Begriff aus dem Satz mit seinen Treffern; ohne Treffer = „nicht gefunden“ (wirkt nicht, wird aber gezeigt).</summary>
public sealed record PlanTerm(string Text, IReadOnlyList<TermMatch> Matches)
{
    public bool NotFound => Matches.Count == 0;

    public bool IsActive => Matches.Any(m => m.Selected);
}

/// <summary>Zeitraum in Lumora aufgelöst.</summary>
/// <param name="From">Erster Tag (inklusive) oder null.</param>
/// <param name="To">Letzter Tag (inklusive) oder null.</param>
/// <param name="EveryYearFrom">Wiederkehrend: jedes Jahr von … bis … (über den Jahreswechsel möglich).</param>
/// <param name="Description">„1985 – 2005“, „jedes Jahr 24.–26. Dezember“.</param>
/// <param name="Source">Herkunft für die Karte: „Geburt bis 20 Jahre (aus Annas Geburtsdatum)“, „letzte 5 Jahre“.</param>
public sealed record PlanTime(DateOnly? From, DateOnly? To, MonthDay? EveryYearFrom, MonthDay? EveryYearTo, string Description, string? Source)
{
    public bool IsRecurring => EveryYearFrom is not null && EveryYearTo is not null;

    /// <summary>Liegt der Tag im Zeitraum?</summary>
    public bool Contains(DateOnly day)
    {
        if (From is { } f && day < f)
        {
            return false;
        }

        if (To is { } t && day > t)
        {
            return false;
        }

        if (EveryYearFrom is { } a && EveryYearTo is { } b)
        {
            var key = day.Month * 100 + day.Day;
            return a.Key <= b.Key ? key >= a.Key && key <= b.Key : key >= a.Key || key <= b.Key;
        }

        return true;
    }
}

/// <summary>Urlaub/Reise: Reise-Erkennung der Storys und/oder passende Tags/Kategorien (Standard: vereinigt).</summary>
public sealed record PlanTrip(bool UseDetection, int DetectionCount, IReadOnlyList<TermMatch> Extra)
{
    public bool IsActive => UseDetection || Extra.Any(m => m.Selected);
}

/// <summary>
/// Was Lumora verstanden hat (ADR-0063) — Grundlage der Karte „So habe ich es verstanden“ und der Abfrage. Jeder Punkt
/// lässt sich in der Karte ändern (neue Instanz über <c>with</c>), danach zählt Lumora neu, ohne die Sprach-KI zu fragen.
/// </summary>
public sealed record AssistantPlan
{
    public IntentAction Action { get; init; } = IntentAction.Story;

    /// <summary>Bei einer Collage zusätzlich eine eigene Story anlegen (Mockup: „Story … + Collage-Bild“).</summary>
    public bool AlsoStory { get; init; }

    public IReadOnlyList<AssistantPerson> Persons { get; init; } = [];

    public IntentMatch PersonMatch { get; init; } = IntentMatch.All;

    public IntentOthers Others { get; init; } = IntentOthers.Any;

    public IReadOnlyList<PlanTerm> Terms { get; init; } = [];

    public IntentMatch TermMatch { get; init; } = IntentMatch.All;

    public PlanTrip? Trip { get; init; }

    public PlanTime? Time { get; init; }

    public IntentMedia Media { get; init; } = IntentMedia.All;

    /// <summary>„Die besten N“ (null = alle).</summary>
    public int? Best { get; init; }

    /// <summary>Lumora hat die Anzahl vorgeschlagen („die schönsten“ ohne Zahl).</summary>
    public bool BestSuggested { get; init; }

    public int MinRating { get; init; }

    public bool FavoritesOnly { get; init; }

    /// <summary>Wirkt überhaupt ein Filter? Ohne Filter wäre „alle Fotos“ gemeint — die Karte weist darauf hin.</summary>
    public bool HasFilter => Persons.Count > 0 || Terms.Any(t => t.IsActive) || Trip?.IsActive == true || Time is not null
                             || Media != IntentMedia.All || MinRating > 0 || FavoritesOnly;
}

/// <summary>Art einer Rückfrage.</summary>
public enum QuestionKind
{
    /// <summary>Welche Person ist gemeint (mehrdeutig oder unbekannt)? Antwort wird als Alias gemerkt.</summary>
    Person = 0,

    /// <summary>Kein Geburtsdatum für einen Zeitraum „bis 20 Jahre“.</summary>
    BirthDate = 1,

    /// <summary>Kein Zuhause eingestellt — Reisen lassen sich so nicht sicher erkennen.</summary>
    Home = 2,
}

/// <summary>Auswahl einer Rückfrage.</summary>
/// <param name="PersonIds">Personen dieser Antwort (mehrere = „beide“).</param>
/// <param name="Value">Sonstige Antwort: „skip“ (ohne), „settings“ (Einstellungen öffnen), „place:&lt;id&gt;“ (Zuhause).</param>
public sealed record QuestionChoice(string Label, string? Detail, IReadOnlyList<int> PersonIds, string? Value = null, int? FaceId = null, int? FaceLibraryId = null);

/// <summary>Rückfrage statt Raten (ADR-0063, Mockup 12 „Wer ist „Oma“?“).</summary>
/// <param name="Key">Wofür: der Personen-Text bzw. „home“/„birth:&lt;Person&gt;“ — damit die Antwort beim nächsten Auflösen greift.</param>
public sealed record AssistantQuestion(QuestionKind Kind, string Key, string Title, string Text, IReadOnlyList<QuestionChoice> Choices, string? Note = null);

/// <summary>Antworten auf Rückfragen dieser Anfrage (Personen merkt sich Lumora dauerhaft als Alias).</summary>
public sealed class AssistantAnswers
{
    /// <summary>Schlüssel der Rückfragen, die übersprungen wurden („ohne diese Person“, „ohne Zeitraum“, „ohne Reise-Erkennung“).</summary>
    public HashSet<string> Skipped { get; } = new(StringComparer.Ordinal);

    /// <summary>Personen-Antworten nur für diese Anfrage (wenn der Verlauf/Alias nicht gemerkt werden soll).</summary>
    public Dictionary<string, IReadOnlyList<int>> Persons { get; } = new(StringComparer.Ordinal);
}

/// <summary>Ergebnis der Auflösung: Plan und ggf. offene Rückfragen (die erste wird gezeigt).</summary>
public sealed record AssistantResolution(AssistantPlan Plan, IReadOnlyList<AssistantQuestion> Questions)
{
    public AssistantQuestion? Question => Questions.Count > 0 ? Questions[0] : null;
}

/// <summary>Treffer eines Plans im aktuellen Bereich.</summary>
/// <param name="FileIds">Alle Treffer in Aufnahme-Reihenfolge.</param>
/// <param name="Selected">Was entsteht: „die besten N“ (in Aufnahme-Reihenfolge) bzw. alle.</param>
/// <param name="PreviewIds">Bis zu 8 gute Fotos für den Vorschau-Streifen.</param>
/// <param name="SimilarSkipped">Weggelassene ähnliche Fotos (Serien/Doppelte) bei „die besten N“.</param>
public sealed record AssistantResult(IReadOnlyList<int> FileIds, IReadOnlyList<int> Selected, IReadOnlyList<int> PreviewIds, int PhotoCount, int VideoCount, int SimilarSkipped)
{
    public static AssistantResult Empty { get; } = new([], [], [], 0, 0, 0);

    public int Count => FileIds.Count;

    /// <summary>Erstes und letztes Aufnahmejahr der gewählten Dateien (Ortszeit) — für die sachliche Überschrift.</summary>
    public (int From, int To)? Years { get; init; }
}
