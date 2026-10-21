using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using PhotoViewerV3.Persons.Controls;
using PhotoViewerV3.Services.Places;

namespace PhotoViewerV3.Places.ViewModels;

/// <summary>Kachel der Seitenleiste „Ohne Ort“ (Vorschaubild, Name, Auswahl).</summary>
public sealed partial class UnlocatedTile : ObservableObject, IAutomationNamed
{
    public UnlocatedTile(int fileId)
    {
        FileId = fileId;
    }

    public int FileId { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    private string _name = string.Empty;

    [ObservableProperty] private ImageSource? _image;

    [ObservableProperty] private bool _isSelected;

    public string AutomationName => string.IsNullOrEmpty(Name) ? "Foto ohne Ort" : Name;
}

/// <summary>Treffer der Ortssuche (offline, GeoNames über <see cref="IReverseGeocoder.Search"/>).</summary>
public sealed record PlaceSearchResult(PlaceInfo Place, double Latitude, double Longitude)
{
    public string Title => Place.Name;

    public string Detail => PlacesText.RegionAndCountry(Place);
}

/// <summary>Texte der Seite „Orte“.</summary>
public static class PlacesText
{
    private static readonly System.Globalization.CultureInfo De = System.Globalization.CultureInfo.GetCultureInfo("de-DE");

    public static string N(int value) => value.ToString("N0", De);

    /// <summary>„1.234 verortete Fotos · 56 ohne Ort“.</summary>
    public static string Summary(int located, int unlocated)
        => $"{N(located)} verortete{(located == 1 ? "s Foto" : " Fotos")} · {N(unlocated)} ohne Ort";

    /// <summary>„Wien, Österreich“ bzw. „Graz, Steiermark, Österreich“ (Region ohne Wiederholung des Namens).</summary>
    public static string Describe(PlaceInfo place)
    {
        var rest = RegionAndCountry(place);
        return string.IsNullOrEmpty(rest) ? place.Name : $"{place.Name}, {rest}";
    }

    public static string RegionAndCountry(PlaceInfo place)
    {
        var parts = new System.Collections.Generic.List<string>();
        if (!string.IsNullOrWhiteSpace(place.Region) && !string.Equals(place.Region, place.Name, System.StringComparison.OrdinalIgnoreCase))
        {
            parts.Add(place.Region!);
        }

        if (!string.IsNullOrWhiteSpace(place.Country))
        {
            parts.Add(place.Country);
        }

        return string.Join(", ", parts);
    }
}
