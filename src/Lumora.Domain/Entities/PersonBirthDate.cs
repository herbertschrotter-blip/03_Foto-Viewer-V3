using System.Globalization;

namespace Lumora.Domain.Entities;

/// <summary>
/// Geburtsdatum einer Person, auch ungefähr (ADR-0045): nur Jahr, Jahr + Monat oder vollständig. Ein Tag ohne Monat ist
/// ungültig. Vorbereitung für „Alter auf einem Foto“ — <see cref="AgeOn"/> liefert deshalb eine Spanne (bei ungefähren
/// Daten kann das Alter an einem Stichtag um ein Jahr schwanken).
/// </summary>
public readonly record struct PersonBirthDate
{
    /// <summary>Frühestes zulässiges Geburtsjahr (alte Familienfotos).</summary>
    public const int MinYear = 1800;

    private static readonly string[] MonthNames = CultureInfo.GetCultureInfo("de-DE").DateTimeFormat.MonthNames;

    private PersonBirthDate(int year, int? month, int? day)
    {
        Year = year;
        Month = month;
        Day = day;
    }

    public int Year { get; }

    public int? Month { get; }

    public int? Day { get; }

    public bool IsExact => Day is not null;

    /// <summary>
    /// Prüft und baut das Datum. <paramref name="year"/> null → kein Datum (gültig, <paramref name="date"/> = null).
    /// Fehlertext auf Deutsch für den Dialog.
    /// </summary>
    public static bool TryCreate(int? year, int? month, int? day, int currentYear, out PersonBirthDate? date, out string? error)
    {
        date = null;
        error = null;
        if (year is null)
        {
            if (month is not null || day is not null)
            {
                error = "Für Tag oder Monat bitte auch das Jahr angeben.";
                return false;
            }

            return true;
        }

        if (year < MinYear || year > currentYear)
        {
            error = $"Das Geburtsjahr muss zwischen {MinYear} und {currentYear} liegen.";
            return false;
        }

        if (month is not null && (month < 1 || month > 12))
        {
            error = "Der Monat muss zwischen 1 und 12 liegen.";
            return false;
        }

        if (day is not null)
        {
            if (month is null)
            {
                error = "Für einen Tag bitte auch den Monat angeben.";
                return false;
            }

            if (day < 1 || day > DateTime.DaysInMonth(year.Value, month.Value))
            {
                error = $"Den {day}. gibt es im {MonthNames[month.Value - 1]} {year} nicht.";
                return false;
            }
        }

        date = new PersonBirthDate(year.Value, month, day);
        return true;
    }

    /// <summary>Wie <see cref="TryCreate"/>, wirft bei ungültigen Werten.</summary>
    public static PersonBirthDate? Create(int? year, int? month, int? day, int? currentYear = null)
        => TryCreate(year, month, day, currentYear ?? DateTime.Today.Year, out var date, out var error)
            ? date
            : throw new ArgumentException(error);

    /// <summary>Gespeicherte Werte (aus dem Katalog) ohne Prüfung gegen das aktuelle Jahr; ungültige → null.</summary>
    public static PersonBirthDate? FromStored(int? year, int? month, int? day)
        => TryCreate(year, month, day, int.MaxValue - 1, out var date, out _) ? date : null;

    /// <summary>„12.03.1980“, „März 1980“ oder „1980“.</summary>
    public string Format()
        => Day is { } d && Month is { } m
            ? new DateOnly(Year, m, d).ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)
            : Month is { } month
                ? $"{MonthNames[month - 1]} {Year}"
                : Year.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Alter am Stichtag als Spanne (Min = Max bei eindeutigem Alter). Vor der Geburt (bzw. im Geburtsjahr vor dem
    /// Monat) → null.
    /// </summary>
    public (int Min, int Max)? AgeOn(DateOnly date)
    {
        var years = date.Year - Year;
        int min, max;
        if (Month is not { } month)
        {
            (min, max) = (years - 1, years);
        }
        else if (date.Month != month)
        {
            min = max = date.Month > month ? years : years - 1;
        }
        else if (Day is { } day)
        {
            min = max = date.Day >= day ? years : years - 1;
        }
        else
        {
            (min, max) = (years - 1, years);
        }

        if (max < 0)
        {
            return null;
        }

        return (Math.Max(0, min), max);
    }

    /// <summary>„46 Jahre“, „etwa 45–46 Jahre“; null vor der Geburt.</summary>
    public string? FormatAgeOn(DateOnly date)
    {
        if (AgeOn(date) is not { } age)
        {
            return null;
        }

        return age.Min == age.Max
            ? age.Max == 1 ? "1 Jahr" : $"{age.Max} Jahre"
            : $"etwa {age.Min}–{age.Max} Jahre";
    }
}
