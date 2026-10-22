using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Lumora.Services.Places;
using Lumora.Services.Places.Suggestions;

namespace Lumora.Places.Suggestions;

/// <summary>Filter-Chips der Liste „Ungenau“ (Mockup 10).</summary>
public enum SuggestionFilter
{
    All,
    Track,
    Sure,
    Likely,
    None,
}

/// <summary>Bereich der Seitenleiste.</summary>
public enum PlacesSideMode
{
    /// <summary>„Ohne Ort“: Kacheln zum Ziehen (ADR-0051).</summary>
    Unlocated,

    /// <summary>„Ungenau“: geschätzte Positionen mit Vorschlägen.</summary>
    Inaccurate,

    /// <summary>„Ohne Ort“ mit Vorschlag (Liste wie „Ungenau“).</summary>
    UnlocatedSuggestions,
}

/// <summary>Ein Filter-Chip mit Zähler.</summary>
public sealed partial class SuggestionFilterChip(SuggestionFilter filter, string label) : ObservableObject
{
    public SuggestionFilter Filter { get; } = filter;

    public string Label { get; } = label;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Text))]
    private int _count;

    [ObservableProperty] private bool _isSelected;

    public string Text => Filter == SuggestionFilter.All ? Label : $"{Label} {PlacesSuggestionText.N(Count)}";
}

/// <summary>Kopfzeile eines Tages bzw. Ausflugs.</summary>
public sealed partial class SuggestionGroupRow(string key, string title) : ObservableObject
{
    public string Key { get; } = key;

    public string Title { get; } = title;

    [ObservableProperty] private string _detail = string.Empty;

    /// <summary>Fotos der Gruppe mit (noch offenem) Vorschlag im aktuellen Filter.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    private int _openSuggestions;

    public bool CanApply => OpenSuggestions > 0;

    public List<SuggestionItemRow> Items { get; } = new();
}

/// <summary>Ein Foto der Liste.</summary>
public sealed partial class SuggestionItemRow : ObservableObject
{
    public SuggestionItemRow(GeoSuggestionItem item, SuggestionGroupRow group, int displayOffsetMinutes)
    {
        Item = item;
        Group = group;
        DisplayOffsetMinutes = displayOffsetMinutes;
        Latitude = item.Suggestion?.Latitude;
        Longitude = item.Suggestion?.Longitude;
    }

    public GeoSuggestionItem Item { get; }

    public SuggestionGroupRow Group { get; }

    public int DisplayOffsetMinutes { get; }

    public int FileId => Item.FileId;

    /// <summary>Vorschlag (bzw. von Hand verschobener Punkt).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSuggestion), nameof(Detail))]
    private double? _latitude;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSuggestion), nameof(Detail))]
    private double? _longitude;

    /// <summary>Auf der Karte von Hand verschoben (wird als „manuell“ gespeichert).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BadgeText), nameof(Detail))]
    private bool _isMoved;

    /// <summary>Übernommen (Rückgängig bis zum Neuladen in der Zeile).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BadgeText), nameof(BadgeKind), nameof(Detail), nameof(CanApply), nameof(IsOpen))]
    private bool _isApplied;

    [ObservableProperty] private bool _isSelected;

    [ObservableProperty] private ImageSource? _image;

    /// <summary>Protokoll-Batch des Übernehmens (für „Rückgängig“ in der Zeile).</summary>
    public int? BatchId { get; set; }

    public bool HasSuggestion => Latitude is not null && Longitude is not null;

    public bool CanApply => HasSuggestion && !IsApplied;

    public bool IsOpen => !IsApplied;

    public bool HasStored => Item.HasStored;

    public GeoSuggestionLevel Level => Item.Level;

    /// <summary>„14:56 · Gipfelkreuz.jpg“.</summary>
    public string Title => Item.LocalTime is { } t ? $"{t.ToString("HH:mm", CultureInfo.GetCultureInfo("de-DE"))} · {Item.Name}" : Item.Name;

    /// <summary>Stil der Plakette: garmin, sure, likely, none, done.</summary>
    public string BadgeKind => IsApplied ? "done" : Level switch
    {
        GeoSuggestionLevel.Track => "garmin",
        GeoSuggestionLevel.Sure => "sure",
        GeoSuggestionLevel.Likely => "likely",
        _ => "none",
    };

    public string BadgeText => IsApplied ? "übernommen" : IsMoved ? "verschoben" : GeoSuggestionText.LevelName(Level);

    public string Detail
    {
        get
        {
            if (IsApplied)
            {
                return "korrigiert";
            }

            if (IsMoved && Latitude is { } lat && Longitude is { } lon)
            {
                var distance = Item.StoredLatitude is { } sl && Item.StoredLongitude is { } so ? $" → {GeoMath.FormatDistance(GeoMath.DistanceMeters(sl, so, lat, lon))} korrigieren" : string.Empty;
                return "von Hand auf der Karte gesetzt" + distance;
            }

            return GeoSuggestionText.Detail(Item, DisplayOffsetMinutes);
        }
    }

    /// <summary>Quelle in Worten (Tooltip, Lightbox).</summary>
    public string SourceText => GeoSuggestionText.Source(Item, DisplayOffsetMinutes);

    public string AutomationName => $"{Title}, {BadgeText}";
}

