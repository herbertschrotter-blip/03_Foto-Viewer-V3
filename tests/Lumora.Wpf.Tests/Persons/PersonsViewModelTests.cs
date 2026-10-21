using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Domain.Enums;
using Lumora.Lightbox;
using Lumora.Persons;
using Lumora.Persons.ViewModels;
using Lumora.Services.Faces.Assignment;
using Lumora.Services.Faces.Crops;
using Lumora.Services.Faces.Indexing;
using Lumora.Shell;
using Lumora.Wpf.Tests.Fixtures;

namespace Lumora.Wpf.Tests.Persons;

/// <summary>
/// ViewModel der Seite „Personen“ (ADR-0037) ohne UI-Thread: Gruppe öffnen (Ausreißer am Ende und ABGEWÄHLT),
/// Benennen und Hinzufügen mit Ausreißer-Rückfrage, Vorschläge bestätigen/ablehnen, Zusammenführen, Kopfzeile.
/// </summary>
public sealed class PersonsViewModelTests
{
    [Fact]
    public async Task Gruppe_oeffnen_waehlt_Ausreisser_ab_und_zeigt_sie_am_Ende()
    {
        var service = new FakeRecognitionService { Groups = new FaceGroupsResult([FakeRecognitionService.Group(1, 6, 2)], 8, 1) };
        var vm = Create(service, new FakePersonsDialogs());
        await vm.LoadOverviewAsync();

        vm.Groups.Should().ContainSingle();
        vm.OpenGroupCommand.Execute(vm.Groups[0]);

        vm.Mode.Should().Be(PersonsPageMode.Group);
        vm.GroupMembers.Should().HaveCount(8);
        vm.GroupMembers.TakeLast(2).Should().OnlyContain(t => t.IsOutlier && !t.IsSelected, "Ausreißer sind markiert und abgewählt");
        vm.GroupMembers.Take(6).Should().OnlyContain(t => !t.IsOutlier && t.IsSelected);
        vm.GroupSelectedCount.Should().Be(6);
        vm.GroupSelectionText.Should().Be("6 von 8 ausgewählt");
        vm.GroupHint.Should().Contain("2 Gesichter passen nicht sicher dazu").And.Contain("abgewählt");
        vm.GroupMembers[^1].AutomationName.Should().Contain("Ausreißer");

        vm.ToggleTileCommand.Execute(vm.GroupMembers[^1]); // Herbert wählt einen Ausreißer doch an
        vm.GroupSelectedCount.Should().Be(7);
        vm.SelectWithoutOutliersCommand.Execute(null);
        vm.GroupSelectedCount.Should().Be(6);
        vm.SelectNoneGroupCommand.Execute(null);
        vm.NameGroupCommand.CanExecute(null).Should().BeFalse("ohne Auswahl nichts zu benennen");
    }

    [Fact]
    public async Task Benennen_mit_Rueckfrage_bestaetigt_ohne_die_markierten_Ausreisser()
    {
        var service = new FakeRecognitionService { Groups = new FaceGroupsResult([FakeRecognitionService.Group(1, 5, 1)], 6, 1) };
        service.Outliers.Add(1003); // ein ausgewähltes Gesicht passt laut Prüfung nicht
        var dialogs = new FakePersonsDialogs { NextName = "  Anna ", OutlierDecision = OutlierDecision.SkipOutliers };
        var vm = Create(service, dialogs);
        await vm.LoadOverviewAsync();
        vm.OpenGroupCommand.Execute(vm.Groups[0]);

        await vm.NameGroupCommand.ExecuteAsync(null);

        service.Checks.Should().ContainSingle().Which.PersonId.Should().BeNull("neue Person: Prüfung gegen bekannte Personen");
        service.Checks[0].Faces.Should().BeEquivalentTo(new long[] { 1001, 1002, 1003, 1004, 1005 }, "nur die Auswahl — der Gruppen-Ausreißer 1006 ist abgewählt");
        dialogs.OutlierQuestions.Should().ContainSingle().Which.Should().Match<(string PersonName, IReadOnlyList<FaceOutlierResult> Outliers, int Total)>(q => q.PersonName == "Anna" && q.Outliers.Count == 1 && q.Total == 5);
        service.Created.Should().Equal("Anna");
        service.Confirmed.Should().ContainSingle();
        service.Confirmed[0].Faces.Should().BeEquivalentTo(new long[] { 1001, 1002, 1004, 1005 });
        service.Confirmed[0].PersonId.Should().Be(100);
        vm.Mode.Should().Be(PersonsPageMode.Overview);
        vm.Message.Should().Contain("Anna");
    }

