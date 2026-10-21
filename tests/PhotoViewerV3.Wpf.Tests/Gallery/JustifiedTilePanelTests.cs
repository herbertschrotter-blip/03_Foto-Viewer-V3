using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PhotoViewerV3.Gallery.Controls;
using PhotoViewerV3.Gallery.ListView;
using PhotoViewerV3.Gallery.ViewModels;
using PhotoViewerV3.Wpf.Tests.Fixtures;
using Xunit.Abstractions;

namespace PhotoViewerV3.Wpf.Tests.Gallery;

/// <summary>
/// Collage-Panel (ADR-0044) mit der echten Collage-Vorlage: nur sichtbare Reihen + Puffer realisiert, randvolle Reihen
/// im echten Fenster, Container-Zuordnung per Treffertest wie <see cref="TileContainerMappingTests"/> (Recycling,
/// Scrollen, Fokus, Pfeiltasten über Reihen), Ansicht Kacheln ↔ Collage wechseln ohne Fehler.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class JustifiedTilePanelTests
{
    private const int ItemCount = 300;
    private const int FocusIndex = 72;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly ITestOutputHelper _output;

    public JustifiedTilePanelTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Tausend_Items_realisieren_nur_sichtbare_Reihen_plus_Puffer()
    {
        Run(VirtualizationMode.Recycling, 1000, host =>
        {
            var panel = host.Panel;
            var layout = panel.Layout;
            layout.ItemCount.Should().Be(1000);
            var visibleRows = layout.RowAt(panel.ViewportHeight - 1) + 1;
            var expected = layout.RowLast(Math.Min(layout.RowCount - 1, visibleRows)) + 1; // sichtbare + 1 Pufferreihe
            panel.RealizedCount.Should().Be(expected);
            panel.RealizedCount.Should().BeLessThan(80);
            panel.ExtentHeight.Should().Be(layout.ExtentHeight);
            _output.WriteLine($"1000 Items: {layout.RowCount} Reihen, {panel.RealizedCount} Container, Umbruch {panel.LastLayoutMilliseconds:N3} ms");

            panel.SetVerticalOffset(panel.ExtentHeight);
            host.Window.UpdateLayout();
            host.ContainerOf(999).Should().NotBeNull("Scrollen ans Ende realisiert die letzten Items");
            panel.RealizedCount.Should().BeLessThan(80);
        });
    }

    [Fact]
    public void Reihen_im_Fenster_randvoll_letzte_Reihe_nicht_gestreckt()
    {
        Run(VirtualizationMode.Recycling, ItemCount, host =>
        {
            var panel = host.Panel;
            panel.SetVerticalOffset(panel.ExtentHeight);
            host.Window.UpdateLayout();
            AssertRowsFull(host, "unten");

            panel.SetVerticalOffset(0);
            host.Window.UpdateLayout();
            AssertRowsFull(host, "oben");
        });
    }

    [Theory]
    [InlineData(VirtualizationMode.Recycling)]
    [InlineData(VirtualizationMode.Standard)]
    public void Fokus_72_dann_scrollen_und_springen_jeder_Platz_traegt_sein_Item(VirtualizationMode mode)
    {
        Run(mode, ItemCount, host =>
        {
            var panel = host.Panel;
            FocusItem(host, FocusIndex);
            AssertSlots(host, "nach Fokus auf 72");

            var step = 0;
            while (panel.VerticalOffset > 0)
            {
                panel.MouseWheelUp();
                if (++step % 3 == 0)
                {
                    panel.MouseWheelDown();
                    host.Window.UpdateLayout();
                    panel.MouseWheelUp();
                }

                host.Window.UpdateLayout();
                AssertSlots(host, $"Mausrad-Schritt {step}");
            }

            host.ContainerOf(FocusIndex).Should().NotBeNull("der fokussierte Container bleibt außerhalb des Viewports erhalten");

            foreach (var offset in new[] { panel.ExtentHeight, 0, panel.ExtentHeight / 2, 0, 500, 0 })
            {
                panel.SetVerticalOffset(offset);
                host.Window.UpdateLayout();
                AssertSlots(host, $"Sprung auf {offset:N0}");
            }

            for (var i = 0; i < 25; i++)
            {
                panel.MouseWheelDown();
                host.Window.UpdateLayout();
                AssertSlots(host, $"runter {i}");
            }
        });
    }

    [Theory]
    [InlineData(VirtualizationMode.Recycling)]
    [InlineData(VirtualizationMode.Standard)]
    public void Pfeiltasten_wandern_ueber_Reihen_und_jeder_Platz_traegt_sein_Item(VirtualizationMode mode)
    {
        Run(mode, ItemCount, host =>
        {
            var layout = host.Panel.Layout;
            FocusItem(host, layout.RowFirst(1) + 1);

            for (var i = 0; i < 8; i++)
            {
                var before = FocusedIndex(host);
                SendKey(host, Key.Down);
                host.Window.UpdateLayout();
                var after = FocusedIndex(host);
                layout.RowOf(after).Should().Be(layout.RowOf(before) + 1, $"Pfeil ab von Item {before} (Reihe {layout.RowOf(before)}) → nächste Reihe, war {after}");
                IsFullyVisible(host, after).Should().BeTrue($"Item {after} wurde in den Viewport gescrollt");
                AssertSlots(host, $"Pfeil ab {i}");
            }

            for (var i = 0; i < 4; i++)
            {
                var before = FocusedIndex(host);
                SendKey(host, Key.Up);
                host.Window.UpdateLayout();
                layout.RowOf(FocusedIndex(host)).Should().Be(layout.RowOf(before) - 1, "Pfeil auf → vorige Reihe");
            }

            var start = FocusedIndex(host);
            SendKey(host, Key.Right);
            host.Window.UpdateLayout();
            FocusedIndex(host).Should().Be(start + 1, "Pfeil rechts → nächstes Item");
            AssertSlots(host, "nach Pfeiltasten");
        });
    }

    [Fact]
    public void Reload_mit_wiederverwendeten_Items_und_neuem_Seitenverhaeltnis_bleibt_konsistent()
    {
        Run(VirtualizationMode.Recycling, ItemCount, host =>
        {
            var panel = host.Panel;
            FocusItem(host, FocusIndex);
            panel.SetVerticalOffset(0);
            host.Window.UpdateLayout();

            host.Items[3] = CollageGridHost.CreateItem(3, (1000, 3000));
            host.Items.Move(FocusIndex, 5);
            host.Items.Move(10, 90);
            host.Items.RemoveAt(40);
            host.Items.Insert(0, CollageGridHost.CreateItem(900, (5000, 1000)));
            host.Window.UpdateLayout();
            AssertSlots(host, "nach Reload");
            AssertRowsFull(host, "nach Reload");

            // Maße nachgetragen (Seitenverhältnis ändert sich) → ViewModel erhöht die Layout-Version
            var changed = host.Items[2];
            changed.UpdateMediaSize(new PhotoViewerV3.Gallery.Data.GalleryFileDto(changed.Id, 1, changed.Name, ".jpg", 1, 1, 0, false, null, false, @"C:\") { Width = 900, Height = 3000 });
            host.Model.CollageLayoutVersion++;
            host.Window.UpdateLayout();
            panel.Layout.AspectOf(2).Should().BeApproximately(0.4, 1e-9, "0,3 wird auf 0,4 begrenzt");
            AssertSlots(host, "nach neuem Seitenverhältnis");
            AssertRowsFull(host, "nach neuem Seitenverhältnis");
        });
    }

    [Fact]
    public void Reihenhoehe_folgt_dem_Regler_und_die_oberste_Kachel_bleibt_im_Blick()
    {
        Run(VirtualizationMode.Recycling, ItemCount, host =>
        {
            var panel = host.Panel;
            panel.SetVerticalOffset(panel.Layout.RowTop(12) + 10);
            host.Window.UpdateLayout();
            foreach (var size in new[] { 320, 120, 240 })
            {
                var first = panel.Layout.RowFirst(panel.Layout.RowAt(panel.VerticalOffset)); // erstes sichtbares Item
                host.Model.TileSize = size;
                host.Window.UpdateLayout();
                panel.Layout.TargetRowHeight.Should().Be(size);
                var row = panel.Layout.RowOf(first);
                panel.VerticalOffset.Should().BeInRange(
                    panel.Layout.RowTop(row) - 1,
                    panel.Layout.RowTop(row) + panel.Layout.RowHeight(row),
                    $"Regler {size}: Item {first} bleibt oben");
                AssertRowsFull(host, $"Regler {size}");
                AssertSlots(host, $"Regler {size}");
            }
        });
    }

    [Fact]
    public void Ansicht_wechseln_Kacheln_Collage_Kacheln_ohne_Fehler()
    {
        Run(VirtualizationMode.Recycling, ItemCount, host =>
        {
            for (var round = 0; round < 3; round++)
            {
                host.Model.ViewMode = GalleryViewMode.Grid;
                host.Window.UpdateLayout();
                var grid = GalleryGridHost.FindVisualChild<VirtualizingTilePanel>(host.ListBox);
                grid.Should().NotBeNull($"Runde {round}: Kachel-Panel aktiv");
                GalleryGridHost.FindVisualChild<JustifiedTilePanel>(host.ListBox).Should().BeNull();
                grid!.SetVerticalOffset(400 * round);
                host.Window.UpdateLayout();
                var gridIndex = grid.FirstRealizedIndex + grid.Columns;
                host.ContainerOf(gridIndex)!.DataContext.Should().BeSameAs(host.Items[gridIndex]);
                host.ContainerOf(gridIndex)!.ContentTemplate.Should().NotBeSameAs(host.CollageTemplate);

                host.Model.ViewMode = GalleryViewMode.Collage;
                host.Window.UpdateLayout();
                var collage = host.Panel;
                collage.RealizedCount.Should().BeGreaterThan(0, $"Runde {round}: Collage realisiert Container");
                host.ContainerOf(collage.FirstRealizedIndex)!.ContentTemplate.Should().BeSameAs(host.CollageTemplate);
                FocusItem(host, 30 + round);
                collage.MouseWheelDown();
                host.Window.UpdateLayout();
                AssertSlots(host, $"Runde {round} Collage");
                AssertRowsFull(host, $"Runde {round} Collage");
            }
        });
    }

    // ---- Hilfen --------------------------------------------------------------------------------

    private static void Run(VirtualizationMode mode, int count, Action<CollageGridHost> test)
    {
        TestUiThread.Run(() =>
        {
            using var host = new CollageGridHost(count, width: 900, height: 600);
            VirtualizingPanel.SetVirtualizationMode(host.ListBox, mode);
            host.Window.ShowActivated = true;
            host.ShowAndLayout();
            host.Window.Activate();
            GalleryGridHost.Pump(DispatcherPriority.Background);
            test(host);
            return true;
        }, Timeout);
    }

    private static void FocusItem(CollageGridHost host, int index)
    {
        host.ListBox.ScrollIntoView(host.Items[index]);
        host.Window.UpdateLayout();
        var container = host.ContainerOf(index)!;
        container.IsSelected = true;
        container.Focus().Should().BeTrue("der Container muss den Tastaturfokus bekommen");
        GalleryGridHost.Pump(DispatcherPriority.Background);
        container.IsKeyboardFocusWithin.Should().BeTrue();
    }

    private static int FocusedIndex(CollageGridHost host)
        => Keyboard.FocusedElement is ListBoxItem item ? host.ListBox.ItemContainerGenerator.IndexFromContainer(item) : -1;

    private static bool IsFullyVisible(CollageGridHost host, int index)
    {
        var panel = host.Panel;
        var cell = panel.Layout.ItemRect(index);
        return cell.Top >= panel.VerticalOffset - 0.5 && cell.Bottom <= panel.VerticalOffset + panel.ViewportHeight + 0.5;
    }

    private static void SendKey(CollageGridHost host, Key key)
    {
        var target = Keyboard.FocusedElement as IInputElement ?? host.ListBox;
        var source = PresentationSource.FromVisual(host.Window)!;
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.KeyDownEvent });
    }

    /// <summary>Jede realisierte gefüllte Reihe beginnt im Fenster bei 0 und endet ±1 px am rechten Rand des Panels.</summary>
    private static void AssertRowsFull(CollageGridHost host, string stage)
    {
        var panel = host.Panel;
        var layout = panel.Layout;
        var checkedRows = 0;
        for (var row = 0; row < layout.RowCount; row++)
        {
            var first = host.ContainerOf(layout.RowFirst(row));
            var last = host.ContainerOf(layout.RowLast(row));
            if (first is null || last is null)
            {
                continue;
            }

            var left = first.TranslatePoint(new Point(0, 0), panel).X;
            var right = last.TranslatePoint(new Point(last.ActualWidth, 0), panel).X;
            left.Should().BeApproximately(0, 1, $"[{stage}] Reihe {row} links");
            if (layout.IsFilledRow(row))
            {
                right.Should().BeApproximately(panel.ActualWidth, 1, $"[{stage}] Reihe {row} randvoll");
                checkedRows++;
            }
            else
            {
                layout.RowHeight(row).Should().Be(layout.TargetRowHeight + CollageGridHost.Spacing, $"[{stage}] letzte Reihe bei Zielhöhe");
            }
        }

        checkedRows.Should().BeGreaterThan(0, stage);
    }

    /// <summary>Treffertest in der Mitte jedes (sichtbaren Teils eines) Platzes: getroffener Container trägt dessen Item.</summary>
    private void AssertSlots(CollageGridHost host, string stage)
    {
        var panel = host.Panel;
        var layout = panel.Layout;
        var offset = panel.VerticalOffset;
        var errors = new List<string>();
        var checkedSlots = 0;
        var firstRow = layout.RowAt(offset);
        var lastRow = layout.RowAt(offset + panel.ViewportHeight - 1);
        for (var row = firstRow; row <= lastRow; row++)
        {
            for (var index = layout.RowFirst(row); index <= layout.RowLast(row); index++)
            {
                var cell = layout.ItemRect(index);
                var top = Math.Max(cell.Top - offset, 0);
                var bottom = Math.Min(cell.Bottom - offset, panel.ViewportHeight);
                if (bottom - top < 20)
                {
                    continue;
                }

                var hit = panel.InputHitTest(new Point(cell.Left + (cell.Width / 2), (top + bottom) / 2)) as DependencyObject;
                var container = FindAncestor<ListBoxItem>(hit);
                checkedSlots++;
                var expected = host.Items[index];
                if (container is null)
                {
                    errors.Add($"Platz {index}: kein Container getroffen");
                    continue;
                }

                if (!ReferenceEquals(container.DataContext, expected) || !ReferenceEquals(container.Content, expected))
                {
                    errors.Add($"Platz {index}: DataContext = {Describe(host, container.DataContext)}, Content = {Describe(host, container.Content)}");
                }

                var presenter = GalleryGridHost.FindVisualChild<ContentPresenter>(container);
                var shown = presenter is not null && VisualTreeHelper.GetChildrenCount(presenter) > 0
                    ? (VisualTreeHelper.GetChild(presenter, 0) as FrameworkElement)?.DataContext
                    : null;
                if (!ReferenceEquals(shown, expected))
                {
                    errors.Add($"Platz {index}: angezeigt = {Describe(host, shown)}");
                }

                var names = TextsOf(container).Where(t => t.StartsWith("IMG_", StringComparison.Ordinal)).ToList();
                if (!names.SequenceEqual(new[] { expected.Name }))
                {
                    errors.Add($"Platz {index}: Name = {string.Join("|", names)} statt {expected.Name}");
                }

                if (Math.Abs(container.ActualWidth - cell.Width) > 0.5 || Math.Abs(container.ActualHeight - cell.Height) > 0.5)
                {
                    errors.Add($"Platz {index}: Container {container.ActualWidth:N1} × {container.ActualHeight:N1} statt Zelle {cell.Width:N1} × {cell.Height:N1}");
                }
            }
        }

        if (errors.Count > 0)
        {
            _output.WriteLine($"[{stage}] {errors.Count} Fehler; Panel-Kinder: {DumpChildren(host)}");
            foreach (var error in errors.Take(10))
            {
                _output.WriteLine("  " + error);
            }
        }

        checkedSlots.Should().BeGreaterThan(0, stage);
        errors.Should().BeEmpty($"[{stage}] jeder sichtbare Platz trägt sein Item");
    }

    private static string Describe(CollageGridHost host, object? value)
        => value is GalleryItem item ? $"Item {host.Items.IndexOf(item)} (Id {item.Id})" : value?.ToString() ?? "null";

    private static string DumpChildren(CollageGridHost host)
    {
        var panel = host.Panel;
        var generator = host.ListBox.ItemContainerGenerator;
        var sb = new StringBuilder();
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(panel); i++)
        {
            var child = VisualTreeHelper.GetChild(panel, i);
            sb.Append($"[{i}:{generator.IndexFromContainer(child)}] ");
        }

        return sb.ToString();
    }

    private static IEnumerable<string> TextsOf(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock { Text: { Length: > 0 } text })
            {
                yield return text;
            }

            foreach (var nested in TextsOf(child))
            {
                yield return nested;
            }
        }
    }

    private static T? FindAncestor<T>(DependencyObject? current)
        where T : DependencyObject
    {
        while (current is not null and not T)
        {
            current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
        }

        return current as T;
    }
}