/// <summary>Texte der Vorschlagsliste.</summary>
public static class PlacesSuggestionText
{
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    public static string N(int value) => value.ToString("N0", De);

    /// <summary>„Sonntag, 15. März 2020“.</summary>
    public static string Day(DateTime day) => day.ToString("dddd, d. MMMM yyyy", De);

    /// <summary>„Weinebene · 5 Fotos, 4 Vorschläge“.</summary>
    public static string GroupDetail(string? place, int count, int suggestions)
    {
        var text = $"{N(count)} {(count == 1 ? "Foto" : "Fotos")}, {N(suggestions)} {(suggestions == 1 ? "Vorschlag" : "Vorschläge")}";
        return string.IsNullOrWhiteSpace(place) ? text : $"{place} · {text}";
    }

    /// <summary>Zusammenfassung oben in der Seitenleiste.</summary>
    public static string Summary(PlacesSideMode mode, int total, int track, int sure, int likely, int none)
    {
        var who = mode == PlacesSideMode.Inaccurate
            ? $"{N(total)} {(total == 1 ? "Foto" : "Fotos")} mit geschätzter Position (ohne GPS-Höhe/-Zeit, über Mobilfunk/WLAN oder unmöglicher Sprung)."
            : $"{N(total)} {(total == 1 ? "Foto" : "Fotos")} ohne Ort mit Aufnahmezeit.";
        return $"{who} Vorschläge zuerst aus Ihren GPS-Spuren, sonst aus zeitnahen Fotos mit echtem GPS: {N(track)} Garmin-Spur · {N(sure)} sicher · {N(likely)} wahrscheinlich · {N(none)} ohne Vorschlag.";
    }

    public static bool Matches(SuggestionFilter filter, GeoSuggestionLevel level) => filter switch
    {
        SuggestionFilter.Track => level == GeoSuggestionLevel.Track,
        SuggestionFilter.Sure => level == GeoSuggestionLevel.Sure,
        SuggestionFilter.Likely => level == GeoSuggestionLevel.Likely,
        SuggestionFilter.None => level == GeoSuggestionLevel.None,
        _ => true,
    };

    /// <summary>Gruppen nach Tag der Aufnahme (Wanduhrzeit), neueste zuerst; ohne Datum am Ende.</summary>
    public static IReadOnlyList<(string Key, string Title, List<GeoSuggestionItem> Items)> GroupByDay(IEnumerable<GeoSuggestionItem> items)
        => items
            .GroupBy(i => i.LocalTime?.Date)
            .OrderByDescending(g => g.Key ?? DateTime.MinValue)
            .Select(g => (g.Key?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "-", g.Key is { } d ? Day(d) : "Ohne Aufnahmedatum", g.OrderBy(i => i.LocalTime).ThenBy(i => i.FileId).ToList()))
            .ToList();
}
