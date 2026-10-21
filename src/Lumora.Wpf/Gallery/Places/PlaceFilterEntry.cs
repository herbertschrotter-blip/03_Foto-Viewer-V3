using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Lumora.Services.Places;

namespace Lumora.Gallery.Places;

/// <summary>Art eines Eintrags im Flyout „Ort“.</summary>
public enum PlaceFilterEntryKind
{
    Country,
    Region,
    Place,

    /// <summary>„ohne Ort“ (keine Koordinaten).</summary>
    WithoutLocation,

    /// <summary>„ohne Ortsnamen“ (Koordinaten, aber kein Ort in der Nähe).</summary>
    WithoutPlaceName,
}

/// <summary>
/// Eintrag im Flyout „Ort“ (ADR-0050): Land → Region → Ort als flache, eingerückte Liste mit Anzahl; Länder und Regionen
/// lassen sich auf- und zuklappen. Ein gewähltes Land bzw. eine gewählte Region steht für alle ihre Orte im
/// Bibliotheksbereich (<see cref="PlaceIds"/>).
/// </summary>
public sealed partial class PlaceFilterEntry : ObservableObject
{
    public PlaceFilterEntry(PlaceFilterEntryKind kind, string key, string name, int count, IReadOnlyList<int> placeIds, PlaceFilterEntry? parent = null)
    {
        Kind = kind;
        Key = key;
        Name = name;
        Count = count;
        PlaceIds = placeIds;
        Parent = parent;
        Level = parent is null ? 0 : parent.Level + 1;
    }

    public PlaceFilterEntryKind Kind { get; }

    /// <summary>Eindeutiger Schlüssel („C:AT“, „R:AT|Wien“, „P:2761369“, „none“, „noname“) — Auswahl bleibt beim Neuladen.</summary>
    public string Key { get; }

    public string Name { get; }

    public int Count { get; }

    public string CountText => Count.ToString("N0");

    public IReadOnlyList<int> PlaceIds { get; }

    public PlaceFilterEntry? Parent { get; }

    public int Level { get; }

    public Thickness Indent => new(Level * 18, 0, 0, 0);

    public List<PlaceFilterEntry> Children { get; } = new();

    public bool HasChildren => Children.Count > 0;

    /// <summary>Suchtext (Name, bei Orten auch Region und Land).</summary>
    public string SearchText => string.Join(" ", new[] { Name, Parent?.Name, Parent?.Parent?.Name }.Where(s => s is not null));

    [ObservableProperty] private bool _isSelected;

    [ObservableProperty] private bool _isExpanded;

    [ObservableProperty] private bool _isVisible = true;

    /// <summary>„▸“/„▾“ für Länder und Regionen.</summary>
    public string ExpanderGlyph => !HasChildren ? string.Empty : IsExpanded ? "▾" : "▸";

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ExpanderGlyph));

    /// <summary>Für Tests und Anzeige.</summary>
    public override string ToString() => $"{new string(' ', Level * 2)}{Name} ({Count})";

    /// <summary>
    /// Baut die Liste aus den Ortszählern des Bereichs: Länder (nach Anzahl), darin Regionen (nach Anzahl), darin Orte
    /// (nach Anzahl), dazu „ohne Ort“ und „ohne Ortsnamen“. Unbekannte PlaceIds (ältere Daten) fallen weg.
    /// </summary>
    public static List<PlaceFilterEntry> Build(PlaceCountSummary summary, IPlaceLookup places)
    {
        var rows = summary.Places
            .Select(p => (Info: places.GetPlace(p.PlaceId), p.Count))
            .Where(p => p.Info is not null)
            .Select(p => (Info: p.Info!, p.Count))
            .ToList();
        var result = new List<PlaceFilterEntry>();
        foreach (var country in rows.GroupBy(r => r.Info.CountryCode).OrderByDescending(g => g.Sum(r => r.Count)).ThenBy(g => g.First().Info.Country, StringComparer.CurrentCulture))
        {
            var countryEntry = new PlaceFilterEntry(PlaceFilterEntryKind.Country, "C:" + country.Key, country.First().Info.Country, country.Sum(r => r.Count), country.Select(r => r.Info.PlaceId).ToList());
            result.Add(countryEntry);
            foreach (var region in country.GroupBy(r => r.Info.Region ?? "—").OrderByDescending(g => g.Sum(r => r.Count)).ThenBy(g => g.Key, StringComparer.CurrentCulture))
            {
                var regionEntry = new PlaceFilterEntry(PlaceFilterEntryKind.Region, $"R:{country.Key}|{region.Key}", region.Key, region.Sum(r => r.Count), region.Select(r => r.Info.PlaceId).ToList(), countryEntry);
                countryEntry.Children.Add(regionEntry);
                result.Add(regionEntry);
                foreach (var place in region.OrderByDescending(r => r.Count).ThenBy(r => r.Info.Name, StringComparer.CurrentCulture))
                {
                    var placeEntry = new PlaceFilterEntry(PlaceFilterEntryKind.Place, "P:" + place.Info.PlaceId, place.Info.Name, place.Count, [place.Info.PlaceId], regionEntry);
                    regionEntry.Children.Add(placeEntry);
                    result.Add(placeEntry);
                }
            }
        }

        if (summary.WithoutPlaceName > 0)
        {
            result.Add(new PlaceFilterEntry(PlaceFilterEntryKind.WithoutPlaceName, "noname", "ohne Ortsnamen (Koordinaten abseits von Orten)", summary.WithoutPlaceName, []));
        }

        result.Add(new PlaceFilterEntry(PlaceFilterEntryKind.WithoutLocation, "none", "ohne Ort", summary.WithoutLocation, []));

        // Wenige Länder: aufgeklappt; Länder mit nur einer Region zeigen die Orte direkt.
        var countries = result.Where(e => e.Kind == PlaceFilterEntryKind.Country).ToList();
        foreach (var c in countries)
        {
            c.IsExpanded = countries.Count <= 3;
            if (c.Children.Count == 1)
            {
                c.Children[0].IsExpanded = true;
            }
        }

        return result;
    }

    /// <summary>Sichtbarkeit aller Einträge nach Aufklappen bzw. Suchtext.</summary>
    public static void UpdateVisibility(IReadOnlyList<PlaceFilterEntry> entries, string? search)
    {
        var needle = search?.Trim();
        if (string.IsNullOrEmpty(needle))
        {
            foreach (var e in entries)
            {
                e.IsVisible = e.Parent is null || (e.Parent.IsExpanded && e.Parent.IsVisible);
            }

            return;
        }

        foreach (var e in entries)
        {
            e.IsVisible = Matches(e, needle) || e.Children.Any(c => Matches(c, needle) || c.Children.Any(g => Matches(g, needle)));
        }

        static bool Matches(PlaceFilterEntry e, string needle) => e.Name.Contains(needle, StringComparison.CurrentCultureIgnoreCase);
    }
}
