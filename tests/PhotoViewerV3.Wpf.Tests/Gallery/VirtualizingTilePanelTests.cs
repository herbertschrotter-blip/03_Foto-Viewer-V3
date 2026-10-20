using System.Diagnostics;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Threading;
using PhotoViewerV3.Gallery.Controls;
using PhotoViewerV3.Gallery.ViewModels;
using PhotoViewerV3.Wpf.Tests.Fixtures;
using Xunit.Abstractions;

namespace PhotoViewerV3.Wpf.Tests.Gallery;

/// <summary>
/// UI-Virtualisierung des Kachel-Grids (0.2.3): Mit 1000 Items existieren nur Container für die sichtbaren Zeilen plus
/// Puffer; Scrollen, ScrollIntoView, Reset und Spaltenumbruch arbeiten über den ItemContainerGenerator; die Auswahl
/// bleibt zwischen Items (ViewModel) und ListBox konsistent, auch für Items ohne Container; der UI-Automation-Baum
/// bleibt klein (Blatt-Peers + Virtualisierung). Alle Tests öffnen ein echtes Fenster auf dem Test-UI-Thread.
/// </summary>
public sealed class VirtualizingTilePanelTests
{
    private const int ItemCount = 1000;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly ITestOutputHelper _output;

    public VirtualizingTilePanelTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Tausend_Items_im_Fenster_900x600_realisieren_hoechstens_60_Container()
    {
        Run(host =>
        {
            var panel = host.Panel;
            var realized = host.RealizedIndexes();
            var visibleRows = (int)Math.Ceiling(panel.ViewportHeight / GalleryGridHost.TileHeight);

            panel.ScrollOwner.Should().NotBeNull("ScrollViewer.CanContentScroll=True muss das Panel als IScrollInfo anbinden");
            panel.ViewportHeight.Should().BeGreaterThan(0).And.BeLessThan(600);
            panel.ExtentHeight.Should().Be(Math.Ceiling(ItemCount / (double)panel.Columns) * GalleryGridHost.TileHeight);
            panel.Columns.Should().BeInRange(4, 5);
            realized.Count.Should().BeLessThanOrEqualTo(60);
            realized.Count.Should().BeGreaterThanOrEqualTo(visibleRows * panel.Columns, "alle sichtbaren Zeilen sind realisiert");
            realized.Count.Should().BeLessThanOrEqualTo((visibleRows + 2 * VirtualizingTilePanel.BufferRows) * panel.Columns);
            realized.Should().StartWith(0).And.BeInAscendingOrder();
            panel.RealizedCount.Should().Be(realized.Count, "Kinder des Panels = realisierte Container");
            _output.WriteLine($"Viewport {panel.ViewportWidth:N0}×{panel.ViewportHeight:N0}, {panel.Columns} Spalten, {realized.Count} Container realisiert (Items 0–{realized[^1]})");
        });
    }

    [Fact]
    public void Scrollen_ans_Ende_realisiert_die_letzten_Items_und_bleibt_klein()
    {
        Run(host =>
        {
            var panel = host.Panel;
            var before = host.RealizedIndexes();

            panel.SetVerticalOffset(panel.ExtentHeight);
            host.Window.UpdateLayout();

            var after = host.RealizedIndexes();
            panel.VerticalOffset.Should().Be(panel.ExtentHeight - panel.ViewportHeight);
            after.Should().Contain(ItemCount - 1).And.NotContain(0);
            after.Should().NotIntersectWith(before);
            after.Count.Should().BeLessThanOrEqualTo(60);
            panel.RealizedCount.Should().Be(after.Count);
            host.TopOf(host.ContainerOf(ItemCount - 1)!).Should().BeLessThan(host.ListBox.ActualHeight);
        });
    }

    [Fact]
    public void ScrollIntoView_realisiert_Item_777_sichtbar_im_Viewport()
    {
        Run(host =>
        {
            host.ContainerOf(777).Should().BeNull("Item 777 liegt beim Start weit außerhalb des Viewports");

            host.ListBox.ScrollIntoView(host.Items[777]);
            host.Window.UpdateLayout();
            GalleryGridHost.Pump(DispatcherPriority.Background);

            var container = host.ContainerOf(777);
            container.Should().NotBeNull();
            var top = host.TopOf(container!);
            top.Should().BeGreaterThanOrEqualTo(0);
            (top + GalleryGridHost.TileHeight).Should().BeLessThanOrEqualTo(host.ListBox.ActualHeight);
            host.RealizedIndexes().Count.Should().BeLessThanOrEqualTo(60);
        });
    }

