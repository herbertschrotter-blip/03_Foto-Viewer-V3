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
using PhotoViewerV3.Services.Categories;

namespace PhotoViewerV3.Gallery.ListView;

/// <summary>
/// Listenansicht der Galerie. Code-Behind: Spalten aus <see cref="GalleryListColumns"/> + Einstellungen aufbauen,
/// Kopfzeilen-Kontextmenü (Spalten ein-/ausblenden), Reihenfolge nach dem Ziehen speichern, Sortierung auf
/// <see cref="GalleryViewModel.SelectedSort"/> abbilden, Enter/Doppelklick → Lightbox. Fachlogik bleibt im ViewModel.
/// </summary>
public partial class GalleryListView : UserControl
{
    private readonly Dictionary<GalleryListColumnKey, DataGridColumn> _columns = new();
    private readonly Dictionary<int, DataGridColumn> _categoryColumns = new(); // ADR-0027: je Kategorie eine Spalte
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

    /// <summary>Alle sichtbaren Spalten als Einstellungs-Token (feste Spalten und „Kategorie:{Id}“) nach DisplayIndex.</summary>
    public IReadOnlyList<string> VisibleColumnTokensInOrder()
        => _columns.Select(p => (Token: p.Key.ToString(), Column: p.Value))
            .Concat(_categoryColumns.Select(p => (Token: GalleryListColumnSettings.CategoryToken(p.Key), Column: p.Value)))
            .Where(p => p.Column.Visibility == Visibility.Visible)
            .OrderBy(p => p.Column.DisplayIndex)
            .Select(p => p.Token)
            .ToList();

    /// <summary>Überschriften der sichtbaren Kategorie-Spalten in Anzeigereihenfolge (Rauchtest, Tests).</summary>
    public IReadOnlyList<string> VisibleCategoryHeaders()
        => _categoryColumns.Values
            .Where(c => c.Visibility == Visibility.Visible)
            .OrderBy(c => c.DisplayIndex)
            .Select(c => c.Header?.ToString() ?? string.Empty)
            .ToList();

    /// <summary>Sortierpfeil der Kategorie-Spalte (Rauchtest, Tests); null ohne Spalte oder ohne Sortierung.</summary>
    public ListSortDirection? CategorySortDirection(int categoryId)
        => _categoryColumns.TryGetValue(categoryId, out var column) ? column.SortDirection : null;

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
        RebuildCategoryColumns();
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
            case nameof(GalleryViewModel.CategoryColumns):
                RebuildCategoryColumns();
                ApplySettings(GalleryListColumnSettings.Parse(_viewModel?.ListColumnSettings));
                UpdateSortIndicators();
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

    /// <summary>
    /// Kategorie-Spalten (ADR-0027) nach den Kategorien des ViewModels neu aufbauen: Wertname per Indexer
    /// <c>Categories[Id]</c>; ein Klick auf die Kopfzeile sortiert die ganze Trefferliste in der DB (<see cref="GalleryViewModel.SortByCategory"/>).
    /// </summary>
    private void RebuildCategoryColumns()
    {
        _applyingSettings = true;
        try
        {
            foreach (var column in _categoryColumns.Values)
            {
                Grid.Columns.Remove(column);
            }

            _categoryColumns.Clear();
            var categories = _viewModel?.CategoryColumns ?? Array.Empty<CategoryInfo>();
            foreach (var category in categories)
            {
                var path = $"{nameof(GalleryItem.Categories)}[{category.Id}]";
                var column = new DataGridTextColumn
                {
                    Header = category.Name,
                    Binding = new Binding(path) { Mode = BindingMode.OneWay },
                    SortMemberPath = path,
                    ElementStyle = TrimmedTextStyle(),
                    Width = new DataGridLength(140),
                    MinWidth = 50,
                };
                _categoryColumns[category.Id] = column;
                Grid.Columns.Add(column);
            }
        }
        finally
        {
            _applyingSettings = false;
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
            case GalleryListColumnKey.Persons:
                // 0.2.38: bestätigte Namen, Vorschläge kursiv/grau mit „?“; Sortierung nur innerhalb der Page.
                return new DataGridTemplateColumn { CellTemplate = (DataTemplate)Resources["PersonsCell"], SortMemberPath = nameof(GalleryItem.PersonsSortKey) };
            case GalleryListColumnKey.Motifs:
                // ADR-0052: „Strand, Meer“ (wahrscheinlichste zuerst); Sortierung nur innerhalb der Page.
                return new DataGridTextColumn
                {
                    Binding = new Binding(nameof(GalleryItem.MotifsText)) { Mode = BindingMode.OneWay },
                    SortMemberPath = nameof(GalleryItem.MotifsText),
                    ElementStyle = TrimmedTextStyle(),
                };
            case GalleryListColumnKey.Captured:
                // Aufnahmedatum (ADR-0046): „12.07.2024 15:30“ / „ca. 1982“, Herkunft im Tooltip; Kopfklick sortiert die ganze Trefferliste.
                return new DataGridTextColumn
                {
                    Binding = new Binding(nameof(GalleryItem.CaptureDateText)) { Mode = BindingMode.OneWay },
                    SortMemberPath = nameof(GalleryItem.CaptureSortKey),
                    ElementStyle = CaptureCellStyle(),
                };
            case GalleryListColumnKey.Place:
                // Ort (ADR-0050): „Wien, Österreich“, Koordinate im Tooltip; Sortierung nur innerhalb der Page.
                var placeStyle = TrimmedTextStyle();
                placeStyle.Setters.Add(new Setter(ToolTipProperty, new Binding(nameof(GalleryItem.PlaceToolTip)) { Mode = BindingMode.OneWay }));
                return new DataGridTextColumn
                {
                    Binding = new Binding(nameof(GalleryItem.PlaceText)) { Mode = BindingMode.OneWay },
                    SortMemberPath = nameof(GalleryItem.PlaceText),
                    ElementStyle = placeStyle,
                };
            default:
                throw new ArgumentOutOfRangeException(nameof(def), def.Key, "Unbekannte Spalte");
        }
    }

