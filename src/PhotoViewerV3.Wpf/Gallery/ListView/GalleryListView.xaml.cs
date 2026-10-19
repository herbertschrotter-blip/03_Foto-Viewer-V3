using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using PhotoViewerV3.Gallery.Data;
using PhotoViewerV3.Gallery.ViewModels;

namespace PhotoViewerV3.Gallery.ListView;

/// <summary>
/// Listenansicht der Galerie. Code-Behind: Spalten aus <see cref="GalleryListColumns"/> + Einstellungen aufbauen,
/// Kopfzeilen-Kontextmenü (Spalten ein-/ausblenden), Reihenfolge nach dem Ziehen speichern, Sortierung auf
/// <see cref="GalleryViewModel.SelectedSort"/> abbilden, Enter/Doppelklick → Lightbox. Fachlogik bleibt im ViewModel.
/// </summary>
public partial class GalleryListView : UserControl
{
    private readonly Dictionary<GalleryListColumnKey, DataGridColumn> _columns = new();
    private GalleryViewModel? _viewModel;
    private ListCollectionView? _view;
    private bool _applyingSettings;

    public GalleryListView()
    {
        InitializeComponent();
        BuildColumns();
        Grid.ColumnHeaderStyle = BuildHeaderStyle();
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>Das DataGrid (für Tests und das Kontextmenü der Galerie-Seite).</summary>
    public GalleryDataGrid DataGrid => Grid;

    /// <summary>Spaltenschlüssel in aktueller Anzeigereihenfolge (sichtbare zuerst nach DisplayIndex).</summary>
    public IReadOnlyList<GalleryListColumnKey> VisibleColumnKeysInOrder()
        => _columns
            .Where(p => p.Value.Visibility == Visibility.Visible)
            .OrderBy(p => p.Value.DisplayIndex)
            .Select(p => p.Key)
            .ToList();

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = e.NewValue as GalleryViewModel;
        if (_viewModel is null)
        {
            Grid.ItemsSource = null;
            _view = null;
            return;
        }

        // Eigene View über CurrentPage: Sortieren innerhalb der Page darf die (Standard-)View des Kachel-Grids nicht berühren.
        _view = new ListCollectionView(_viewModel.CurrentPage);
        Grid.ItemsSource = _view;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        ApplySettings(GalleryListColumnSettings.Parse(_viewModel.ListColumnSettings));
        UpdateSortIndicators();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(GalleryViewModel.SelectedSort):
                UpdateSortIndicators();
                break;
            case nameof(GalleryViewModel.ListColumnSettings) when !_applyingSettings:
                ApplySettings(GalleryListColumnSettings.Parse(_viewModel?.ListColumnSettings));
                break;
        }
    }

    // ---- Spalten ---------------------------------------------------------------------------------

    private void BuildColumns()
    {
        foreach (var def in GalleryListColumns.All)
        {
            var column = CreateColumn(def);
            column.Header = def.Header;
            column.Width = new DataGridLength(def.Width);
            column.MinWidth = 50;
            _columns[def.Key] = column;
            Grid.Columns.Add(column);
        }
    }

    private DataGridColumn CreateColumn(GalleryListColumnDefinition def)
    {
        switch (def.Key)
        {
            case GalleryListColumnKey.Name:
                return new DataGridTemplateColumn { CellTemplate = (DataTemplate)Resources["NameCell"], SortMemberPath = nameof(GalleryItem.Name) };
            case GalleryListColumnKey.Folder:
                return new DataGridTextColumn { Binding = new Binding(nameof(GalleryItem.FolderPath)), SortMemberPath = nameof(GalleryItem.FolderPath), ElementStyle = TrimmedTextStyle() };
            case GalleryListColumnKey.Modified:
                return new DataGridTextColumn
                {
                    Binding = new Binding(nameof(GalleryItem.SourceModifiedAtUtc)) { Converter = (IValueConverter)Resources["UtcToLocal"] },
                    SortMemberPath = nameof(GalleryItem.ModifiedAtUnix),
                    ElementStyle = TrimmedTextStyle(),
                };
            case GalleryListColumnKey.Size:
                return new DataGridTextColumn { Binding = new Binding(nameof(GalleryItem.SizeDisplay)), SortMemberPath = nameof(GalleryItem.Size), ElementStyle = TrimmedTextStyle() };
            case GalleryListColumnKey.Type:
                return new DataGridTextColumn
                {
                    Binding = new Binding(nameof(GalleryItem.IsVideo)) { Converter = (IValueConverter)Resources["MediaTypeText"] },
                    SortMemberPath = nameof(GalleryItem.IsVideo),
                    ElementStyle = TrimmedTextStyle(),
                };
            case GalleryListColumnKey.Rating:
                return new DataGridTemplateColumn { CellTemplate = (DataTemplate)Resources["RatingCell"], SortMemberPath = nameof(GalleryItem.Rating) };
            case GalleryListColumnKey.Favorite:
                return new DataGridTemplateColumn { CellTemplate = (DataTemplate)Resources["FavoriteCell"], SortMemberPath = nameof(GalleryItem.IsFavorite) };
            case GalleryListColumnKey.Tags:
                // Sortierung nur clientseitig innerhalb der Page (TagsText = Namen in Anzeigereihenfolge).
                return new DataGridTemplateColumn { CellTemplate = (DataTemplate)Resources["TagsCell"], SortMemberPath = nameof(GalleryItem.TagsText) };
            case GalleryListColumnKey.Status:
                return new DataGridTemplateColumn { CellTemplate = (DataTemplate)Resources["StatusCell"], SortMemberPath = nameof(GalleryItem.StatusText) };
            default:
                throw new ArgumentOutOfRangeException(nameof(def), def.Key, "Unbekannte Spalte");
        }
    }

    private static Style TrimmedTextStyle()
    {
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        style.Setters.Add(new Setter(VerticalAlignmentProperty, VerticalAlignment.Center));
        return style;
    }

    /// <summary>Kopfzeilen-Stil mit Kontextmenü zum Ein-/Ausblenden der Spalten.</summary>
    private Style BuildHeaderStyle()
    {
        var menu = new ContextMenu();
        menu.Opened += OnHeaderMenuOpened;
        var style = new Style(typeof(DataGridColumnHeader));
        style.Setters.Add(new Setter(ContextMenuProperty, menu));
        style.Setters.Add(new Setter(PaddingProperty, new Thickness(8, 6, 8, 6)));
        return style;
    }

    private void OnHeaderMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu)
        {
            return;
        }

        menu.Items.Clear();
        menu.Items.Add(new MenuItem { Header = "Spalten", IsEnabled = false, FontWeight = FontWeights.SemiBold });
        foreach (var def in GalleryListColumns.All)
        {
            var column = _columns[def.Key];
            var item = new MenuItem
            {
                Header = def.Header,
                IsCheckable = true,
                IsChecked = column.Visibility == Visibility.Visible,
                IsEnabled = def.Key != GalleryListColumnKey.Name,
                StaysOpenOnClick = true,
                Tag = def.Key,
            };
            item.Click += OnColumnToggleClick;
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());
        var reset = new MenuItem { Header = "Standard wiederherstellen" };
        reset.Click += (_, _) => SaveSettings(GalleryListColumnSettings.Default);
        menu.Items.Add(reset);
    }

    private void OnColumnToggleClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: GalleryListColumnKey key } item)
        {
            return;
        }

        var current = GalleryListColumnSettings.Parse(_viewModel?.ListColumnSettings).WithOrder(VisibleColumnKeysInOrder());
        SaveSettings(current.WithVisibility(key, item.IsChecked));
    }

    private void OnColumnReordered(object? sender, DataGridColumnEventArgs e)
    {
        if (_applyingSettings)
        {
            return;
        }

        var current = GalleryListColumnSettings.Parse(_viewModel?.ListColumnSettings);
        SaveSettings(current.WithOrder(VisibleColumnKeysInOrder()));
    }

    private void SaveSettings(GalleryListColumnSettings settings)
    {
        ApplySettings(settings);
        if (_viewModel is not null)
        {
            _applyingSettings = true;
            try
            {
                _viewModel.ListColumnSettings = settings.Serialize();
            }
            finally
            {
                _applyingSettings = false;
            }
        }
    }

    /// <summary>Sichtbarkeit und DisplayIndex aus den Einstellungen; ausgeblendete Spalten bekommen Indizes dahinter.</summary>
    internal void ApplySettings(GalleryListColumnSettings settings)
    {
        _applyingSettings = true;
        try
        {
            var index = 0;
            foreach (var key in settings.Visible)
            {
                if (_columns.TryGetValue(key, out var column))
                {
                    column.Visibility = Visibility.Visible;
                    column.DisplayIndex = index++;
                }
            }

            foreach (var def in GalleryListColumns.All)
            {
                if (!settings.IsVisible(def.Key))
                {
                    var column = _columns[def.Key];
                    column.Visibility = Visibility.Collapsed;
                    column.DisplayIndex = index++;
                }
            }
        }
        finally
        {
            _applyingSettings = false;
        }
    }

    // ---- Sortierung ------------------------------------------------------------------------------

    private void OnSorting(object sender, DataGridSortingEventArgs e)
    {
        var key = _columns.FirstOrDefault(p => ReferenceEquals(p.Value, e.Column)).Key;
        if (_viewModel is null || GalleryListColumns.NextSort(key, _viewModel.SelectedSort.Sort) is not { } next)
        {
            // Spalte ohne DB-Sortierung (Ordner, Typ, Bewertung, Favorit, Tags, Status): Standardverhalten des DataGrid,
            // d. h. Sortierung innerhalb der geladenen Page über die eigene ListCollectionView.
            return;
        }

        e.Handled = true;
        _view?.SortDescriptions.Clear();
        _viewModel.SelectedSort = _viewModel.SortOptions.First(o => o.Sort == next);
        UpdateSortIndicators();
    }

    /// <summary>Sortierpfeil der Kopfzeile an die DB-Sortierung angleichen (Combo in der Toolbar oder Kopfzeilenklick).</summary>
    private void UpdateSortIndicators()
    {
        if (_viewModel is null)
        {
            return;
        }

        var active = GalleryListColumns.ForSort(_viewModel.SelectedSort.Sort);
        foreach (var (key, column) in _columns)
        {
            column.SortDirection = active is { } a && a.Key == key
                ? (a.Descending ? ListSortDirection.Descending : ListSortDirection.Ascending)
                : null;
        }

        // Eine DB-Sortierung ersetzt eine zuvor gewählte Page-Sortierung.
        if (active is not null && _view is { SortDescriptions.Count: > 0 })
        {
            _view.SortDescriptions.Clear();
        }
    }

    // ---- Öffnen ----------------------------------------------------------------------------------

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || sender is not DataGridRow { DataContext: GalleryItem item })
        {
            return;
        }

        if (IsInsideButton(e.OriginalSource as DependencyObject, (DependencyObject)sender))
        {
            return;
        }

        e.Handled = true;
        _viewModel?.OpenInLightboxCommand.Execute(item);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Return || Keyboard.Modifiers != ModifierKeys.None || _viewModel is null)
        {
            return;
        }

        var item = Grid.CurrentItem as GalleryItem ?? _viewModel.CurrentPage.FirstOrDefault(i => i.IsSelected);
        if (item is null)
        {
            return;
        }

        e.Handled = true;
        _viewModel.OpenInLightboxCommand.Execute(item);
    }

    private static bool IsInsideButton(DependencyObject? source, DependencyObject container)
    {
        var current = source;
        while (current is not null && !ReferenceEquals(current, container))
        {
            if (current is ButtonBase)
            {
                return true;
            }

            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return false;
    }
}
