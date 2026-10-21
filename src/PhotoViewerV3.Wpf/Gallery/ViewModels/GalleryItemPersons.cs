using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using PhotoViewerV3.Gallery.Data;

namespace PhotoViewerV3.Gallery.ViewModels;

/// <summary>
/// Personen auf einer Datei (0.2.38), unveränderlich — <see cref="GalleryItem.Persons"/> wird als Ganzes ersetzt.
/// Bestätigte Personen (Listenspalte normal, Kachel-Symbol mit Anzahl) und Vorschläge (Listenspalte kursiv/grau mit „?“)
/// jeweils nach Name sortiert.
/// </summary>
public sealed partial class GalleryItemPersons
{
    public static GalleryItemPersons Empty { get; } = new([], []);

    /// <param name="unnamedCount">Unbenannte Gesichter der Datei (0.2.39, <c>GalleryItemFaceMarks.cs</c>).</param>
    public GalleryItemPersons(IReadOnlyList<string> confirmed, IReadOnlyList<string> suggested, int unnamedCount = 0)
    {
        Confirmed = confirmed ?? throw new ArgumentNullException(nameof(confirmed));
        Suggested = suggested ?? throw new ArgumentNullException(nameof(suggested));
        UnnamedCount = Math.Max(0, unnamedCount);
    }

    /// <summary>Aus den Zeilen einer Datei (<see cref="GalleryPageSource.GetFaceMarksForFilesAsync"/>).</summary>
    public static GalleryItemPersons From(IEnumerable<GalleryFacePersonRow> rows, int unnamedCount = 0)
    {
        var list = rows.ToList();
        if (list.Count == 0 && unnamedCount <= 0)
        {
            return Empty;
        }

        static List<string> Names(IEnumerable<GalleryFacePersonRow> r)
            => r.Select(x => x.Name).Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();

        return new GalleryItemPersons(Names(list.Where(r => r.IsConfirmed)), Names(list.Where(r => !r.IsConfirmed)), unnamedCount);
    }

    /// <summary>Namen der bestätigten Personen.</summary>
    public IReadOnlyList<string> Confirmed { get; }

    /// <summary>Namen vorgeschlagener (noch nicht bestätigter) Personen.</summary>
    public IReadOnlyList<string> Suggested { get; }

    public bool HasConfirmed => Confirmed.Count > 0;

    public bool HasSuggestions => Suggested.Count > 0;

    public int ConfirmedCount => Confirmed.Count;

    /// <summary>Bestätigte Namen durch Komma (Listenspalte).</summary>
    public string ConfirmedText => string.Join(", ", Confirmed);

    /// <summary>Vorschläge „Anna?, Bert?“ (Listenspalte, kursiv/grau).</summary>
    public string SuggestionsText => string.Join(", ", Suggested.Select(n => n + "?"));

    /// <summary>Trenner zwischen bestätigten Namen und Vorschlägen in der Listenspalte.</summary>
    public string Separator => HasConfirmed && HasSuggestions ? ", " : string.Empty;

    /// <summary>Sortierschlüssel der Listenspalte (innerhalb der Page): bestätigte Namen, dann nur Vorschläge, dann leer.</summary>
    public GalleryPersonsSortKey SortKey => new(HasConfirmed ? 0 : HasSuggestions ? 1 : 2, HasConfirmed ? ConfirmedText : SuggestionsText);

    /// <summary>Tooltip des Kachel-Symbols bzw. der Listenzelle.</summary>
    public string ToolTip
    {
        get
        {
            var parts = new List<string>(2);
            if (HasConfirmed)
            {
                parts.Add("Personen: " + ConfirmedText);
            }

            if (HasSuggestions)
            {
                parts.Add("Vorschläge: " + SuggestionsText);
            }

            return parts.Count == 0 ? "Keine Personen" : string.Join(Environment.NewLine, parts);
        }
    }

    public override string ToString() => HasSuggestions ? $"{ConfirmedText}{Separator}{SuggestionsText}" : ConfirmedText;
}

/// <summary>
/// Sortierschlüssel der Spalte „Personen“: Gruppe (0 bestätigt, 1 nur Vorschläge, 2 keine) vor dem Text
/// (Kulturvergleich ohne Groß-/Kleinschreibung) — ein Präfixzeichen sortierte im Kulturvergleich unzuverlässig.
/// </summary>
public sealed record GalleryPersonsSortKey(int Group, string Text) : IComparable
{
    public int CompareTo(object? obj)
    {
        if (obj is not GalleryPersonsSortKey other)
        {
            return 1;
        }

        var byGroup = Group.CompareTo(other.Group);
        return byGroup != 0 ? byGroup : string.Compare(Text, other.Text, StringComparison.CurrentCultureIgnoreCase);
    }

    public override string ToString() => Text;
}

/// <summary>Personen der Datei (0.2.38) — eigene Datei, damit der Kern von <see cref="GalleryItem"/> unverändert bleibt.</summary>
public sealed partial class GalleryItem
{
    /// <summary>Bestätigte Personen und Vorschläge (Listenspalte „Personen“, Kachel-Symbol); wird als Ganzes ersetzt.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PersonsSortKey))]
    private GalleryItemPersons _persons = GalleryItemPersons.Empty;

    /// <summary>Sortierschlüssel der Spalte „Personen“ (Sortierung innerhalb der Page).</summary>
    public GalleryPersonsSortKey PersonsSortKey => Persons.SortKey;

    /// <summary>Laufnummer der Abfrage, deren Ergebnis <see cref="Persons"/> zuletzt gesetzt hat (ältere werden verworfen).</summary>
    internal long PersonsSequence { get; set; }
}
