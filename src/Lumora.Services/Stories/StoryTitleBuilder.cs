using System.Globalization;
using Lumora.Domain.Enums;

namespace Lumora.Services.Stories;

/// <summary>
/// Sachliche deutsche Überschriften der Storys (ADR-0058) — nur aus Datum, Ortsnamen (GeoNames, ADR-0050) und
/// Personennamen, nie erfundener Text. Beispiele: „Samstag, 6. September 2025“, „12.–19. Juli 2025“,
/// „28. Juli – 9. August 2024“, „30. Dezember 2024 – 2. Januar 2025“; Orte „Grado · Triest · Aquileia — Italien“ bzw.
/// „Grado, Triest und 2 weitere“; „Anna · 2019“, „Max · 2021–2023“, „Grado · 2014–2025“, „Heute vor 3 Jahren“.
/// Ungefähre Daten zeigen nur, was bekannt ist („Juli 2025“, „2025“).
/// </summary>
public static class StoryTitleBuilder
{
    /// <summary>Gedankenstrich (bis-Strich) zwischen Zahlen.</summary>
    public const string EnDash = "–";

    /// <summary>Trenner zwischen Orten bzw. Name und Jahr.</summary>
    public const string Dot = " · ";

    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    /// <summary>„Samstag, 6. September 2025“ (ohne Jahr: „Samstag, 6. September“).</summary>
    public static string Day(DateOnly date, bool withYear = true)
        => date.ToString(withYear ? "dddd, d. MMMM yyyy" : "dddd, d. MMMM", German);

    /// <summary>Zeitraum: ein Tag mit Wochentag, sonst „12.–19. Juli 2025“, „28. Juli – 9. August 2024“, „30. Dezember 2024 – 2. Januar 2025“.</summary>
    public static string Range(DateOnly first, DateOnly last, CaptureDatePrecision precision = CaptureDatePrecision.Exact)
    {
        if (last < first)
        {
            (first, last) = (last, first);
        }

        switch (precision)
        {
            case CaptureDatePrecision.Year:
                return first.Year == last.Year
                    ? first.Year.ToString(CultureInfo.InvariantCulture)
                    : first.Year.ToString(CultureInfo.InvariantCulture) + EnDash + last.Year.ToString(CultureInfo.InvariantCulture);
            case CaptureDatePrecision.Month:
                return first.Year == last.Year && first.Month == last.Month
                    ? first.ToString("MMMM yyyy", German)
                    : first.ToString(first.Year == last.Year ? "MMMM" : "MMMM yyyy", German) + " " + EnDash + " " + last.ToString("MMMM yyyy", German);
        }

        if (first == last)
        {
            return Day(first);
        }

        if (first.Year == last.Year && first.Month == last.Month)
        {
            return first.Day.ToString(CultureInfo.InvariantCulture) + "." + EnDash + last.ToString("d. MMMM yyyy", German);
        }

        if (first.Year == last.Year)
        {
            return first.ToString("d. MMMM", German) + " " + EnDash + " " + last.ToString("d. MMMM yyyy", German);
        }

        return first.ToString("d. MMMM yyyy", German) + " " + EnDash + " " + last.ToString("d. MMMM yyyy", German);
    }

    /// <summary>Kurzer Tag für die Tagesleiste der Story-Ansicht: „Sa 12.“.</summary>
    public static string ShortDay(DateOnly date)
        => date.ToString("ddd", German).TrimEnd('.') + " " + date.Day.ToString(CultureInfo.InvariantCulture) + ".";

    /// <summary>
    /// Orte: 1–3 Namen mit „ · “, mehr als drei als „A, B und N weitere“; <paramref name="country"/> (nur bei einem
    /// einzigen Land außerhalb von zu Hause) als „ — Italien“. Null ohne Orte.
    /// </summary>
    public static string? Places(IReadOnlyList<string> names, string? country = null)
    {
        ArgumentNullException.ThrowIfNull(names);
        var distinct = names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (distinct.Count == 0)
        {
            return string.IsNullOrWhiteSpace(country) ? null : country;
        }

        var text = distinct.Count <= 3
            ? string.Join(Dot, distinct)
            : distinct[0] + ", " + distinct[1] + " und " + (distinct.Count - 2).ToString(CultureInfo.InvariantCulture) + " weitere";
        return string.IsNullOrWhiteSpace(country) ? text : text + " — " + country;
    }

    /// <summary>„Anna · 2019“ bzw. „Max · 2021–2023“.</summary>
    public static string Person(string name, int fromYear, int toYear) => name + Dot + Years(fromYear, toYear);

    /// <summary>„Grado · 2014–2025“.</summary>
    public static string Place(string name, int fromYear, int toYear) => name + Dot + Years(fromYear, toYear);

    /// <summary>„2019“ bzw. „2021–2023“.</summary>
    public static string Years(int fromYear, int toYear)
        => fromYear == toYear
            ? fromYear.ToString(CultureInfo.InvariantCulture)
            : Math.Min(fromYear, toYear).ToString(CultureInfo.InvariantCulture) + EnDash + Math.Max(fromYear, toYear).ToString(CultureInfo.InvariantCulture);

    /// <summary>„Heute vor einem Jahr“ bzw. „Heute vor 3 Jahren“.</summary>
    public static string YearsAgo(int years)
        => years == 1 ? "Heute vor einem Jahr" : "Heute vor " + years.ToString(CultureInfo.InvariantCulture) + " Jahren";

    /// <summary>„Oktober in früheren Jahren“.</summary>
    public static string MonthInEarlierYears(int month)
        => new DateOnly(2000, month, 1).ToString("MMMM", German) + " in früheren Jahren";

    /// <summary>Gespeicherter Monats-Rückblick: „Oktober · 2015–2024“.</summary>
    public static string MonthYears(int month, int fromYear, int toYear)
        => new DateOnly(2000, month, 1).ToString("MMMM", German) + Dot + Years(fromYear, toYear);

    /// <summary>Anzeigename der Art: „Reise“, „Ereignis“, „Ausflug“, „Rückblick“, „Person“, „Ort“, „Eigene Story“.</summary>
    public static string KindLabel(StoryKind kind, bool isAway)
        => kind switch
        {
            StoryKind.Trip => "Reise",
            StoryKind.Event => isAway ? "Ausflug" : "Ereignis",
            StoryKind.Memory => "Rückblick",
            StoryKind.Person => "Person",
            StoryKind.Place => "Ort",
            _ => "Eigene Story",
        };

    /// <summary>„421 Fotos · 9 Videos“, „1 Foto“, „58 Fotos“ (Fotos = alle Nicht-Videos).</summary>
    public static string Counts(int fileCount, int videoCount)
    {
        var photos = Math.Max(0, fileCount - videoCount);
        var parts = new List<string>(2);
        if (photos > 0 || videoCount == 0)
        {
            parts.Add(photos.ToString("N0", German) + (photos == 1 ? " Foto" : " Fotos"));
        }

        if (videoCount > 0)
        {
            parts.Add(videoCount.ToString("N0", German) + (videoCount == 1 ? " Video" : " Videos"));
        }

        return string.Join(Dot, parts);
    }
}
