using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoViewerV3.Gallery.Controls;
using PhotoViewerV3.Lightbox;
using PhotoViewerV3.Persons;
using PhotoViewerV3.Persons.Controls;
using PhotoViewerV3.Persons.Dialogs;
using PhotoViewerV3.Persons.ViewModels;
using PhotoViewerV3.Services.Faces.Assignment;
using PhotoViewerV3.Services.Faces.Crops;
using PhotoViewerV3.Services.Faces.Indexing;
using PhotoViewerV3.Shell;
using PhotoViewerV3.Wpf.Tests.Fixtures;
using Xunit.Abstractions;

namespace PhotoViewerV3.Wpf.Tests.Persons;

/// <summary>
/// Echte Seite „Personen“ im Fenster (Test-UI-Thread): XAML lädt, Gruppenansicht mit 2 000 Gesichtern ist virtualisiert
/// (nur sichtbare Kacheln realisiert, sichtbarer Bereich ans ViewModel gemeldet), Kacheln sind Blatt-Peers, der
/// UI-Automation-Baum bleibt klein; Personen-Auswahl filtert.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class PersonsPageUiTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly ITestOutputHelper _output;

    public PersonsPageUiTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Gruppe_mit_2000_Gesichtern_realisiert_nur_sichtbare_Blatt_Kacheln()
    {
        TestUiThread.Run(() =>
        {
            var service = new FakeRecognitionService
            {
                Groups = new FaceGroupsResult([FakeRecognitionService.Group(1, 1990, 10), FakeRecognitionService.Group(2, 5, 0)], 2005, 1),
            };
            service.Persons.Add(new PersonOverview(new PersonSummary(7, "Anna", 3, 3, null, false), 0));
            var crops = new RecordingCrops();
            var vm = new PersonsViewModel(
                service,
                NullService.Create<IFaceIndexService>(),
                new FaceImageProvider(crops),
                new FakePersonsDialogs(),
                NullService.Create<ILightboxService>(),
                NullService.Create<INavigationService>(),
                NullLogger<PersonsViewModel>.Instance,
                new WeakReferenceMessenger());
            var page = new PersonsPage(vm);
            var window = new Window
            {
                Width = 1100,
                Height = 760,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -5000,
                Top = -5000,
                Content = page,
            };
            try
            {
                window.Show();
                window.UpdateLayout();
                DoEvents();
                vm.Groups.Should().HaveCount(2, "Seite lädt beim Anzeigen die Übersicht");
                vm.Persons.Should().ContainSingle();

                vm.ShowGroup(service.Groups.Groups[0]);
                window.UpdateLayout();
                DoEvents();

                var list = (TileListBox)page.FindName("GroupMembersList");
                var panel = FindVisualChild<VirtualizingTilePanel>(list)!;
                panel.Should().NotBeNull();
                list.Items.Count.Should().Be(2000);
                panel.RealizedCount.Should().BeGreaterThan(0).And.BeLessThan(120, "UI-virtualisiert: nur sichtbare Zeilen plus Puffer");
                crops.Requested.Should().NotBeEmpty("sichtbarer Bereich fordert Ausschnitte an");
                crops.Requested.Count.Should().BeLessThan(200, "nur sichtbarer Bereich plus Puffer, nicht alle 2 000");

                var container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
                container.Should().BeOfType<TileListBoxItem>();
                var itemPeer = UIElementAutomationPeer.CreatePeerForElement(container);
                itemPeer!.GetChildren().Should().BeNull("Kachel ist ein Blatt im UI-Automation-Baum");
                itemPeer.GetName().Should().StartWith("Gesicht");
                var listPeer = UIElementAutomationPeer.CreatePeerForElement(list)!;
                var count = CountPeers(listPeer);
                _output.WriteLine($"Gruppe 2 000 Gesichter: {panel.RealizedCount} Container, {count} UIA-Elemente, {crops.Requested.Count} Ausschnitte angefordert");

                panel.SetVerticalOffset(panel.ExtentHeight);
                window.UpdateLayout();
                DoEvents();
                panel.RealizedCount.Should().BeLessThan(120);
                crops.Requested.Should().Contain(1000L + 2000, "Ende der Liste angefordert, nachdem dorthin gescrollt wurde");
            }
            finally
            {
                window.Close();
            }

            return true;
        }, Timeout);
    }

    [Fact]
    public void Personenauswahl_filtert_ohne_Gross_Kleinschreibung()
    {
        TestUiThread.Run(() =>
        {
            IReadOnlyList<PersonSummary> persons =
            [
                new(1, "Anna", 1, 1, null, false),
                new(2, "Bert", 1, 1, null, false),
                new(3, "Johanna", 1, 1, null, false),
            ];
            PersonPickerDialog.Filter(persons, "ANN").Select(p => p.Name).Should().Equal("Anna", "Johanna");
            PersonPickerDialog.Filter(persons, " ").Should().HaveCount(3);

            var dialog = new PersonPickerDialog("Zu Person hinzufügen", persons, 2) { ShowActivated = false, Left = -5000, Top = -5000, WindowStartupLocation = WindowStartupLocation.Manual };
            dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(() => dialog.Finish(persons[1]));
            dialog.ShowDialog();
            dialog.Result.Should().Be(persons[1]);
            return true;
        }, Timeout);
    }

    /// <summary>Arbeitet die Dispatcher-Warteschlange bis Background ab (Loaded, gebündelte Meldungen).</summary>
    private static void DoEvents()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        _ = System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => frame.Continue = false);
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    private static int CountPeers(AutomationPeer peer)
    {
        var children = peer.GetChildren();
        return children is null ? 1 : 1 + children.Sum(CountPeers);
    }

    private static T? FindVisualChild<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            if (FindVisualChild<T>(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private sealed class RecordingCrops : IFaceCropService
    {
        public HashSet<long> Requested { get; } = [];

        public event Action<FaceCropReadyEventArgs>? CropReady
        {
            add { }
            remove { }
        }

        public string GetCropPath(int libraryId, long faceId) => string.Empty;

        public void Request(int libraryId, IReadOnlyCollection<long> faceIds) => Requested.UnionWith(faceIds);

        public void Delete(int libraryId, IEnumerable<long> faceIds)
        {
        }

        public void DeleteAll()
        {
        }

        public void Start()
        {
        }
    }
}
