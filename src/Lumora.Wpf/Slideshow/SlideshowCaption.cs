using System.Globalization;
using Lumora.Domain.Enums;
using Lumora.Services.Metadata;

namespace Lumora.Slideshow;

/// <summary>Einblendung unten links: Datumszeile und Ortszeile (beide optional).</summary>
public sealed record SlideshowCaption(string? DateText, string? PlaceText)
{
    public static readonly SlideshowCaption Empty = new(null, null);

    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    public bool HasDate => !string.IsNullOrWhiteSpace(DateText);

    public bool HasPlace => !string.IsNullOrWhiteSpace(PlaceText);

    public bool IsEmpty => !HasDate && !HasPlace;

    /// <summary>Datum und Ort einer Datei; leerer Ort → nur Datum, unbekanntes Datum → nur Ort.</summary>
    public static SlideshowCaption For(CaptureDateValue? date, string? place)
        => new(FormatDate(date), string.IsNullOrWhiteSpace(place) ? null : place.Trim());

    /// <summary>
    /// Sachliches Datum wie fotografiert (Wanduhrzeit): genau und Tag „Samstag, 12. Juli 2025“; ungefähre Angaben nur so
    /// genau wie bekannt — Monat „Juli 1982“, Jahr „1982“. Deutsch, unabhängig von der Spracheinstellung von Windows.
    /// </summary>
    public static string? FormatDate(CaptureDateValue? date)
    {
        if (date is not { } value)
        {
            return null;
        }

        var local = value.Local;
        return value.Precision switch
        {
            CaptureDatePrecision.Year => local.ToString("yyyy", German),
            CaptureDatePrecision.Month => local.ToString("MMMM yyyy", German),
            _ => local.ToString("dddd, d. MMMM yyyy", German),
        };
    }
}
