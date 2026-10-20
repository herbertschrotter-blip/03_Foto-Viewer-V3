using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PhotoViewerV3.Gallery.Controls;
using PhotoViewerV3.Gallery.Data;
using PhotoViewerV3.Gallery.ListView;
using PhotoViewerV3.Gallery.ViewModels;
using PhotoViewerV3.Wpf.Tests.Fixtures;
using Xunit.Abstractions;

namespace PhotoViewerV3.Wpf.Tests.Gallery;

/// <summary>
/// Galerie-Kopfzeile (0.2.13): Bei 1280 px Fensterbreite wurde das Suchfeld zusammengedrückt, darunter lief die
/// Zeile über. Die Tests rendern das echte <see cref="GalleryToolbar"/> in einem Fenster bei −5000/−5000 mit der
/// Inhaltsbreite der Galerie-Seite (Fensterbreite − 220 px Navigationsbereich − 2 × 16 px Rand) und prüfen: nichts
/// ragt über den Rand, die Suche hat ihre Mindestbreite und Platz für den Platzhalter, die Zeilenzahl passt.
/// Die PNGs landen unter <c>bin/…/toolbar-shots</c> (Belege in <c>docs/spikes/07-galerie-kopfzeile*</c>).
/// </summary>
public sealed class GalleryToolbarLayoutTests(ITestOutputHelper output)
{
    private const double NavPaneWidth = 220;
    private const double PageMargin = 16;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [Theory]
    [InlineData(1024, 2)] // Suche rutscht in eine eigene Zeile
    [InlineData(1280, 1)]
    [InlineData(1920, 1)]
    [InlineData(2560, 1)]
    public void Kopfzeile_bleibt_sauber(int windowWidth, int expectedFilterLines)
    {
        var result = Render(windowWidth, new ToolbarStub(), $"galerie-kopfzeile-{windowWidth}");

        result.Overflow.Should().BeEmpty("kein Element darf über den rechten Rand der Kopfzeile ragen");
        result.SearchAreaWidth.Should().BeGreaterThanOrEqualTo(result.SearchAreaMinWidth - 0.5);
        result.SearchBoxWidth.Should().BeGreaterThanOrEqualTo(result.PlaceholderNeeded, "der Platzhalter muss vollständig sichtbar sein");
        result.FilterLines.Should().Be(expectedFilterLines);
        result.ViewLines.Should().Be(1);
    }

    [Fact]
    public void Kopfzeile_mit_Ordner_Chip_und_aktivem_Status_bricht_um_statt_zu_quetschen()
    {
        var stub = new ToolbarStub
        {
            HasFolderFilter = true,
            FolderChipText = @"2026\2026-07 Urlaub Italien (mit Unterordnern)",
            Filter = GalleryFilter.NotArchived,
        };

        var result = Render(1280, stub, "galerie-kopfzeile-1280-ordner-status");

        result.Overflow.Should().BeEmpty();
        result.SearchAreaWidth.Should().BeGreaterThanOrEqualTo(result.SearchAreaMinWidth - 0.5);
        result.FilterLines.Should().Be(2);
    }

    [Fact]
    public void Kopfzeile_rechte_Haelfte_der_Ordner_Seite_bleibt_bedienbar()
    {
        // Die GalleryView steckt auch rechts in der Ordner-Seite: dort ist sie deutlich schmaler.
        var result = Render(1024, new ToolbarStub(), "galerie-kopfzeile-ordnerseite", contentWidthOverride: 560);

        result.Overflow.Should().BeEmpty();
        result.SearchAreaWidth.Should().BeGreaterThanOrEqualTo(result.SearchAreaMinWidth - 0.5);
        result.FilterLines.Should().BeGreaterThan(1);
    }

    // ----- ToolbarFlowPanel direkt -----

    [Fact]
    public void FlowPanel_breit_eine_Zeile_Fuellelement_bis_MaxWidth_rechtsbuendig()
    {
        TestUiThread.Run(
            () =>
            {
                var (panel, a, b, fill, c) = CreatePanel();
                Layout(panel, 1000);

                panel.LineCount.Should().Be(1);
                Slot(a).X.Should().Be(0);
                Slot(b).X.Should().Be(108);
                Slot(fill).Width.Should().Be(300);
                Slot(c).Right.Should().BeApproximately(1000, 0.5, "Füll-Element an MaxWidth schiebt den Rest nach rechts");
                Slot(fill).X.Should().BeApproximately(1000 - 50 - 8 - 300, 0.5);
                return 0;
            },
            Timeout);
    }

    [Fact]
    public void FlowPanel_schmal_Fuellelement_bricht_um_und_beginnt_linksbuendig()
    {
        TestUiThread.Run(
            () =>
            {
                var (panel, a, b, fill, c) = CreatePanel();
                Layout(panel, 400);

                panel.LineCount.Should().Be(2);
                Slot(a).Y.Should().Be(Slot(b).Y);
                Slot(fill).X.Should().Be(0, "das umgebrochene Füll-Element beginnt die Zeile");
                Slot(fill).Y.Should().BeGreaterThan(Slot(a).Y);
                Slot(fill).Width.Should().Be(300, "es wächst bis MaxWidth");
                Slot(c).X.Should().Be(308);
                return 0;
            },
            Timeout);
    }