    [Fact]
    public async Task Benennen_abbrechen_bei_der_Rueckfrage_legt_nichts_an()
    {
        var service = new FakeRecognitionService { Groups = new FaceGroupsResult([FakeRecognitionService.Group(1, 4, 0)], 4, 1) };
        service.Outliers.Add(1001);
        var dialogs = new FakePersonsDialogs { NextName = "Bert", OutlierDecision = OutlierDecision.Cancel };
        var vm = Create(service, dialogs);
        await vm.LoadOverviewAsync();
        vm.OpenGroupCommand.Execute(vm.Groups[0]);

        await vm.NameGroupCommand.ExecuteAsync(null);

        service.Created.Should().BeEmpty("Person erst nach der Entscheidung anlegen");
        service.Confirmed.Should().BeEmpty();
        vm.Mode.Should().Be(PersonsPageMode.Group, "Herbert bleibt in der Gruppe und kann abwählen");

        dialogs.OutlierDecision = OutlierDecision.ConfirmAll;
        await vm.NameGroupCommand.ExecuteAsync(null);
        service.Confirmed.Should().ContainSingle().Which.Faces.Should().HaveCount(4);
    }

    [Fact]
    public async Task Vorhandener_Name_fragt_und_fuegt_zur_Person_hinzu()
    {
        var service = new FakeRecognitionService { Groups = new FaceGroupsResult([FakeRecognitionService.Group(1, 3, 0)], 3, 1) };
        service.Persons.Add(new PersonOverview(new PersonSummary(7, "Anna", 4, 4, null, false), 0));
        var dialogs = new FakePersonsDialogs { NextName = "anna" };
        var vm = Create(service, dialogs);
        await vm.LoadOverviewAsync();
        vm.OpenGroupCommand.Execute(vm.Groups[0]);

        await vm.NameGroupCommand.ExecuteAsync(null);

        dialogs.Confirmations.Should().ContainSingle(m => m.Contains("Es gibt schon eine Person"));
        service.Created.Should().BeEmpty();
        service.Checks.Single().PersonId.Should().Be(7, "Prüfung gegen die vorhandene Person");
        service.Confirmed.Single().PersonId.Should().Be(7);
    }

    [Fact]
    public async Task Zu_Person_hinzufuegen_schlaegt_die_aehnliche_Person_vor()
    {
        var anna = new PersonSummary(7, "Anna", 4, 4, null, false);
        var service = new FakeRecognitionService { Groups = new FaceGroupsResult([FakeRecognitionService.Group(1, 3, 1, 7, "Anna")], 4, 1) };
        service.Persons.Add(new PersonOverview(anna, 0));
        var dialogs = new FakePersonsDialogs { NextPick = anna };
        var vm = Create(service, dialogs);
        await vm.LoadOverviewAsync();
        vm.Groups[0].Hint.Should().Be("ähnelt Anna?");
        vm.OpenGroupCommand.Execute(vm.Groups[0]);
        vm.GroupHint.Should().Contain("Ähnelt „Anna“");

        await vm.AddGroupToPersonCommand.ExecuteAsync(null);

        dialogs.Picks.Single().Suggested.Should().Be(7);
        service.Checks.Single().PersonId.Should().Be(7);
        dialogs.OutlierQuestions.Should().BeEmpty("keine Ausreißer in der Auswahl → keine Rückfrage");
        service.Confirmed.Single().Faces.Should().BeEquivalentTo(new long[] { 1001, 1002, 1003 });
    }