    [Fact]
    public void Reset_der_Items_entfernt_alle_Container()
    {
        Run(host =>
        {
            host.RealizedIndexes().Should().NotBeEmpty();
            var panel = host.Panel;

            host.Items.Clear();
            host.Window.UpdateLayout();

            panel.RealizedCount.Should().Be(0);
            panel.ExtentHeight.Should().Be(0);
            panel.VerticalOffset.Should().Be(0);

            // Neue Seite: wieder Container, Offset bleibt geklemmt.
            foreach (var item in Enumerable.Range(0, 10).Select(GalleryGridHost.CreateItem))
            {
                host.Items.Add(item);
            }

            host.Window.UpdateLayout();
            host.RealizedIndexes().Should().Equal(Enumerable.Range(0, 10));
        });
    }

    [Fact]
    public void Breiterer_Viewport_erhoeht_die_Spaltenzahl()
    {
        Run(host =>
        {
            var panel = host.Panel;
            var columnsBefore = panel.Columns;
            var extentBefore = panel.ExtentHeight;

            host.Window.Width = 1500;
            host.Window.UpdateLayout();

            panel.Columns.Should().BeGreaterThan(columnsBefore);
            panel.Columns.Should().Be((int)Math.Floor(panel.ViewportWidth / GalleryGridHost.TileWidth));
            panel.ExtentHeight.Should().BeLessThan(extentBefore);
            host.RealizedIndexes().Should().StartWith(0);
            host.RealizedIndexes().Count.Should().BeLessThanOrEqualTo(80);
            var lastInFirstRow = host.ContainerOf(panel.Columns - 1)!;
            host.TopOf(lastInFirstRow).Should().Be(host.TopOf(host.ContainerOf(0)!), "letzte Kachel der ersten Zeile liegt in derselben Zeile");
            host.TopOf(host.ContainerOf(panel.Columns)!).Should().BeGreaterThan(host.TopOf(host.ContainerOf(0)!));
        });
    }

    [Fact]
    public void Mausrad_Zeile_und_Seite_scrollen_pixelweise()
    {
        Run(host =>
        {
            var panel = host.Panel;
            var wheel = Math.Max(1, SystemParameters.WheelScrollLines) * 16.0;

            panel.MouseWheelDown();
            panel.VerticalOffset.Should().Be(wheel);
            panel.LineDown();
            panel.VerticalOffset.Should().Be(wheel + 16);
            panel.MouseWheelUp();
            panel.LineUp();
            panel.VerticalOffset.Should().Be(0);
            panel.MouseWheelUp();
            panel.VerticalOffset.Should().Be(0, "nicht über den Anfang hinaus");

            panel.PageDown();
            panel.VerticalOffset.Should().Be(panel.ViewportHeight);
            panel.PageUp();
            panel.VerticalOffset.Should().Be(0);

            panel.SetVerticalOffset(double.MaxValue);
            panel.VerticalOffset.Should().Be(panel.ExtentHeight - panel.ViewportHeight, "nicht über das Ende hinaus");
            panel.SetHorizontalOffset(100);
            panel.HorizontalOffset.Should().Be(0, "kein horizontales Scrollen");
        });
    }

    [Fact]
    public void VisibleRangeChanged_meldet_den_sichtbaren_Bereich()
    {
        Run(host =>
        {
            var reports = new List<(int First, int Last)>();
            VirtualizingTilePanel.AddVisibleRangeChangedHandler(host.ListBox, (_, e) => reports.Add((e.FirstIndex, e.LastIndex)));
            var panel = host.Panel;

            host.Window.Width = 901; // Layout anstoßen → Meldung des aktuellen Bereichs
            host.Window.UpdateLayout();
            panel.SetVerticalOffset(panel.ExtentHeight);
            host.Window.UpdateLayout();

            reports.Should().NotBeEmpty();
            reports[^1].Last.Should().Be(ItemCount - 1);
            reports[^1].First.Should().BeGreaterThan(ItemCount - 60);
            reports.Should().OnlyContain(r => r.First >= 0 && r.Last >= r.First);
        });
    }

    [Fact]
    public void Einfuegen_Entfernen_Verschieben_Ersetzen_halten_Container_und_Items_konsistent()
    {
        Run(host =>
        {
            host.Items.Insert(0, GalleryGridHost.CreateItem(5000));
            host.Items.RemoveAt(3);
            host.Items.Move(1, 6);
            host.Items[2] = GalleryGridHost.CreateItem(6000);
            host.Window.UpdateLayout();

            AssertContainersMatchItems(host);

            host.Panel.SetVerticalOffset(host.Panel.ExtentHeight / 2);
            host.Window.UpdateLayout();
            var middle = host.RealizedIndexes()[host.RealizedIndexes().Count / 2];
            host.Items.RemoveAt(middle);
            host.Items.Insert(middle, GalleryGridHost.CreateItem(7000));
            host.Items.Move(middle, middle + 2);
            host.Window.UpdateLayout();

            AssertContainersMatchItems(host);
        });
    }

