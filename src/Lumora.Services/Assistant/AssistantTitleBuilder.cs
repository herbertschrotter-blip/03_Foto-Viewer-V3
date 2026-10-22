using Lumora.Services.Stories;

namespace Lumora.Services.Assistant;

/// <summary>
/// Sachliche Überschrift aus dem Verständnis (ADR-0063) — wie <see cref="StoryTitleBuilder"/> nur aus Namen, Begriffen des
/// Benutzers, Zeitraum und Ort, nie erfundener Text: „Anna · 1985–2005“, „Nicole und Nina · Urlaub 2021–2026“,
/// „Sonnenuntergang · Meer · 2014–2025“, „Oma · 24.–26. Dezember · 2005–2024“. Leer, wenn nichts davon da ist (dann
/// bildet der Story-Dienst die Überschrift aus Datum und Ort).
/// </summary>
public static class AssistantTitleBuilder
{
    public static string Build(AssistantPlan plan, (int From, int To)? years)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var parts = new List<string>();
        if (plan.Persons.Count > 0)
        {
            parts.Add(Names(plan.Persons.Select(p => p.Name).ToList()));
        }

        foreach (var term in plan.Terms.Where(t => t.IsActive))
        {
            var place = term.Matches.FirstOrDefault(m => m.Selected && m.Kind is TermMatchKind.Place or TermMatchKind.Country or TermMatchKind.Region);
            parts.Add(place is not null && term.Matches.Count(m => m.Selected) == 1 ? place.Label.Split(',')[0].Trim() : Capitalize(term.Text));
        }

        if (plan.Time is { IsRecurring: true } time)
        {
            parts.Add(time.Description.Replace("jedes Jahr am ", string.Empty, StringComparison.Ordinal).Replace("jedes Jahr ", string.Empty, StringComparison.Ordinal).Split(',')[0]);
        }

        var yearText = years is { } y ? StoryTitleBuilder.Years(y.From, y.To) : null;
        if (plan.Trip is { IsActive: true })
        {
            parts.Add(yearText is null ? "Urlaub" : "Urlaub " + yearText);
        }
        else if (yearText is not null && parts.Count > 0)
        {
            parts.Add(yearText);
        }

        var title = string.Join(StoryTitleBuilder.Dot, parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        return title.Length > StoryService.MaxTitleLength ? title[..StoryService.MaxTitleLength].TrimEnd() : title;
    }

    /// <summary>„Anna“, „Nicole und Nina“, „Anna, Max und Lea“.</summary>
    public static string Names(IReadOnlyList<string> names)
        => names.Count switch
        {
            0 => string.Empty,
            1 => names[0],
            _ => string.Join(", ", names.Take(names.Count - 1)) + " und " + names[^1],
        };

    private static string Capitalize(string text)
    {
        var t = text.Trim();
        return t.Length == 0 ? t : char.ToUpper(t[0], System.Globalization.CultureInfo.GetCultureInfo("de-DE")) + t[1..];
    }
}
