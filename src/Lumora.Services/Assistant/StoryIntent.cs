namespace Lumora.Services.Assistant;

/// <summary>Was am Ende entsteht (ADR-0063). Numerische Werte stehen im Verlauf (<c>AssistantHistory.Action</c>).</summary>
public enum IntentAction
{
    Story = 0,
    Collage = 1,
    Album = 2,
    Slideshow = 3,
    Gallery = 4,
}

/// <summary>Verknüpfung mehrerer Personen bzw. Begriffe.</summary>
public enum IntentMatch
{
    /// <summary>Alle zusammen (Standard bei „mit Nicole und Nina“).</summary>
    All = 0,

    /// <summary>Mindestens eine bzw. einer.</summary>
    Any = 1,
}

/// <summary>Weitere (andere bestätigte) Personen auf dem Foto.</summary>
public enum IntentOthers
{
    /// <summary>Egal.</summary>
    Any = 0,

    /// <summary>Nur die genannten Personen („nur wir beide“).</summary>
    None = 1,
}

/// <summary>Medien.</summary>
public enum IntentMedia
{
    All = 0,
    Photos = 1,
    Videos = 2,
}

/// <summary>Eine Person aus dem Satz: Name, Beziehung („meine Schwester“) oder der Benutzer selbst („ich“).</summary>
public sealed record IntentPerson(string? Name = null, string? Relation = null, bool IsMe = false)
{
    /// <summary>Text für Anzeige und gemerkte Antworten: Name, sonst Beziehung, sonst „ich“.</summary>
    public string Text => !string.IsNullOrWhiteSpace(Name) ? Name.Trim() : !string.IsNullOrWhiteSpace(Relation) ? Relation.Trim() : "ich";
}

/// <summary>Wiederkehrender Tag im Jahr („Weihnachten“ = 24.12.–26.12.).</summary>
public readonly record struct MonthDay(int Month, int Day)
{
    /// <summary>Schlüssel zum Vergleichen (MMDD).</summary>
    public int Key => Month * 100 + Day;

    public static bool TryParse(string? text, out MonthDay value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Trim().Split('-', '.', '/');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var m) || !int.TryParse(parts[1], out var d))
        {
            return false;
        }

        if (m is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(2000, m))
        {
            return false;
        }

        value = new MonthDay(m, d);
        return true;
    }

    public override string ToString() => $"{Month:00}-{Day:00}";
}

/// <summary>
/// Strukturierte Absicht eines Satzes (ADR-0063) — das EINZIGE, was die Sprach-KI liefert: Personen-Bezüge, Begriffe
/// (Orte, Motive, Tags, Kategorien als Text), Zeitraum (absolut, relativ „letzte N Jahre“, Alter einer Person,
/// wiederkehrende Tage), Reise/Urlaub, Medien, „die besten N“, Aktion. Kein Titel, keine Beschreibung — die Überschrift
/// baut Lumora sachlich. Aufgelöst wird alles in Lumora (<see cref="StoryIntentResolver"/>).
/// </summary>
public sealed record StoryIntent
{
    public IntentAction Action { get; init; } = IntentAction.Story;

    public IReadOnlyList<IntentPerson> Persons { get; init; } = [];

    public IntentMatch PersonMatch { get; init; } = IntentMatch.All;

    public IntentOthers Others { get; init; } = IntentOthers.Any;

    /// <summary>Begriffe als Text (Ort, Motiv, Tag, Kategorie …) — zugeordnet wird in Lumora.</summary>
    public IReadOnlyList<string> Terms { get; init; } = [];

    public IntentMatch TermMatch { get; init; } = IntentMatch.All;

    /// <summary>Urlaub/Reise/Ferien — über die Reise-Erkennung der Storys (plus passende Tags/Kategorien).</summary>
    public bool Trip { get; init; }

    public DateOnly? DateFrom { get; init; }

    public DateOnly? DateTo { get; init; }

    /// <summary>„Die letzten N Jahre“ relativ zu heute (0 = keine Angabe).</summary>
    public int LastYears { get; init; }

    /// <summary>Alter von (Jahre, null = keine Angabe).</summary>
    public int? AgeFrom { get; init; }

    /// <summary>Alter bis einschließlich (Jahre, null = keine Angabe).</summary>
    public int? AgeTo { get; init; }

    /// <summary>Wessen Alter (Name, Beziehung oder „ich“); leer = die erste genannte Person.</summary>
    public string? AgePerson { get; init; }

    public MonthDay? EveryYearFrom { get; init; }

    public MonthDay? EveryYearTo { get; init; }

    public IntentMedia Media { get; init; } = IntentMedia.All;

    /// <summary>„Die besten 50“ (null = keine Zahl).</summary>
    public int? Best { get; init; }

    /// <summary>„Die schönsten/besten“ ohne Zahl — Lumora schlägt eine Anzahl vor.</summary>
    public bool BestWanted { get; init; }

    public int MinRating { get; init; }

    public bool FavoritesOnly { get; init; }

    public bool HasAge => AgeFrom is not null || AgeTo is not null;

    public bool HasRecurring => EveryYearFrom is not null && EveryYearTo is not null;

    public bool HasTime => DateFrom is not null || DateTo is not null || LastYears > 0 || HasAge || HasRecurring;
}
