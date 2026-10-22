using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Navigation;
using Lumora.Gallery.Controls;
using Lumora.Gallery.ViewModels;
using Lumora.Places.Map;
using Lumora.Places.ViewModels;
using Wpf.Ui.Controls;
using MenuItem = System.Windows.Controls.MenuItem;

namespace Lumora.Places;

/// <summary>
/// Seite „Orte“ (ADR-0051). Code-Behind nur als UI-Klebstoff: Karten-Ereignisse (Klick, Doppelklick, Rechtsklick-Menü,
/// Ablegen) ans ViewModel, Auswahl und Ziehen in der Seitenleiste „Ohne Ort“, Ortssuche per Tastatur.
/// </summary>
public partial class PlacesPage : UserControl
{
    private readonly PlacesViewModel _viewModel;
    private Point? _dragStart;
    private UnlocatedTile? _dragTile;
    private UnlocatedTile? _pendingSelect;

    /// <param name="gallery">Galerie-ViewModel (Singleton): Suchfeld und Filterkarte binden daran — ein Filterzustand für
    /// Galerie, Ordner und Orte (ADR-0055). Ohne (Tests) bleiben beide ausgeblendet.</param>
    public PlacesPage(PlacesViewModel viewModel, GalleryViewModel? gallery = null)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        FilterCard.DataContext = gallery;
        GallerySearch.DataContext = gallery;
        FilterCard.Visibility = gallery is null ? Visibility.Collapsed : Visibility.Visible;
        GallerySearch.Visibility = FilterCard.Visibility;
        Loaded += (_, _) =>
        {
            if (gallery is not null)
            {
                _ = gallery.EnsureInitializedAsync(); // Filterlisten (Tags, Personen …) und gemeinsamer Filter bereit
            }

            _ = viewModel.ActivateAsync();
        };
        Unloaded += (_, _) => viewModel.Deactivate();
        Map.MarkerClicked += (_, e) => _ = _viewModel.ActivateMarkerAsync(e.Marker);
        Map.MarkerDoubleClicked += (_, e) => _viewModel.ShowInGallery(e.Marker);
        Map.ContextRequested += OnMapContextRequested;
        // Nicht im OLE-Drop-Rückruf einen modalen Dialog öffnen (DoDragDrop liefe sonst bis zum Schließen weiter)
        Map.FilesDropped += (_, e) => Dispatcher.BeginInvoke(() => _ = _viewModel.AssignAsync(e.FileIds, e.Latitude, e.Longitude));
        // Ungenaue Orte (ADR-0062): Vorschlag ziehen bzw. Punkt anklicken
        Map.SuggestionMoved += (_, e) => _viewModel.MoveSuggestion(e.Id, e.Latitude, e.Longitude);
        Map.OverlayClicked += (_, e) => _viewModel.SelectRowByFileId(e.Id);
    }

    public PlacesViewModel ViewModel => _viewModel;

    /// <summary>Die Karte (Rauchtest, Tests).</summary>
    public MapControl MapView => Map;

    // ---- Karte ---------------------------------------------------------------------------------------------------------

    private void OnMapContextRequested(object? sender, MapPointEventArgs e)
    {
        var menu = BuildContextMenu(e);
        menu.PlacementTarget = Map;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    /// <summary>Kontextmenü: auf einem Marker öffnen/hineinzoomen und „In der Galerie zeigen“, immer „Hier verorten“.</summary>
    internal ContextMenu BuildContextMenu(MapPointEventArgs e)
    {
        var menu = new ContextMenu();
        if (e.Marker is { } marker)
        {
            var open = new MenuItem
            {
                Header = marker.IsGroup ? "Hineinzoomen" : "Foto öffnen",
                Icon = new SymbolIcon { Symbol = marker.IsGroup ? SymbolRegular.ZoomIn24 : SymbolRegular.Open24 },
            };
            open.Click += (_, _) => _ = _viewModel.ActivateMarkerAsync(marker);
            menu.Items.Add(open);
            var gallery = new MenuItem
            {
                Header = marker.IsGroup ? $"In der Galerie zeigen ({MapMarker.FormatCount(marker.Count)} Fotos)" : "In der Galerie zeigen",
                Icon = new SymbolIcon { Symbol = SymbolRegular.ImageMultiple24 },
            };
            gallery.Click += (_, _) => _viewModel.ShowInGallery(marker);
            menu.Items.Add(gallery);
            menu.Items.Add(new Separator());
        }

        var count = _viewModel.SelectedCount;
        var assign = new MenuItem
        {
            Header = count switch
            {
                0 => "Hier verorten (erst Fotos unter „Ohne Ort“ auswählen)",
                1 => "Hier verorten (1 Foto)",
                _ => $"Hier verorten ({PlacesText.N(count)} Fotos)",
            },
            IsEnabled = count > 0,
            Icon = new SymbolIcon { Symbol = SymbolRegular.Location24 },
        };
        System.Windows.Automation.AutomationProperties.SetName(assign, "Hier verorten");
        var (lat, lon) = (e.Latitude, e.Longitude);
        assign.Click += (_, _) => _ = _viewModel.AssignSelectedAtAsync(lat, lon);
        menu.Items.Add(assign);
        return menu;
    }

    // ---- Kartenstil (ADR-0055) -------------------------------------------------------------------------------------------

    private void OnMapStyleClick(object sender, RoutedEventArgs e)
    {
        var menu = BuildMapStyleMenu();
        menu.PlacementTarget = MapStyleButton;
        menu.Placement = PlacementMode.Left;
        menu.IsOpen = true;
    }

    /// <summary>Stil-Menü: alle Stile, der gewählte mit Häkchen, fehlende Schlüssel im Text („— Schlüssel nötig“).</summary>
    internal ContextMenu BuildMapStyleMenu()
    {
        var menu = new ContextMenu();
        foreach (var entry in _viewModel.MapStyleEntries())
        {
            var item = new MenuItem
            {
                Header = entry.Header,
                ToolTip = entry.NeedsKey ? $"{entry.Option.Description}. {Lumora.Places.Map.Tiles.MapStyleCatalog.KeyMissingNotice}." : entry.Option.Description,
                IsCheckable = true,
                IsChecked = entry.IsCurrent,
            };
            System.Windows.Automation.AutomationProperties.SetName(item, entry.Option.Label);
            var style = entry.Option.Style;
            item.Click += (_, _) => _ = _viewModel.SetMapStyleAsync(style);
            menu.Items.Add(item);
        }

        return menu;
    }

    private void OnAttributionNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // kein Browser — die Zuordnung bleibt trotzdem sichtbar
        }

        e.Handled = true;
    }

    // ---- Seitenleiste „Ohne Ort“ ---------------------------------------------------------------------------------------

    private void OnUnlocatedVisibleRangeChanged(object sender, VisibleRangeChangedEventArgs e)
        => _viewModel.ReportUnlocatedVisibleRange(e.FirstIndex, e.LastIndex);

    private void OnTileMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem { DataContext: UnlocatedTile tile } item)
        {
            return;
        }

        item.Focus();
        if (e.ClickCount == 2)
        {
            _ = _viewModel.OpenUnlocatedAsync(tile);
            e.Handled = true;
            return;
        }

        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        _dragStart = e.GetPosition(this);
        _dragTile = tile;
        _pendingSelect = null;
        if (tile.IsSelected && !ctrl && !shift)
        {
            _pendingSelect = tile; // ausgewählte Kachel: erst beim Loslassen allein auswählen (sonst ginge die Auswahl beim Ziehen verloren)
            item.PreviewMouseLeftButtonUp += OnTileMouseUp;
        }
        else
        {
            _viewModel.SelectTile(tile, ctrl, shift);
        }

        e.Handled = true;
    }

    private void OnTileMouseUp(object sender, MouseButtonEventArgs e)
    {
        ((ListBoxItem)sender).PreviewMouseLeftButtonUp -= OnTileMouseUp;
        if (_pendingSelect is { } tile)
        {
            _pendingSelect = null;
            _viewModel.SelectTile(tile, toggle: false, range: false);
        }

        _dragStart = null;
    }

    private void OnTileMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start || _dragTile is not { } tile || e.LeftButton != MouseButtonState.Pressed
            || sender is not ListBoxItem item)
        {
            return;
        }

        var pos = e.GetPosition(this);
        if (Math.Abs(pos.X - start.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(pos.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _dragStart = null;
        _pendingSelect = null;
        item.PreviewMouseLeftButtonUp -= OnTileMouseUp;
        var data = new DataObject(MapControl.FileIdsDataFormat, _viewModel.DragFileIds(tile));
        DragDrop.DoDragDrop(item, data, DragDropEffects.Copy);
    }

    private void OnTileKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not ListBoxItem { DataContext: UnlocatedTile tile })
        {
            return;
        }

        if (e.Key == Key.Space)
        {
            _viewModel.SelectTile(tile, toggle: true, range: false);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            _ = _viewModel.OpenUnlocatedAsync(tile);
            e.Handled = true;
        }
    }

    // ---- Ortssuche -----------------------------------------------------------------------------------------------------

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _viewModel.GoToSearchResultCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Down && _viewModel.IsSearchOpen)
        {
            SearchList.SelectedIndex = 0;
            (SearchList.ItemContainerGenerator.ContainerFromIndex(0) as UIElement)?.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _viewModel.IsSearchOpen = false;
        }
    }

    private void OnSearchResultClick(object sender, MouseButtonEventArgs e)
    {
        if (SearchList.SelectedItem is PlaceSearchResult result)
        {
            _viewModel.GoToSearchResultCommand.Execute(result);
        }
    }

    private void OnSearchListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SearchList.SelectedItem is PlaceSearchResult result)
        {
            _viewModel.GoToSearchResultCommand.Execute(result);
            e.Handled = true;
        }
    }
}