    [Fact]
    public async Task Ignorieren_der_Ausreisser_laesst_den_Rest_der_Gruppe_offen()
    {
        var service = new FakeRecognitionService { Groups = new FaceGroupsResult([FakeRecognitionService.Group(1, 4, 2)], 6, 1) };
        var vm = Create(service, new FakePersonsDialogs());
        await vm.LoadOverviewAsync();
        vm.OpenGroupCommand.Execute(vm.Groups[0]);
        vm.SelectNoneGroupCommand.Execute(null);
        vm.ToggleTileCommand.Execute(vm.GroupMembers[4]);
        vm.ToggleTileCommand.Execute(vm.GroupMembers[5]);

        await vm.IgnoreGroupSelectionCommand.ExecuteAsync(null);

        service.Ignored.Single().Should().BeEquivalentTo(new long[] { 1005, 1006 });
        vm.Mode.Should().Be(PersonsPageMode.Group);
        vm.GroupMembers.Should().HaveCount(4).And.OnlyContain(t => t.IsSelected && !t.IsOutlier);
    }

    [Fact]
    public async Task Vorschlaege_einzeln_und_als_Auswahl_bestaetigen_und_ablehnen()
    {
        var anna = new PersonSummary(7, "Anna", 2, 2, 1, false);
        var service = new FakeRecognitionService();
        service.Persons.Add(new PersonOverview(anna, 3));
        service.PersonFaces[7] = new PersonFaces(anna,
            [FakeRecognitionService.Face(1, FaceState.Confirmed), FakeRecognitionService.Face(2, FaceState.Confirmed)],
            [FakeRecognitionService.Face(11, FaceState.Suggested, 0.9f), FakeRecognitionService.Face(12, FaceState.Suggested, 0.8f), FakeRecognitionService.Face(13, FaceState.Suggested, 0.5f)]);
        service.Outliers.Add(13);
        var dialogs = new FakePersonsDialogs { OutlierDecision = OutlierDecision.SkipOutliers };
        var vm = Create(service, dialogs);
        await vm.LoadOverviewAsync();
        await vm.OpenPersonCommand.ExecuteAsync(vm.Persons[0]);

        vm.Mode.Should().Be(PersonsPageMode.Person);
        vm.DetailTab.Should().Be(0, "Vorschläge zuerst, wenn es welche gibt");
        vm.Suggestions.Select(t => t.FaceId).Should().Equal(11, 12, 13);
        vm.Suggestions[0].ScoreText.Should().Be("90 %");
        vm.Confirmed.Should().HaveCount(2);

        await vm.ConfirmSuggestionCommand.ExecuteAsync(vm.Suggestions[0]);
        service.Confirmed.Single().Faces.Should().Equal(11);
        dialogs.OutlierQuestions.Should().BeEmpty();

        vm.ToggleTileCommand.Execute(vm.Suggestions[1]);
        vm.ToggleTileCommand.Execute(vm.Suggestions[2]);
        vm.ConfirmSelectedSuggestionsCommand.CanExecute(null).Should().BeTrue();
        await vm.ConfirmSelectedSuggestionsCommand.ExecuteAsync(null);
        dialogs.OutlierQuestions.Should().ContainSingle().Which.PersonName.Should().Be("Anna");
        service.Confirmed[^1].Faces.Should().Equal(12L);

        await vm.RejectSuggestionCommand.ExecuteAsync(vm.Suggestions[2]);
        service.Rejected.Single().Should().Equal(13L);

        // „alle sichtbaren“ = gemeldeter sichtbarer Bereich
        vm.ReportVisibleRange(PersonsListKind.Suggestions, 0, 1);
        await vm.RejectVisibleSuggestionsCommand.ExecuteAsync(null);
        service.Rejected[^1].Should().Equal(11L, 12L);

        // Bestätigte entfernen / Titelbild
        vm.ToggleTileCommand.Execute(vm.Confirmed[1]);
        vm.SetCoverCommand.CanExecute(null).Should().BeTrue();
        await vm.RemoveSelectedConfirmedCommand.ExecuteAsync(null);
        service.Removed.Single().Should().Equal(2L);
    }

