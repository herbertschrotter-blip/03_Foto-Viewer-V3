using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PhotoViewerV3.Persons.Controls;
using PhotoViewerV3.Places;
using PhotoViewerV3.Places.Dialogs;
using PhotoViewerV3.Places.Map;
using PhotoViewerV3.Places.Picker;
using PhotoViewerV3.Wpf.Tests.Fixtures;
using Xunit.Abstractions;

namespace PhotoViewerV3.Wpf.Tests.Places;

/// <summary>
/// Echte Seite „Orte“ im Fenster (Test-UI-Thread) mit Fake-Daten und ABGESCHALTETER Kachelquelle (kein Netz): XAML lädt,
/// Karte zeichnet Marker ohne Kacheln, ist ein Blatt im UI-Automation-Baum, „Ohne Ort“ ist virtualisiert, Kontextmenü
/// „Hier verorten“, Dialog „Fotos verorten“ und Kartenfenster „Ort wählen“.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class PlacesPageUiTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private readonly ITestOutputHelper _output;

    public PlacesPageUiTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Seite_laedt_ohne_Netz_zeichnet_Gruppen_und_bleibt_ein_kleiner_Automation_Baum()
    {
        TestUiThread.Run(() =>
        {
            var h = new PlacesHarness();
            h.SeedAustria();
            for (var i = 0; i < 500; i++)
            {
                h.World.Add(1000 + i, 1); // viele Fotos ohne Ort
            }

            var vm = h.Create();
            var page = new PlacesPage(vm);
            var window = OffscreenWindow(page);
            try
            {
                window.Show();
                PumpUntil(() => !vm.IsLoading && vm.LocatedCount == 5);
                window.UpdateLayout();
                DoEvents();

                var map = page.MapView;
                map.ActualWidth.Should().BeGreaterThan(300);
                vm.Viewport.Width.Should().BeApproximately(map.ActualWidth, 1, "Karte meldet ihre Größe");
                map.LastDrawnMarkers.Should().BeGreaterThan(0);
                map.LastDrawnTileImages.Should().Be(0, "Kachelquelle abgeschaltet — graue Karte");
                vm.TileStatusText.Should().Contain("abgeschaltet");
                vm.SummaryText.Should().Be("5 verortete Fotos · 503 ohne Ort");

                var marker = vm.Markers.First();
                map.MarkerAt(map.ScreenPointOf(marker)!.Value).Should().BeSameAs(marker, "Treffer auf dem Marker");

                var mapPeer = UIElementAutomationPeer.CreatePeerForElement(map)!;
                (mapPeer.GetChildren()?.Count ?? 0).Should().Be(0, "Marker sind keine eigenen Automation-Elemente");
                mapPeer.GetName().Should().Be("Karte");

                var list = (TileListBox)page.FindName("UnlocatedList");
                list.Items.Count.Should().Be(503);
                var realized = CountRealized(list);
                realized.Should().BeGreaterThan(0).And.BeLessThan(150, "virtualisiert");
                var container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
                UIElementAutomationPeer.CreatePeerForElement(container)!.GetChildren().Should().BeNull("Kachel ist ein Blatt");
                PumpUntil(() => h.Images.Requested.Contains(1000 + 499) || h.Images.Requested.Count > 5);
                h.Images.Requested.Should().NotBeEmpty("sichtbare Kacheln holen Vorschaubilder");
                _output.WriteLine($"Karte {map.ActualWidth:N0}×{map.ActualHeight:N0}, {map.LastDrawnMarkers} Marker, {realized} Kachel-Container von 503");

                // Kontextmenü: ohne Auswahl gesperrt, mit Auswahl „Hier verorten (1 Foto)“; auf einem Marker zusätzlich Galerie
                var empty = page.BuildContextMenu(new MapPointEventArgs(48.2, 16.37, null, new Point(10, 10)));
                var assign = empty.Items.OfType<MenuItem>().Single();
                assign.IsEnabled.Should().BeFalse();
                vm.SelectTile(vm.Unlocated[0], toggle: false, range: false);
                var menu = page.BuildContextMenu(new MapPointEventArgs(48.2, 16.37, marker, new Point(10, 10)));
                menu.Items.OfType<MenuItem>().Select(i => (string)i.Header).Should().Contain(["Hier verorten (1 Foto)"]);
                menu.Items.OfType<MenuItem>().Should().Contain(i => ((string)i.Header).StartsWith("In der Galerie zeigen"));
            }
            finally
            {
                window.Close();
            }

            return true;
        }, Timeout);
    }

    [Fact]
    public void Dialog_Fotos_verorten_liefert_Aenderungsdatum_beibehalten()
    {
        TestUiThread.Run(() =>
        {
            var dialog = new AssignLocationDialog(new AssignLocationRequest(3, 48.2082, 16.3738, "Wien, Österreich"))
            {
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -5000,
                Top = -5000,
            };
            dialog.Show();
            DoEvents();
            ((TextBlock)dialog.FindName("QuestionText")).Text.Should().Be("3 Fotos hier verorten?");
            ((TextBlock)dialog.FindName("PlaceText")).Text.Should().Be("Wien, Österreich");
            ((TextBlock)dialog.FindName("CoordinatesText")).Text.Should().Be("48,20820° N · 16,37380° O");
            ((CheckBox)dialog.FindName("KeepModifiedBox")).IsChecked = false;
            dialog.Accept();
            dialog.Answer.Should().Be(new AssignLocationAnswer(false));
            return true;
        }, Timeout);
    }

    [Fact]
    public void Kartenfenster_Ort_waehlen_setzt_Nadel_und_liefert_den_Punkt()
    {
        TestUiThread.Run(() =>
        {
            var world = new FakePlacesWorld();
            var window = new MapPickerWindow(null, world, "Ort setzen")
            {
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -5000,
                Top = -5000,
            };
            window.Show();
            DoEvents();
            window.Accept();
            window.IsVisible.Should().BeTrue("ohne Nadel kein Übernehmen");

            window.SetPoint(48.2082, 16.3738);
            ((TextBlock)window.FindName("PlaceText")).Text.Should().Be("Wien, Österreich");
            ((MapControl)window.FindName("Map")).Pin.Should().NotBeNull();
            window.Accept();
            window.Result.Should().Be((48.2082, 16.3738));
            return true;
        }, Timeout);
    }

    private static Window OffscreenWindow(UIElement content) => new()
    {
        Width = 1200,
        Height = 800,
        WindowStyle = WindowStyle.None,
        ShowInTaskbar = false,
        ShowActivated = false,
        WindowStartupLocation = WindowStartupLocation.Manual,
        Left = -5000,
        Top = -5000,
        Content = content,
    };

    private static void PumpUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            DoEvents();
            Thread.Sleep(5);
        }

        condition().Should().BeTrue("Bedingung innerhalb von 15 s");
    }

    private static void DoEvents()
    {
        var frame = new DispatcherFrame();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    private static int CountRealized(ItemsControl list)
    {
        var count = 0;
        for (var i = 0; i < list.Items.Count; i++)
        {
            if (list.ItemContainerGenerator.ContainerFromIndex(i) is not null)
            {
                count++;
            }
        }

        return count;
    }
}