    [Fact]
    public void Item_Auswahl_vor_der_Realisierung_erscheint_am_Container_und_in_der_ListBox()
    {
        Run(host =>
        {
            host.ContainerOf(900).Should().BeNull();
            host.Items[900].IsSelected = true;
            host.Items[1].IsSelected = true;
            GalleryGridHost.Pump(DispatcherPriority.Background);

            host.ListBox.SelectedItems.Cast<object>().Should().Contain(host.Items[900], "die ListBox gleicht die Item-Auswahl auch ohne Container ab");
            host.ListBox.SelectedItems.Cast<object>().Should().Contain(host.Items[1]);
            host.ContainerOf(1)!.IsSelected.Should().BeTrue();

            host.ListBox.ScrollIntoView(host.Items[900]);
            host.Window.UpdateLayout();
            GalleryGridHost.Pump(DispatcherPriority.Background);
            host.ContainerOf(900)!.IsSelected.Should().BeTrue();
            host.ListBox.SelectedItems.Count.Should().Be(2);
        });
    }

    [Fact]
    public void ListBox_Auswahl_erreicht_Items_ohne_Container_und_aufgehobene_Auswahl_lebt_nicht_wieder_auf()
    {
        Run(host =>
        {
            host.ListBox.SelectedItems.Add(host.Items[900]);
            host.Items[900].IsSelected.Should().BeTrue("Umschalt-Bereich/Strg+A der ListBox muss das Item erreichen");

            // ViewModel hebt die Auswahl auf (z. B. Esc oder Seitenwechsel), solange kein Container existiert …
            host.Items[900].IsSelected = false;
            host.ListBox.ScrollIntoView(host.Items[900]);
            host.Window.UpdateLayout();
            GalleryGridHost.Pump(DispatcherPriority.Background);

            // … beim Realisieren darf die ListBox sie nicht wiederbeleben.
            host.ContainerOf(900)!.IsSelected.Should().BeFalse();
            host.Items[900].IsSelected.Should().BeFalse();
            host.ListBox.SelectedItems.Cast<object>().Should().NotContain(host.Items[900]);
        });
    }

    [Fact]
    public void Auswahl_ueberlebt_Scrollen_und_Recycling()
    {
        Run(host =>
        {
            var container2 = host.ContainerOf(2)!;
            container2.IsSelected = true; // wie ein Klick auf die Kachel
            host.Items[2].IsSelected.Should().BeTrue();

            host.Panel.SetVerticalOffset(host.Panel.ExtentHeight);
            host.Window.UpdateLayout();
            host.ContainerOf(2).Should().BeNull();
            host.Items[2].IsSelected.Should().BeTrue("die Auswahl lebt im Item, nicht im Container");

            host.Panel.SetVerticalOffset(0);
            host.Window.UpdateLayout();
            host.ContainerOf(2)!.IsSelected.Should().BeTrue();
            host.RealizedContainers().Count(c => c.IsSelected).Should().Be(1, "recycelte Container bringen keine fremde Auswahl mit");
        });
    }

    [Fact]
    public void Automation_Baum_mit_1000_Kacheln_bleibt_unter_100_Elementen()
    {
        Run(host =>
        {
            var peer = UIElementAutomationPeer.CreatePeerForElement(host.ListBox);
            peer.Should().NotBeNull();
            var count = CountPeers(peer!);
            _output.WriteLine($"UI-Automation-Elemente unter der ListBox: {count} (realisierte Container: {host.RealizedIndexes().Count})");
            count.Should().BeLessThan(100);
        });
    }

    [Fact]
    public void Messung_Erstaufbau_1000_Kacheln_WrapPanel_gegen_virtualisiert()
    {
        var wrap = TestUiThread.Run(() => MeasureFirstLayout(virtualizing: false), Timeout);
        var tile = TestUiThread.Run(() => MeasureFirstLayout(virtualizing: true), Timeout);
        _output.WriteLine($"WrapPanel (0.2.2):      Layout {wrap.LayoutMs:N0} ms, bis Render {wrap.RenderMs:N0} ms, {wrap.Containers} Container, {wrap.Peers} UIA-Elemente");
        _output.WriteLine($"VirtualizingTilePanel:  Layout {tile.LayoutMs:N0} ms, bis Render {tile.RenderMs:N0} ms, {tile.Containers} Container, {tile.Peers} UIA-Elemente");

        wrap.Containers.Should().Be(ItemCount);
        tile.Containers.Should().BeLessThanOrEqualTo(60);
        tile.Peers.Should().BeLessThan(100);
        tile.LayoutMs.Should().BeLessThan(wrap.LayoutMs);
    }

