using CommunityToolkit.Mvvm.ComponentModel;
using PhotoViewerV3.Services.Places;

namespace PhotoViewerV3.Gallery.ViewModels;

/// <summary>Koordinate und Ort einer Kachel (ADR-0050).</summary>
public sealed record GalleryItemPlace(double Latitude, double Longitude, PlaceInfo? Place)
{
    /// <summary>Spalte „Ort“: „Wien, Österreich“ bzw. die Koordinate ohne Ortsnamen.</summary>
    public string Text => PlaceText.Short(Place) ?? PhotoViewerV3.Services.Metadata.GeoCoordinate.Format(Latitude, Longitude);

    /// <summary>Tooltip: „Wien, Österreich (48,2082° N, 16,3738° O)“.</summary>
    public string Detail => PlaceText.Describe(Place, Latitude, Longitude);
}

/// <summary>
/// Ort der Kachel (ADR-0050): Listenspalte „Ort“ und Tooltip. Wird nach dem Laden der Seite gebündelt nachgeladen
/// (<c>GalleryViewModel.Places.cs</c>) und nach <c>GeoLocationsChangedMessage</c> ersetzt.
/// </summary>
public sealed partial class GalleryItem
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlaceText), nameof(PlaceToolTip))]
    private GalleryItemPlace? _place;

    /// <summary>Reihenfolge der Ladeaufträge: ein älteres Ergebnis überschreibt kein neueres.</summary>
    internal long PlaceSequence { get; set; }

    /// <summary>Spalte „Ort“ (leer ohne Koordinaten).</summary>
    public string PlaceText => Place?.Text ?? string.Empty;

    /// <summary>Tooltip der Spalte „Ort“.</summary>
    public string? PlaceToolTip => Place?.Detail;
}
