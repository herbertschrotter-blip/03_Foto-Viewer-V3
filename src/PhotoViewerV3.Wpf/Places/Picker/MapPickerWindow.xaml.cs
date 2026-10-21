using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PhotoViewerV3.Places.Dialogs;
using PhotoViewerV3.Places.Map;
using PhotoViewerV3.Places.Map.Tiles;
using PhotoViewerV3.Places.ViewModels;
using PhotoViewerV3.Services.Places;
using PhotoViewerV3.Services.Places.Clustering;

namespace PhotoViewerV3.Places.Picker;

/// <summary>
/// Kartenfenster „Ort wählen“ (Implementierung von <see cref="IMapPicker"/>, ADR-0051): Klick setzt die Nadel, die
/// Fußzeile zeigt den nächsten Ort (offline) und die Koordinaten; Ortssuche springt hin. Enter = Übernehmen (mit Nadel),
/// Esc = Abbrechen.
/// </summary>
public sealed partial class MapPickerWindow : PhotoViewerV3.Shell.Dialogs.AppFluentWindow
{
    private readonly IReverseGeocoder _geocoder;

    public MapPickerWindow(MapTileProvider? tiles, IReverseGeocoder geocoder, string? title)
    {
        _geocoder = geocoder;
        InitializeComponent();
        if (!string.IsNullOrWhiteSpace(title))
        {
            Title = title;
            DialogTitleBar.Title = title;
        }
        else
        {
            Title = "Ort wählen";
        }

        Map.Viewport = Viewport;
        Map.Tiles = tiles;
        AttributionText.Text = tiles?.Attribution ?? OsmTileSource.OsmAttribution;
        Map.MapClicked += (_, e) => SetPoint(e.Latitude, e.Longitude);
    }

    public MapViewport Viewport { get; } = new();

    /// <summary>Gewählter Punkt oder null.</summary>
    public (double Latitude, double Longitude)? Point { get; private set; }

    /// <summary>Ergebnis nach dem Schließen (null = abgebrochen).</summary>
    public (double Latitude, double Longitude)? Result { get; private set; }

    /// <summary>Nadel setzen (Klick, Suche, Tests).</summary>
    public void SetPoint(double latitude, double longitude)
    {
        Point = (latitude, longitude);
        Map.Pin = new System.Windows.Point(MercatorProjection.LongitudeToX(longitude), MercatorProjection.LatitudeToY(latitude));
        var place = SafeNearest(latitude, longitude);
        PlaceText.Text = place is null ? "Kein Ort in der Nähe bekannt" : PlacesText.Describe(place);
        CoordinatesText.Text = AssignLocationDialog.FormatCoordinates(latitude, longitude);
        OkButton.IsEnabled = true;
    }

    /// <summary>Übernehmen (Knopf, Enter, Tests) — nur mit gesetzter Nadel.</summary>
    public void Accept()
    {
        if (Point is null)
        {
            return;
        }

        Result = Point;
        Close();
    }

    private PlaceInfo? SafeNearest(double latitude, double longitude)
    {
        try
        {
            return _geocoder.Nearest(latitude, longitude);
        }
        catch (System.Exception)
        {
            return null;
        }
    }

    private void OnOkClick(object sender, RoutedEventArgs e) => Accept();

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
        else if (e.Key == Key.Enter && !SearchBox.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            Accept();
        }
    }

    private sealed record SearchItem(PlaceInfo Place, double Latitude, double Longitude)
    {
        public string Display => PlacesText.Describe(Place);
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        var text = SearchBox.Text?.Trim() ?? string.Empty;
        var items = text.Length < 2 ? [] : SafeSearch(text);
        SearchList.ItemsSource = items;
        SearchPopup.IsOpen = items.Count > 0;
    }

    private System.Collections.Generic.List<SearchItem> SafeSearch(string text)
    {
        try
        {
            return _geocoder.Search(text, 8).Select(r => new SearchItem(r.Place, r.Latitude, r.Longitude)).ToList();
        }
        catch (System.Exception)
        {
            return [];
        }
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SearchList.Items.Count > 0 && SearchList.Items[0] is SearchItem first)
        {
            GoTo(first);
            e.Handled = true;
        }
    }

    private void OnSearchResultClick(object sender, MouseButtonEventArgs e)
    {
        if (SearchList.SelectedItem is SearchItem item)
        {
            GoTo(item);
        }
    }

    private void GoTo(SearchItem item)
    {
        SearchPopup.IsOpen = false;
        Viewport.CenterOn(item.Latitude, item.Longitude, 13);
        SetPoint(item.Latitude, item.Longitude);
    }
}