    private static Style CaptureCellStyle()
    {
        var style = TrimmedTextStyle();
        style.Setters.Add(new Setter(ToolTipProperty, new Binding(nameof(GalleryItem.CaptureDateToolTip)) { Mode = BindingMode.OneWay }));
        return style;
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

        var categories = _viewModel?.CategoryColumns ?? Array.Empty<CategoryInfo>();
        foreach (var category in categories)
        {
            if (!_categoryColumns.TryGetValue(category.Id, out var column))
            {
                continue;
            }

            var categoryItem = new MenuItem
            {
                Header = $"{category.Name} (Kategorie)",
                IsCheckable = true,
                IsChecked = column.Visibility == Visibility.Visible,
                StaysOpenOnClick = true,
                Tag = category.Id,
            };
            categoryItem.Click += OnColumnToggleClick;
            menu.Items.Add(categoryItem);
        }

        menu.Items.Add(new Separator());
        var reset = new MenuItem { Header = "Standard wiederherstellen" };
        reset.Click += (_, _) => SaveSettings(GalleryListColumnSettings.Default);
        menu.Items.Add(reset);
    }

    private void OnColumnToggleClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item)
        {
            return;
        }

        var current = GalleryListColumnSettings.Parse(_viewModel?.ListColumnSettings).WithTokenOrder(VisibleColumnTokensInOrder());
        switch (item.Tag)
        {
            case GalleryListColumnKey key:
                SaveSettings(current.WithVisibility(key, item.IsChecked));
                break;
            case int categoryId:
                SaveSettings(current.WithCategoryVisibility(categoryId, item.IsChecked));
                break;
        }
    }

    private void OnColumnReordered(object? sender, DataGridColumnEventArgs e)
    {
        if (_applyingSettings)
        {
            return;
        }

        var current = GalleryListColumnSettings.Parse(_viewModel?.ListColumnSettings);
        SaveSettings(current.WithTokenOrder(VisibleColumnTokensInOrder()));
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
            var placed = new HashSet<DataGridColumn>();
            foreach (var token in settings.VisibleTokens)
            {
                var column = GalleryListColumnSettings.TryParseCategoryToken(token, out var categoryId)
                    ? _categoryColumns.GetValueOrDefault(categoryId)
                    : Enum.TryParse<GalleryListColumnKey>(token, out var key) ? _columns.GetValueOrDefault(key) : null;
                if (column is not null && placed.Add(column))
                {
                    column.Visibility = Visibility.Visible;
                    column.DisplayIndex = index++;
                }
            }

            // Kategorie-Spalten ohne Token (neue Kategorien) sichtbar ans Ende, ausgeblendete dahinter.
            foreach (var (categoryId, column) in _categoryColumns)
            {
                if (!placed.Contains(column) && settings.IsCategoryVisible(categoryId))
                {
                    column.Visibility = Visibility.Visible;
                    column.DisplayIndex = index++;
                    placed.Add(column);
                }
            }

            foreach (var def in GalleryListColumns.All)
            {
                var column = _columns[def.Key];
                if (!placed.Contains(column))
                {
                    column.Visibility = Visibility.Collapsed;
                    column.DisplayIndex = index++;
                }
            }

            foreach (var column in _categoryColumns.Values)
            {
                if (!placed.Contains(column))
                {
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
        if (!_columns.ContainsValue(e.Column))
        {
            // Kategorie-Spalte (ADR-0027, 0.2.23): ganze Trefferliste serverseitig nach dem Wert sortieren.
            var categoryId = _categoryColumns.FirstOrDefault(p => ReferenceEquals(p.Value, e.Column)).Key;
            if (_viewModel is not null && categoryId > 0)
            {
                e.Handled = true;
                _view?.SortDescriptions.Clear();
                _viewModel.SortByCategory(categoryId);
                UpdateSortIndicators();
            }

            return;
        }

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

        var categorySort = _viewModel.SelectedSort.Category;
        var active = categorySort is null ? GalleryListColumns.ForSort(_viewModel.SelectedSort.Sort) : null;
        foreach (var (key, column) in _columns)
        {
            column.SortDirection = active is { } a && a.Key == key
                ? (a.Descending ? ListSortDirection.Descending : ListSortDirection.Ascending)
                : null;
        }

        foreach (var (categoryId, column) in _categoryColumns)
        {
            column.SortDirection = categorySort is { } c && c.CategoryId == categoryId
                ? (c.Descending ? ListSortDirection.Descending : ListSortDirection.Ascending)
                : null;
        }

        // Eine DB-Sortierung ersetzt eine zuvor gewählte Page-Sortierung.
        if ((active is not null || categorySort is not null) && _view is { SortDescriptions.Count: > 0 })
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
