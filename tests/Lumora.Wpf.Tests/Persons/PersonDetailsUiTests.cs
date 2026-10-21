using System.Diagnostics;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Domain.Entities;
using Lumora.Lightbox;
using Lumora.Persons;
using Lumora.Persons.Controls;
using Lumora.Persons.Dialogs;
using Lumora.Persons.ViewModels;
using Lumora.Services.Faces.Assignment;
using Lumora.Services.Faces.Crops;
using Lumora.Services.Faces.Indexing;
using Lumora.Shell;
using Lumora.Wpf.Tests.Fixtures;

namespace Lumora.Wpf.Tests.Persons;

/// <summary>
/// Echte Fenster (0.2.47, ADR-0045): Personen-Dialog lädt ohne Binding-Fehler und liefert die Eingabe; Personen-Seite
/// nach Beziehung gruppiert (Abschnitte mit Blatt-Kacheln, Titelbilder angefordert), Detailzeile; Personenauswahl
/// findet auch Nachname und Beziehung.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class PersonDetailsUiTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    [Fact]
    public void Personen_Dialog_bindet_fehlerfrei_und_liefert_die_Eingabe()
    {
        TestUiThread.Run(() =>
        {
            using var errors = new BindingErrors();
            var request = new PersonEditRequest("Als neue Person benennen", "Wer ist das?", "Benennen",
                new PersonDetailsInput("Anna", "Muster", null, "Schwester", PersonBirthDate.Create(1975, 3, null)),
                ["Schwester", "Bruder"], ["Bert"], PersonsViewModel.ExistingPersonHintForGroup, "Herbert");
            var dialog = new PersonDetailsDialog(new PersonDetailsEditor(request))
            {
                ShowActivated = false, Left = -5000, Top = -5000, WindowStartupLocation = WindowStartupLocation.Manual,
            };
            dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(() =>
            {
                dialog.Editor.Nickname = "Annerl";
                dialog.Editor.IsMe = true;
                dialog.Finish(dialog.Editor.BuildInput());
            }, System.Windows.Threading.DispatcherPriority.Background);
            dialog.ShowDialog();

            dialog.Result.Should().Be(new PersonDetailsInput("Anna", "Muster", "Annerl", "Schwester", PersonBirthDate.Create(1975, 3, null), true));
            errors.Messages.Should().BeEmpty();

            IReadOnlyList<PersonSummary> persons =
            [
                new(1, "Anna Muster", 1, 1, null, false) { Details = new PersonDetails(1, "Anna", "Muster", null, 1, "Schwester", null, false) },
                new(2, "Opa Franz", 1, 1, null, false) { Details = new PersonDetails(2, "Franz", "Huber", "Opa Franz", null, null, null, false) },
            ];
            PersonPickerDialog.Filter(persons, "huber").Select(p => p.Id).Should().Equal(2);
            PersonPickerDialog.Filter(persons, "schwester").Select(p => p.Id).Should().Equal(1);
            return true;
        }, Timeout);
    }

    [Fact]
    public void Personen_Seite_gruppiert_nach_Beziehung_mit_Blatt_Kacheln()
    {
        TestUiThread.Run(() =>
        {
            using var errors = new BindingErrors();
            var service = new FakeRecognitionService();
            void Add(int id, string first, string? relation)
            {
                var d = new PersonDetails(id, first, "Muster", null, relation?.Length, relation, PersonBirthDate.Create(1980, null, null), id == 1);
                service.Persons.Add(new PersonOverview(new PersonSummary(id, d.DisplayName, 2, 2, id * 10, false) { Details = d }, 0));
            }

            Add(1, "Anna", "Schwester");
            Add(2, "Berta", "Schwester");
            Add(3, "Carl", null);
            service.PersonFaces[1] = new PersonFaces(service.Persons[0].Person, [FakeRecognitionService.Face(10, Domain.Enums.FaceState.Confirmed)], []);
            var crops = new RecordingCrops();
            var vm = new PersonsViewModel(
                service,
                NullService.Create<IFaceIndexService>(),
                new FaceImageProvider(crops),
                new FakePersonsDialogs(),
                NullService.Create<ILightboxService>(),
                NullService.Create<INavigationService>(),
                NullLogger<PersonsViewModel>.Instance,
                new WeakReferenceMessenger(),
                details: new FakePersonDetailsService(service));
            var page = new PersonsPage(vm);
            var window = new Window
            {
                Width = 1100, Height = 760, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -5000, Top = -5000, Content = page,
            };
            try
            {
                window.Show();
                window.UpdateLayout();
                DoEvents();
                vm.Persons.Should().HaveCount(3);

                vm.GroupByRelation = true;
                window.UpdateLayout();
                DoEvents();
                var lists = FindVisualChildren<TileListBox>(page).Where(l => l.IsVisible && l.Name.Length == 0).ToList();
                lists.Should().HaveCount(2, "Abschnitte „Schwester“ und „Ohne Beziehung“");
                lists[0].Items.Count.Should().Be(2);
                var container = (ListBoxItem)lists[0].ItemContainerGenerator.ContainerFromIndex(0);
                container.Should().BeOfType<TileListBoxItem>();
                UIElementAutomationPeer.CreatePeerForElement(container)!.GetChildren().Should().BeNull("Blatt im UI-Automation-Baum");
                crops.Requested.Should().Contain([10L, 20L, 30L], "gruppierte Ansicht fordert alle Titelbilder an");

                vm.LoadPersonAsync(1, keepState: false).GetAwaiter().GetResult();
                window.UpdateLayout();
                DoEvents();
                vm.PersonInfoText.Should().Contain("Schwester").And.Contain("geboren 1980").And.Contain("Das bin ich");
                vm.EditPersonCommand.CanExecute(null).Should().BeTrue();
                errors.Messages.Should().BeEmpty();
            }
            finally
            {
                window.Close();
            }

            return true;
        }, Timeout);
    }

    private static void DoEvents()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        _ = System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => frame.Continue = false);
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var found in FindVisualChildren<T>(child))
            {
                yield return found;
            }
        }
    }

    /// <summary>Sammelt WPF-Binding-Fehler während des Tests.</summary>
    private sealed class BindingErrors : TraceListener
    {
        public BindingErrors()
        {
            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Listeners.Add(this);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        }

        public List<string> Messages { get; } = [];

        public override void Write(string? message)
        {
        }

        public override void WriteLine(string? message)
        {
            if (message is not null)
            {
                Messages.Add(message);
            }
        }

        protected override void Dispose(bool disposing)
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(this);
            base.Dispose(disposing);
        }
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