    [Fact]
    public void FlowPanel_Abstandhalter_ohne_MaxWidth_schiebt_rechte_Gruppe_an_den_Rand_und_ignoriert_Collapsed()
    {
        TestUiThread.Run(
            () =>
            {
                var panel = new ToolbarFlowPanel { Spacing = 8 };
                var left = Box(120, 30);
                var hidden = Box(500, 30);
                hidden.Visibility = Visibility.Collapsed;
                var spacer = new Border { MinWidth = 0, Height = 1 };
                ToolbarFlowPanel.SetFill(spacer, true);
                var right = Box(200, 32);
                panel.Children.Add(left);
                panel.Children.Add(hidden);
                panel.Children.Add(spacer);
                panel.Children.Add(right);

                Layout(panel, 800);

                panel.LineCount.Should().Be(1);
                Slot(right).Right.Should().BeApproximately(800, 0.5);
                Slot(left).Y.Should().Be(1, "Elemente werden in der Zeile (Höhe 32) vertikal zentriert");

                Layout(panel, 300);
                panel.LineCount.Should().Be(2, "die rechte Gruppe passt nicht mehr und bricht um");
                Slot(right).X.Should().Be(0);
                return 0;
            },
            Timeout);
    }

    // ----- Hilfen -----

    private static (ToolbarFlowPanel Panel, Border A, Border B, Border Fill, Border C) CreatePanel()
    {
        var panel = new ToolbarFlowPanel { Spacing = 8, LineSpacing = 6 };
        var a = Box(100, 30);
        var b = Box(100, 30);
        var fill = new Border { MinWidth = 200, MaxWidth = 300, Height = 30 };
        ToolbarFlowPanel.SetFill(fill, true);
        var c = Box(50, 30);
        panel.Children.Add(a);
        panel.Children.Add(b);
        panel.Children.Add(fill);
        panel.Children.Add(c);
        return (panel, a, b, fill, c);
    }

    private static Border Box(double width, double height) => new() { Width = width, Height = height };

    private static void Layout(ToolbarFlowPanel panel, double width)
    {
        panel.Measure(new Size(width, double.PositiveInfinity));
        panel.Arrange(new Rect(0, 0, width, panel.DesiredSize.Height));
    }

    private static Rect Slot(FrameworkElement element) => LayoutInformation.GetLayoutSlot(element);

    private RenderResult Render(int windowWidth, ToolbarStub stub, string shotName, double? contentWidthOverride = null)
    {
        var result = TestUiThread.Run(
            () =>
            {
                var contentWidth = contentWidthOverride ?? windowWidth - NavPaneWidth - (2 * PageMargin);
                var toolbar = new GalleryToolbar { DataContext = stub };
                var page = new Border { Width = contentWidth, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(PageMargin, 8, PageMargin, 12), Child = toolbar };

                var pane = new Border { Width = NavPaneWidth, Background = Brush("ControlFillColorDefaultBrush") };
                pane.Child = new TextBlock
                {
                    Text = "Navigation (220 px)",
                    Margin = new Thickness(16, 12, 0, 0),
                    Foreground = Brush("TextFillColorTertiaryBrush"),
                };

                var root = new DockPanel { Background = Brush("ApplicationBackgroundBrush"), LastChildFill = false };
                if (contentWidthOverride is null)
                {
                    DockPanel.SetDock(pane, Dock.Left);
                    root.Children.Add(pane);
                }

                DockPanel.SetDock(page, Dock.Left);
                root.Children.Add(page);

                var window = new Window
                {
                    WindowStyle = WindowStyle.None,
                    ResizeMode = ResizeMode.NoResize,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -5000,
                    Top = -5000,
                    SizeToContent = SizeToContent.WidthAndHeight,
                    Content = root,
                };
                window.Show();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                try
                {
                    var measured = Measure(toolbar);
                    SaveShot(root, toolbar.ActualHeight + page.Margin.Top + page.Margin.Bottom, shotName);
                    return measured;
                }
                finally
                {
                    window.Close();
                }
            },
            Timeout);

        output.WriteLine(
            $"{shotName}: Inhalt {result.ToolbarWidth:N0} px, Filterzeilen {result.FilterLines}, Suche {result.SearchAreaWidth:N0} px " +
            $"(min {result.SearchAreaMinWidth:N0}), Eingabefeld {result.SearchBoxWidth:N0} px (Platzhalter braucht {result.PlaceholderNeeded:N0})");
        return result;
    }

