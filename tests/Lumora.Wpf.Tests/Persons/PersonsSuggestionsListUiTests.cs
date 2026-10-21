using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Domain.Enums;
using Lumora.Gallery.Controls;
using Lumora.Lightbox;
using Lumora.Persons;
using Lumora.Persons.ViewModels;
using Lumora.Services.Faces.Assignment;
using Lumora.Services.Faces.Crops;
using Lumora.Services.Faces.Indexing;
using Lumora.Shell;
using Lumora.Wpf.Tests.Fixtures;
using Xunit.Abstractions;

namespace Lumora.Wpf.Tests.Persons;

/// <summary>
/// Herberts Meldung (0.2.40): „Person mit 30 Vorschlägen — ich sehe nicht alle, nach ‚alle bestätigen‘ schon.“ Echte
/// Seite wie in der App in einer WPF-UI-NavigationView, kleines Fenster: alle Vorschläge sind in der Liste, die Liste
/// ist scrollbar (nicht abgeschnitten), Scrollen und Reiterwechsel fordern die Ausschnitte der neu sichtbaren Kacheln an,
/// die Zeile „Zeigt … von …“ stimmt mit dem Kopf überein. Dazu die Ansicht „Ignorierte Gesichter“ mit 2 000 Gesichtern
/// (virtualisiert, Blatt-Kacheln, Nachladen am Ende).
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class PersonsSuggestionsListUiTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    [Fact]
    public void Sechzig_Vorschlaege_im_kleinen_Fenster_sind_alle_erreichbar_und_laden_ihre_Ausschnitte()
    {
        TestUiThread.Run(() =>
        {
            var service = new FakeRecognitionService();
            service.Persons.Add(new PersonOverview(new PersonSummary(7, "Anna", 1, 1, null, false), 60));
            service.PersonFaces[7] = new PersonFaces(service.Persons[0].Person, [FakeRecognitionService.Face(1, FaceState.Confirmed)],
                Enumerable.Range(100, 60).Select(i => FakeRecognitionService.Face(i, FaceState.Suggested, 0.8f)).ToList());
            service.PersonFaces[8] = new PersonFaces(new PersonSummary(8, "Bert", 1, 1, null, false), [FakeRecognitionService.Face(2, FaceState.Confirmed)], []);
            var crops = new RecordingCrops();
            var (vm, page, window) = Host(service, crops, new FakeIgnoreService());
            try
            {
                // erst eine Person ohne Vorschläge (Reiter „Bestätigt“), dann Anna
                vm.LoadPersonAsync(8, keepState: false).GetAwaiter().GetResult();
                Pump(window);
                vm.DetailTab.Should().Be(1);
                vm.LoadPersonAsync(7, keepState: false).GetAwaiter().GetResult();
                Pump(window);

                var list = (ListBox)page.FindName("SuggestionsList");
                var panel = FindVisualChild<VirtualizingTilePanel>(list)!;
                vm.DetailTab.Should().Be(0);
                list.Items.Count.Should().Be(60, "alle Vorschläge sind in der Liste");
                vm.PersonDetails.Should().Contain("60 Vorschläge");
                panel.ExtentHeight.Should().BeGreaterThan(panel.ViewportHeight, "im kleinen Fenster passt nicht alles — die Liste scrollt");
                list.ActualHeight.Should().BeLessThan(window.ActualHeight, "Liste hat eine begrenzte Höhe (keine äußere Bildlaufleiste)");
                panel.ScrollOwner.Should().NotBeNull();
                vm.SuggestionsRangeText.Should().StartWith("Zeigt 1–").And.Contain("von 60 Vorschlägen").And.EndWith("nach unten scrollen für weitere.");
                output.WriteLine($"Liste {list.ActualHeight:0} px, Viewport {panel.ViewportHeight:0}/{panel.ExtentHeight:0}, {panel.RealizedCount} Container — {vm.SuggestionsRangeText}");
                crops.Requested.Should().Contain(100L).And.NotContain(159L, "Ausschnitte nur für den sichtbaren Bereich plus Puffer");

                panel.SetVerticalOffset(panel.ExtentHeight);
                Pump(window);
                crops.Requested.Should().Contain(159L, "nach dem Scrollen ans Ende angefordert");
                vm.SuggestionsRangeText.Should().Contain("–60 von 60").And.EndWith(".");

                // Reiterwechsel: Bereich bleibt gleich (kein VisibleRangeChanged) — Ausschnitte trotzdem angefordert
                crops.Requested.Clear();
                vm.DetailTab = 1;
                Pump(window);
                vm.DetailTab = 0;
                Pump(window);
                crops.Requested.Should().Contain(159L, "Reiterwechsel fordert die sichtbaren Ausschnitte erneut an");
            }
            finally
            {
                window.Close();
            }

            return true;
        }, Timeout);
    }

    [Fact]
    public void Ignorierte_Gesichter_2000_virtualisiert_als_Blatt_Kacheln_und_mit_Nachladen()
    {
        TestUiThread.Run(() =>
        {
            var ignore = new FakeIgnoreService();
            ignore.Faces.AddRange(Enumerable.Range(1, 2000).Select(i =>
                new IgnoredFaceInfo(i, 5000 - i, new System.Drawing.RectangleF(0.3f, 0.2f, 0.1f, 0.1f), i % 3 == 0 ? 18 : 60, i % 3 == 0)));
            var crops = new RecordingCrops();
            var (vm, page, window) = Host(new FakeRecognitionService(), crops, ignore);
            try
            {
                vm.ShowIgnoredCommand.ExecuteAsync(null).GetAwaiter().GetResult();
                Pump(window);
                var list = (ListBox)page.FindName("IgnoredList");
                var panel = FindVisualChild<VirtualizingTilePanel>(list)!;
                list.Items.Count.Should().Be(PersonsViewModel.IgnoredPageSize);
                panel.RealizedCount.Should().BeGreaterThan(0).And.BeLessThan(120, "UI-virtualisiert");
                var container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(2);
                var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(container)!;
                peer.GetChildren().Should().BeNull("Kachel ist ein Blatt");
                peer.GetName().Should().Contain("zu klein");

                for (var i = 0; i < 4 && list.Items.Count < 2000; i++)
                {
                    panel.SetVerticalOffset(panel.ExtentHeight);
                    Pump(window);
                }

                list.Items.Count.Should().Be(2000, "beim Scrollen ans Ende seitenweise nachgeladen");
                panel.SetVerticalOffset(panel.ExtentHeight);
                Pump(window);
                vm.IgnoredRangeText.Should().EndWith("von 2.000 ignorierten Gesichtern.".Replace("2.000", 2000.ToString("N0", System.Globalization.CultureInfo.CurrentCulture), StringComparison.Ordinal));
                panel.RealizedCount.Should().BeLessThan(120);
                crops.Requested.Should().Contain(2000L);
            }
            finally
            {
                window.Close();
            }

            return true;
        }, Timeout);
    }

    /// <summary>Seite wie in der App: WPF-UI-NavigationView mit Seitenanbieter, kleines Fenster außerhalb des Bildschirms.</summary>
    private static (PersonsViewModel Vm, PersonsPage Page, Window Window) Host(FakeRecognitionService service, IFaceCropService crops, IFaceIgnoreService ignore)
    {
        var vm = new PersonsViewModel(service, NullService.Create<IFaceIndexService>(), new FaceImageProvider(crops), new FakePersonsDialogs(),
            NullService.Create<ILightboxService>(), NullService.Create<INavigationService>(), NullLogger<PersonsViewModel>.Instance,
            new WeakReferenceMessenger(), ignore);
        var page = new PersonsPage(vm);
        var nav = new global::Wpf.Ui.Controls.NavigationView { PaneDisplayMode = global::Wpf.Ui.Controls.NavigationViewPaneDisplayMode.Left };
        nav.MenuItems.Add(new global::Wpf.Ui.Controls.NavigationViewItem { Content = "Personen", TargetPageType = typeof(PersonsPage), TargetPageTag = "persons" });
        nav.SetPageProviderService(new SinglePageProvider(page));
        var window = new Window
        {
            Width = 1000,
            Height = 600,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -5000,
            Top = -5000,
            Content = nav,
        };
        window.Show();
        nav.Navigate(typeof(PersonsPage));
        Pump(window);
        return (vm, page, window);
    }

    private static void Pump(Window window)
    {
        for (var i = 0; i < 6; i++)
        {
            window.UpdateLayout();
            var frame = new System.Windows.Threading.DispatcherFrame();
            _ = System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => frame.Continue = false);
            System.Windows.Threading.Dispatcher.PushFrame(frame);
        }
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

    private sealed class SinglePageProvider(object page) : global::Wpf.Ui.Abstractions.INavigationViewPageProvider
    {
        public object? GetPage(Type pageType) => page;
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
