using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PhotoViewerV3.Gallery.Controls;
using PhotoViewerV3.Gallery.ViewModels;
using PhotoViewerV3.Wpf.Tests.Fixtures;
using Xunit.Abstractions;

namespace PhotoViewerV3.Wpf.Tests.Gallery;

/// <summary>
/// Container-Zuordnung im Kachel-Raster (0.2.42): Für jeden sichtbaren Platz muss der per Treffertest getroffene
/// <see cref="ListBoxItem"/> genau das Item dieses Platzes tragen — als <c>DataContext</c> (das liest der Doppelklick)
/// und als angezeigten Inhalt (<c>Content</c>). Fehlerbild: Kachel 13 zeigte Datei A, Doppelklick öffnete Datei B
/// (Item 72, kurz vorher fokussiert).
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class TileContainerMappingTests
{
    private const int ItemCount = 256;
    private const int FocusIndex = 72;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly ITestOutputHelper _output;

    public TileContainerMappingTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [InlineData(VirtualizationMode.Recycling)]
    [InlineData(VirtualizationMode.Standard)]
    public void Fokussierte_Kachel_72_dann_nach_oben_scrollen_jeder_Platz_traegt_sein_Item(VirtualizationMode mode)
    {
        Run(mode, host =>
        {
            var panel = host.Panel;
            FocusItem(host, FocusIndex);
            AssertSlots(host, "nach Fokus auf 72");

            // In Mausrad-Rastungen zurück nach oben, dabei zwischendurch kleine Sprünge hin und her.
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
                AssertSlots(host, $"Mausrad-Schritt {step}, Offset {panel.VerticalOffset:N0}");
            }

            host.ContainerOf(FocusIndex).Should().NotBeNull("der fokussierte Container bleibt außerhalb des Viewports erhalten");
            AssertSlots(host, "oben angekommen");

            // Sprünge: ganz nach unten, oben, in die Mitte, oben.
            foreach (var offset in new[] { panel.ExtentHeight, 0, panel.ExtentHeight / 2, 0, 3 * GalleryGridHost.TileHeight, 0 })
            {
                panel.SetVerticalOffset(offset);
                host.Window.UpdateLayout();
                AssertSlots(host, $"Sprung auf {offset:N0}");
            }
        });
    }

    [Theory]
    [InlineData(VirtualizationMode.Recycling)]
    [InlineData(VirtualizationMode.Standard)]
    public void Ohne_Fokus_hin_und_her_scrollen_jeder_Platz_traegt_sein_Item(VirtualizationMode mode)
    {
        Run(mode, host =>
        {
            var panel = host.Panel;
            for (var i = 0; i < 30; i++)
            {
                panel.MouseWheelDown();
                host.Window.UpdateLayout();
                AssertSlots(host, $"runter {i}");
            }

            for (var i = 0; i < 30; i++)
            {
                panel.MouseWheelUp();
                host.Window.UpdateLayout();
                AssertSlots(host, $"hoch {i}");
            }
        });
    }

    [Theory]
    [InlineData(VirtualizationMode.Recycling)]
    [InlineData(VirtualizationMode.Standard)]
    public void Fokus_per_Tastatur_bewegen_und_zurueckscrollen_jeder_Platz_traegt_sein_Item(VirtualizationMode mode)
    {
        Run(mode, host =>
        {
            var panel = host.Panel;
            FocusItem(host, FocusIndex);

            // Pfeiltasten wie die echte Navigation: Fokus wandert, Container bleiben Kinder des Panels.
            for (var i = 0; i < 6; i++)
            {
                SendKey(host, i % 2 == 0 ? Key.Down : Key.Right);
                host.Window.UpdateLayout();
                AssertSlots(host, $"Taste {i}");
            }

            panel.SetVerticalOffset(0);
            host.Window.UpdateLayout();
            AssertSlots(host, "oben nach Tastatur");

            for (var i = 0; i < 8; i++)
            {
                panel.MouseWheelDown();
                host.Window.UpdateLayout();
                AssertSlots(host, $"runter {i}");
            }

            panel.SetVerticalOffset(0);
            host.Window.UpdateLayout();
            AssertSlots(host, "wieder oben");
        });
    }

    [Theory]
    [InlineData(VirtualizationMode.Recycling)]
    [InlineData(VirtualizationMode.Standard)]
    public void Lightbox_Fenster_oeffnen_schliessen_dann_nach_oben_jeder_Platz_traegt_sein_Item(VirtualizationMode mode)
    {
        Run(mode, host =>
        {
            var panel = host.Panel;
            FocusItem(host, FocusIndex);

            // Lightbox-Simulation: ein anderes Fenster holt sich den Fokus, schließt wieder; der Galerie-Fokus kehrt zurück.
            var lightbox = new Window
            {
                Width = 200,
                Height = 200,
                Left = -4000,
                Top = -4000,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Content = new Button { Content = "x" },
            };
            lightbox.Show();
            lightbox.Activate();
            ((Button)lightbox.Content).Focus();
            GalleryGridHost.Pump(DispatcherPriority.Background);
            lightbox.Close();
            host.Window.Activate();
            GalleryGridHost.Pump(DispatcherPriority.Background);

            for (var step = 0; panel.VerticalOffset > 0 && step < 200; step++)
            {
                panel.MouseWheelUp();
                host.Window.UpdateLayout();
                AssertSlots(host, $"nach Lightbox, Schritt {step}");
            }

            AssertSlots(host, "nach Lightbox oben");
        });
    }

    [Theory]
    [InlineData(VirtualizationMode.Recycling)]
    [InlineData(VirtualizationMode.Standard)]
    public void Seiten_Reload_mit_wiederverwendeten_Items_bei_Fokus_ausserhalb_jeder_Platz_traegt_sein_Item(VirtualizationMode mode)
    {
        Run(mode, host =>
        {
            var panel = host.Panel;
            FocusItem(host, FocusIndex);
            panel.SetVerticalOffset(0);
            host.Window.UpdateLayout();
            AssertSlots(host, "oben vor Reload");

            // Reload wie ApplyRows: einige Items ersetzt (neue Instanz), andere verschoben (Sortierung), eins entfernt.
            host.Items[3] = GalleryGridHost.CreateItem(3);
            host.Items.Move(FocusIndex, 5);
            host.Items.Move(10, 90);
            host.Items.RemoveAt(40);
            host.Items.Insert(0, GalleryGridHost.CreateItem(900));
            host.Window.UpdateLayout();
            AssertSlots(host, "nach Reload");

            for (var i = 0; i < 12; i++)
            {
                panel.MouseWheelDown();
                host.Window.UpdateLayout();
                AssertSlots(host, $"nach Reload runter {i}");
            }

            panel.SetVerticalOffset(0);
            host.Window.UpdateLayout();
            AssertSlots(host, "nach Reload oben");
        });
    }

    // ---- Hilfen --------------------------------------------------------------------------------

    private static void Run(VirtualizationMode mode, Action<GalleryGridHost> test)
    {
        TestUiThread.Run(() =>
        {
            using var host = new GalleryGridHost(ItemCount, width: 600, height: 420);
            VirtualizingPanel.SetVirtualizationMode(host.ListBox, mode);
            host.Window.ShowActivated = true;
            host.ShowAndLayout();
            host.Window.Activate();
            GalleryGridHost.Pump(DispatcherPriority.Background);
            test(host);
            return true;
        }, Timeout);
    }

    private static void FocusItem(GalleryGridHost host, int index)
    {
        host.ListBox.ScrollIntoView(host.Items[index]);
        host.Window.UpdateLayout();
        var container = host.ContainerOf(index)!;
        container.IsSelected = true;
        container.Focus().Should().BeTrue("der Container muss den Tastaturfokus bekommen (wie nach dem Doppelklick)");
        GalleryGridHost.Pump(DispatcherPriority.Background);
        container.IsKeyboardFocusWithin.Should().BeTrue();
    }

    private static void SendKey(GalleryGridHost host, Key key)
    {
        var target = Keyboard.FocusedElement as IInputElement ?? host.ListBox;
        var source = PresentationSource.FromVisual(host.Window)!;
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.KeyDownEvent };
        target.RaiseEvent(args);
    }

    /// <summary>Treffertest in der Mitte jedes (vollständig oder teilweise) sichtbaren Platzes.</summary>
    private void AssertSlots(GalleryGridHost host, string stage)
    {
        var panel = host.Panel;
        var columns = panel.Columns;
        var w = panel.ItemWidth;
        var h = panel.ItemHeight;
        var offset = panel.VerticalOffset;
        var firstRow = (int)Math.Floor(offset / h);
        var lastRow = (int)Math.Ceiling((offset + panel.ViewportHeight) / h) - 1;
        var errors = new List<string>();
        var checkedSlots = 0;

        for (var row = firstRow; row <= lastRow; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                var index = row * columns + column;
                if (index >= host.Items.Count)
                {
                    break;
                }

                // Platzmitte, auf den sichtbaren Teil der Zeile geklemmt.
                var top = Math.Max(row * h - offset, 0);
                var bottom = Math.Min(row * h - offset + h, panel.ViewportHeight);
                if (bottom - top < 20)
                {
                    continue;
                }

                var point = new Point(column * w + w / 2, (top + bottom) / 2);
                var hit = panel.InputHitTest(point) as DependencyObject;
                var container = FindAncestor<ListBoxItem>(hit);
                checkedSlots++;
                var expected = host.Items[index];
                if (container is null)
                {
                    errors.Add($"Platz {index}: kein Container getroffen");
                    continue;
                }

                if (!ReferenceEquals(container.DataContext, expected))
                {
                    errors.Add($"Platz {index}: DataContext = {Describe(host, container.DataContext)}");
                }

                if (!ReferenceEquals(container.Content, expected))
                {
                    errors.Add($"Platz {index}: Content = {Describe(host, container.Content)}");
                }

                var presenter = GalleryGridHost.FindVisualChild<ContentPresenter>(container);
                if (presenter is not null && !ReferenceEquals(presenter.Content, expected))
                {
                    errors.Add($"Platz {index}: ContentPresenter.Content = {Describe(host, presenter.Content)}");
                }

                // Was die Kachel tatsächlich zeigt: DataContext der Vorlagen-Wurzel (Name, Vorschaubild).
                var shown = presenter is not null && VisualTreeHelper.GetChildrenCount(presenter) > 0
                    ? (VisualTreeHelper.GetChild(presenter, 0) as FrameworkElement)?.DataContext
                    : null;
                if (!ReferenceEquals(shown, expected))
                {
                    errors.Add($"Platz {index}: angezeigt = {Describe(host, shown)}");
                }

                // Sichtbarer Dateiname: veraltete Bindungen behalten bei DisconnectedItem ihren letzten Wert.
                var names = TextsOf(container).Where(t => t.StartsWith("IMG_", StringComparison.Ordinal)).ToList();
                if (!names.SequenceEqual(new[] { expected.Name }))
                {
                    errors.Add($"Platz {index}: sichtbarer Name = {string.Join("|", names)} statt {expected.Name}");
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

    private static string Describe(GalleryGridHost host, object? value)
        => value is GalleryItem item ? $"Item {host.Items.IndexOf(item)} (Id {item.Id})" : value?.ToString() ?? "null";

    private static string DumpChildren(GalleryGridHost host)
    {
        var panel = host.Panel;
        var generator = host.ListBox.ItemContainerGenerator;
        var sb = new StringBuilder();
        var children = VisualTreeHelper.GetChildrenCount(panel);
        for (var i = 0; i < children; i++)
        {
            var child = VisualTreeHelper.GetChild(panel, i);
            var index = generator.IndexFromContainer(child);
            var top = child is UIElement element ? element.TranslatePoint(new Point(0, 0), panel).Y : double.NaN;
            sb.Append($"[{i}:{index}@{top:N0}] ");
        }

        return sb.ToString();
    }

    private static IEnumerable<string> TextsOf(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
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