    private static RenderResult Measure(GalleryToolbar toolbar)
    {
        var filterRow = (ToolbarFlowPanel)toolbar.FindName("FilterRow");
        var viewRow = (ToolbarFlowPanel)toolbar.FindName("ViewRow");
        var searchArea = (FrameworkElement)toolbar.FindName("SearchArea");
        var searchBox = (global::Wpf.Ui.Controls.TextBox)toolbar.FindName("SearchBox");

        var overflow = new List<string>();
        foreach (var panel in new[] { filterRow, viewRow })
        {
            foreach (FrameworkElement child in panel.Children)
            {
                if (child.Visibility != Visibility.Visible)
                {
                    continue;
                }

                var bounds = child.TransformToAncestor(toolbar).TransformBounds(new Rect(0, 0, child.ActualWidth, child.ActualHeight));
                if (bounds.Right > toolbar.ActualWidth + 0.5)
                {
                    overflow.Add($"{child.GetType().Name} {child.Name}: rechts {bounds.Right:N1} > {toolbar.ActualWidth:N1}");
                }
            }
        }

        // Platzhalter + Suchsymbol (links, ca. 34 px) + Innenabstände (ca. 22 px)
        var placeholder = new FormattedText(
            searchBox.PlaceholderText,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(searchBox.FontFamily, searchBox.FontStyle, searchBox.FontWeight, searchBox.FontStretch),
            searchBox.FontSize,
            Brushes.White,
            1.0);

        return new RenderResult(
            toolbar.ActualWidth,
            filterRow.LineCount,
            viewRow.LineCount,
            searchArea.ActualWidth,
            searchArea.MinWidth,
            searchBox.ActualWidth,
            placeholder.WidthIncludingTrailingWhitespace + 56,
            overflow);
    }

    private static void SaveShot(FrameworkElement root, double visibleHeight, string name)
    {
        var width = (int)Math.Ceiling(root.ActualWidth);
        var height = (int)Math.Ceiling(Math.Min(root.ActualHeight, visibleHeight)); // nur die Kopfzeile, nicht die Fenster-Mindesthöhe
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var dir = Path.Combine(AppContext.BaseDirectory, "toolbar-shots");
        Directory.CreateDirectory(dir);
        using var stream = File.Create(Path.Combine(dir, name + ".png"));
        encoder.Save(stream);
    }

    private static Brush Brush(string key)
        => Application.Current.TryFindResource(key) as Brush ?? Brushes.Transparent;

    private sealed record RenderResult(
        double ToolbarWidth,
        int FilterLines,
        int ViewLines,
        double SearchAreaWidth,
        double SearchAreaMinWidth,
        double SearchBoxWidth,
        double PlaceholderNeeded,
        List<string> Overflow);

    /// <summary>Datenquelle mit den Eigenschaften, an die die Kopfzeile bindet (wie GalleryViewModel).</summary>
    private sealed class ToolbarStub
    {
        private static readonly ICommand Noop = new NoopCommand();

        public GalleryFilter Filter { get; set; } = GalleryFilter.All;

        public IReadOnlyList<GallerySortOption> SortOptions { get; } =
        [
            new(GallerySort.ModifiedDesc, "Datum neu → alt"),
            new(GallerySort.NameAsc, "Name A → Z"),
        ];

        public GallerySortOption? SelectedSort { get; set; }

        public IReadOnlyList<int> PageSizeOptions { get; } = [200, 500, 1000];

        public int PageSize { get; set; } = 500;

        public string? SearchTerm { get; set; }

        public GalleryViewMode ViewMode { get; set; } = GalleryViewMode.Grid;

        public bool IsGridView => ViewMode == GalleryViewMode.Grid;

        public int TileSize { get; set; } = GalleryViewModel.DefaultTileSize;

        public int MinTileSizeValue => GalleryViewModel.MinTileSize;

        public int MaxTileSizeValue => GalleryViewModel.MaxTileSize;

        public int TileSizeStepValue => GalleryViewModel.TileSizeStep;

        public bool HasFolderFilter { get; set; }

        public string FolderChipText { get; set; } = string.Empty;

        public string TagFilterText { get; set; } = "Tags";

        public IReadOnlyList<object> TagFilterEntries { get; } = [];

        public GalleryTagMode TagFilterMode { get; set; } = GalleryTagMode.Any;

        public bool HasNoTags => true;

        public bool HasLibrary => true;

        public ICommand SearchCommand => Noop;

        public ICommand ClearSearchCommand => Noop;

        public ICommand ClearFolderFilterCommand => Noop;

        public ICommand UndoCommand => Noop;

        public ICommand ReloadCommand => Noop;

        public ICommand ApplyTagFilterCommand => Noop;

        public ICommand ClearTagFilterCommand => Noop;

        public ToolbarStub()
        {
            SelectedSort = SortOptions[0];
        }

        private sealed class NoopCommand : ICommand
        {
            public event EventHandler? CanExecuteChanged
            {
                add { }
                remove { }
            }

            public bool CanExecute(object? parameter) => true;

            public void Execute(object? parameter)
            {
            }
        }
    }
}