    [Fact]
    public async Task Zusammenfuehren_fragt_nach_und_wechselt_zur_Zielperson()
    {
        var anna = new PersonSummary(7, "Anna", 2, 2, null, false);
        var dup = new PersonSummary(8, "Anna Handy", 1, 1, null, false);
        var service = new FakeRecognitionService();
        service.Persons.Add(new PersonOverview(anna, 0));
        service.Persons.Add(new PersonOverview(dup, 0));
        service.PersonFaces[7] = new PersonFaces(anna, [FakeRecognitionService.Face(1, FaceState.Confirmed)], []);
        service.PersonFaces[8] = new PersonFaces(dup, [FakeRecognitionService.Face(2, FaceState.Confirmed)], []);
        var dialogs = new FakePersonsDialogs { NextPick = anna };
        var vm = Create(service, dialogs);
        await vm.LoadPersonAsync(8, keepState: false);
        vm.DetailTab.Should().Be(1, "ohne Vorschläge gleich die bestätigten Gesichter");

        await vm.MergePersonCommand.ExecuteAsync(null);

        dialogs.Picks.Single().Excluded.Should().Be(8, "sich selbst kann man nicht wählen");
        dialogs.Confirmations.Should().ContainSingle(m => m.Contains("„Anna Handy“ wird gelöscht"));
        service.Merged.Should().Equal((8, 7));
        vm.CurrentPerson!.Id.Should().Be(7);
    }

    [Fact]
    public void Kopfzeile_zeigt_Indexstand_und_Hinweis_wenn_die_Erkennung_aus_ist()
    {
        PersonsViewModel.DescribeIndex(new FaceIndexStatus(FaceIndexState.Running, 1234, 19556)).Should().Be($"Analysiert: {1234:N0} von {19556:N0} Fotos · läuft");
        PersonsViewModel.DescribeIndex(new FaceIndexStatus(FaceIndexState.Paused, 5, 9)).Should().EndWith("pausiert");
        PersonsViewModel.DescribeIndex(new FaceIndexStatus(FaceIndexState.Idle, 9, 9)).Should().Be("Alle 9 Fotos analysiert.");
        PersonsViewModel.DescribeIndex(FaceIndexStatus.Disabled).Should().Be("Gesichtserkennung ausgeschaltet.");

        var index = new FakeIndexStatus(FaceIndexStatus.Disabled);
        var navigation = new RecordingNavigation();
        var vm = Create(new FakeRecognitionService(), new FakePersonsDialogs(), index, navigation);
        vm.IsRecognitionOff.Should().BeTrue();
        vm.RecognitionOffText.Should().Contain("in den Einstellungen einschalten");
        vm.OpenSettingsCommand.Execute(null);
        navigation.Tags.Should().Equal(NavigationTags.SettingsFaces);
        PersonsDialogService.DescribeOutliers("Anna", 2, 9).Should().Be("2 von 9 Gesichtern passen nicht gut zu „Anna“. Nur Bestätigtes wird gelernt — falsche Gesichter verschlechtern die Vorschläge.");
    }

    private static PersonsViewModel Create(
        FakeRecognitionService service,
        FakePersonsDialogs dialogs,
        IFaceIndexService? index = null,
        INavigationService? navigation = null)
        => new(
            service,
            index ?? new FakeIndexStatus(new FaceIndexStatus(FaceIndexState.Idle, 3, 3)),
            new FaceImageProvider(NullService.Create<IFaceCropService>()),
            dialogs,
            NullService.Create<ILightboxService>(),
            navigation ?? new RecordingNavigation(),
            NullLogger<PersonsViewModel>.Instance,
            new WeakReferenceMessenger());

    private sealed class FakeIndexStatus(FaceIndexStatus status) : IFaceIndexService
    {
        public FaceIndexStatus Status { get; } = status;

        public FaceIndexSettings Settings => FaceIndexSettings.Default;

        public bool IsPaused => false;

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task SetEnabledAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;

        public Task SetUseGpuAsync(bool useGpu, CancellationToken ct = default) => Task.CompletedTask;

        public void Pause()
        {
        }

        public void Resume()
        {
        }

        public void CancelRun()
        {
        }

        public void RequestRun()
        {
        }

        public Task<int> RebuildAsync(CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class RecordingNavigation : INavigationService
    {
        public List<string> Tags { get; } = [];

        public string? CurrentTag => Tags.LastOrDefault();

        public event EventHandler<string>? Navigated
        {
            add { }
            remove { }
        }

        public void NavigateTo(string tag) => Tags.Add(tag);
    }
}