    [Fact]
    public void Thumbnail_Reihenfolge_sichtbare_Kacheln_zuerst_dann_nach_Abstand()
    {
        var page = Enumerable.Range(0, 20).Select(GalleryGridHost.CreateItem).ToList();
        var items = page.ToList();

        var ordered = GalleryViewModel.OrderByVisibility(items, page, first: 8, last: 11);

        ordered.Take(4).Select(i => i.Id - 1).Should().Equal(8, 9, 10, 11);
        ordered.Skip(4).Take(2).Select(i => i.Id - 1).Should().Equal(7, 12);
        ordered.Select(i => i.Id - 1).Should().BeEquivalentTo(Enumerable.Range(0, 20));
        GalleryViewModel.OrderByVisibility(items, page, -1, -1).Should().BeSameAs(items, "ohne bekannten Bereich bleibt die Reihenfolge");
    }

    // ---- Hilfen --------------------------------------------------------------------------------

    [Theory]
    [InlineData(120, 122)]
    [InlineData(320, 250)]
    public void Kachelgroesse_aendern_haelt_die_oberste_Kachel_im_Blick(double width, double height)
    {
        Run(host =>
        {
            var panel = host.Panel;
            panel.SetVerticalOffset(40 * GalleryGridHost.TileHeight + 30); // mitten in Zeile 40
            host.Window.UpdateLayout();
            var anchor = (int)(panel.VerticalOffset / panel.ItemHeight) * panel.Columns;

            panel.ItemWidth = width;
            panel.ItemHeight = height;
            host.Window.UpdateLayout();

            ((int)(panel.VerticalOffset / panel.ItemHeight)).Should().Be(anchor / panel.Columns, $"Item {anchor} bleibt in der obersten Zeile");
            host.ContainerOf(anchor).Should().NotBeNull();
            host.TopOf(host.ContainerOf(anchor)!).Should().BeInRange(0, height);
            panel.ExtentHeight.Should().Be(Math.Ceiling(ItemCount / (double)panel.Columns) * height);
        });
    }

    [Theory]
    [InlineData(50, 120)]
    [InlineData(129, 120)]
    [InlineData(131, 140)]
    [InlineData(187.4, 180)]
    [InlineData(999, 320)]
    public void Vorschaugroesse_wird_begrenzt_und_rastet_auf_20er_Stufen(double value, int expected)
        => GalleryViewModel.NormalizeTileSize(value).Should().Be(expected);

    private static void Run(Action<GalleryGridHost> test)
    {
        TestUiThread.Run(() =>
        {
            using var host = new GalleryGridHost(ItemCount);
            host.ShowAndLayout();
            test(host);
            return true;
        }, Timeout);
    }

    private static void AssertContainersMatchItems(GalleryGridHost host)
    {
        var generator = host.ListBox.ItemContainerGenerator;
        var realized = host.RealizedIndexes();
        realized.Should().NotBeEmpty();
        foreach (var index in realized)
        {
            var container = (ListBoxItem)generator.ContainerFromIndex(index);
            container.DataContext.Should().BeSameAs(host.Items[index], $"Container {index}");
            var expectedRow = index / host.Panel.Columns;
            var expectedTop = expectedRow * GalleryGridHost.TileHeight - host.Panel.VerticalOffset;
            (host.TopOf(container) - host.TopOf(host.ContainerOf(realized[0])!)).Should().BeApproximately(
                expectedTop - (realized[0] / host.Panel.Columns * GalleryGridHost.TileHeight - host.Panel.VerticalOffset), 0.5);
        }

        host.Panel.RealizedCount.Should().Be(realized.Count);
    }

    private static int CountPeers(AutomationPeer peer)
    {
        var children = peer.GetChildren();
        return children is null ? 1 : 1 + children.Sum(CountPeers);
    }

    private static (double LayoutMs, double RenderMs, int Containers, int Peers) MeasureFirstLayout(bool virtualizing)
    {
        using var host = new GalleryGridHost(ItemCount, virtualizing);
        host.Window.Show();
        host.Window.UpdateLayout();

        var sw = Stopwatch.StartNew();
        host.ListBox.ItemsSource = host.Items;
        host.Window.UpdateLayout();
        var layoutMs = sw.Elapsed.TotalMilliseconds;
        GalleryGridHost.Pump(DispatcherPriority.Loaded);
        var renderMs = sw.Elapsed.TotalMilliseconds;

        var peers = CountPeers(UIElementAutomationPeer.CreatePeerForElement(host.ListBox)!);
        return (layoutMs, renderMs, host.RealizedIndexes().Count, peers);
    }
}
