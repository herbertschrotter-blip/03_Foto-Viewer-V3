using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoViewerV3.Domain.Entities;
using PhotoViewerV3.Domain.Enums;
using PhotoViewerV3.Lightbox;
using PhotoViewerV3.Persons;
using PhotoViewerV3.Persons.Dialogs;
using PhotoViewerV3.Persons.ViewModels;
using PhotoViewerV3.Services.Faces.Assignment;
using PhotoViewerV3.Services.Faces.Crops;
using PhotoViewerV3.Services.Faces.Indexing;
using PhotoViewerV3.Settings.PersonRelations;
using PhotoViewerV3.Shell;
using PhotoViewerV3.Wpf.Tests.Fixtures;

namespace PhotoViewerV3.Wpf.Tests.Persons;

/// <summary>
/// Personen-Details (0.2.47, ADR-0045) ohne Fenster: Dialog-Logik (Anzeigename, Hinweise, Prüfung, Beziehung in
/// vorhandener Schreibweise), Benennen und Bearbeiten auf der Personen-Seite, Sortieren/Gruppieren/Suchen in der
/// Übersicht, Detailzeile mit Alter, Einstellungen „Personen &amp; Beziehungen“.
/// </summary>
public sealed class PersonDetailsViewModelTests
{
    private static PersonEditRequest Request(PersonDetailsInput? initial = null, string? me = null)
        => new("T", "M", "OK", initial, ["Schwester", "Cousin mütterlicherseits"], ["Anna Muster", "Opa Franz"],
            PersonsViewModel.ExistingPersonHintForGroup, me);

    [Fact]
    public void Dialog_zeigt_Anzeigenamen_und_prueft_die_Eingaben()
    {
        var editor = new PersonDetailsEditor(Request(), currentYear: 2026);
        editor.CanConfirm.Should().BeFalse("ohne Namen");
        editor.Error.Should().Contain("Vornamen");

        editor.FirstName = " Anna ";
        editor.LastName = "muster";
        editor.DisplayName.Should().Be("Anna muster");
        editor.ExistingText.Should().Be("Es gibt schon eine Person „Anna Muster“ — die Gesichter werden ihr hinzugefügt.");
        editor.Nickname = "Annerl";
        editor.DisplayNameText.Should().Be("Anzeigename: Annerl");
        editor.ExistingText.Should().BeNull();

        editor.BirthDay = 12;
        editor.Error.Should().Contain("Jahr", "Tag ohne Jahr");
        editor.BirthYear = "19x0";
        editor.Error.Should().Contain("Zahl");
        editor.BirthYear = "1980";
        editor.Error.Should().Contain("Monat", "Tag ohne Monat");
        editor.BirthMonth = 3;
        editor.CanConfirm.Should().BeTrue();
        editor.AgeText.Should().StartWith("Alter heute: ");

        editor.Relation = "SCHWESTER ";
        editor.IsMe = true;
        editor.MeText.Should().BeNull("bisher niemand markiert");
        var input = editor.BuildInput()!;
        input.Should().Be(new PersonDetailsInput("Anna", "muster", "Annerl", "Schwester", PersonBirthDate.Create(1980, 3, 12, 2026), true),
            "getrimmt, Beziehung in der vorhandenen Schreibweise");

        var other = new PersonDetailsEditor(Request(me: "Herbert"), currentYear: 2026) { IsMe = true };
        other.MeText.Should().Contain("„Herbert“");
    }

    [Fact]
    public void Dialog_mit_Vorbelegung_und_nur_Jahr()
    {
        var initial = new PersonDetailsInput("Franz", "Huber", "Opa Franz", "Großvater", PersonBirthDate.Create(1931, null, null, 2026));
        var editor = new PersonDetailsEditor(Request(initial), currentYear: 2026);
        (editor.FirstName, editor.LastName, editor.Nickname, editor.Relation, editor.BirthYear, editor.BirthMonth, editor.BirthDay)
            .Should().Be(("Franz", "Huber", "Opa Franz", "Großvater", "1931", 0, 0));
        editor.ExistingText.Should().NotBeNull("„Opa Franz“ steht in der Liste der anderen Namen");
        editor.BuildInput().Should().Be(initial);
        PersonDetailsEditor.Months[3].Label.Should().Be("März");
        PersonDetailsEditor.Days.Should().HaveCount(32);
    }

    [Fact]
    public async Task Gruppe_benennen_legt_Person_mit_allen_Details_an()
    {
        var service = new FakeRecognitionService { Groups = new FaceGroupsResult([FakeRecognitionService.Group(1, 3, 0)], 3, 1) };
        service.Persons.Add(new PersonOverview(new PersonSummary(7, "Herbert", 1, 1, null, false)
        {
            Details = new PersonDetails(7, "Herbert", "", null, null, null, null, true),
        }, 0));
        var details = new FakePersonDetailsService(service) { Relations = { new PersonRelationInfo(1, "Schwester", 1) } };
        var input = new PersonDetailsInput("Anna", "Muster", null, "Schwester", PersonBirthDate.Create(1975, null, null));
        var dialogs = new FakePersonsDialogs { NextInput = input };
        var vm = Create(service, dialogs, details);
        await vm.LoadOverviewAsync();
        vm.OpenGroupCommand.Execute(vm.Groups[0]);

        await vm.NameGroupCommand.ExecuteAsync(null);

        var request = dialogs.EditRequests.Single();
        request.Relations.Should().Equal("Schwester");
        request.ExistingNames.Should().Contain("Herbert");
        request.CurrentMeName.Should().Be("Herbert");
        details.Created.Should().Equal(input);
        service.Created.Should().BeEmpty("ohne den Einzelnamen-Weg");
        service.Confirmed.Single().PersonId.Should().Be(100);
        vm.Message.Should().Contain("Anna Muster");
    }

    [Fact]
    public async Task Bearbeiten_speichert_Details_und_zeigt_sie_in_der_Detailansicht()
    {
        var anna = new PersonSummary(7, "Anna Muster", 3, 2, null, false)
        {
            Details = new PersonDetails(7, "Anna", "Muster", null, 1, "Schwester", PersonBirthDate.Create(1975, 3, null), false),
        };
        var service = new FakeRecognitionService();
        service.Persons.Add(new PersonOverview(anna, 0));
        service.PersonFaces[7] = new PersonFaces(anna, [FakeRecognitionService.Face(1, FaceState.Confirmed)], []);
        var details = new FakePersonDetailsService(service);
        var dialogs = new FakePersonsDialogs { NextInput = new PersonDetailsInput("Anna", "Muster", "Annerl", "Schwester", PersonBirthDate.Create(1975, 3, null)) };
        var vm = Create(service, dialogs, details);
        await vm.LoadPersonAsync(7, keepState: false);

        vm.PersonInfoText.Should().StartWith("Schwester · geboren März 1975 (");
        PersonsViewModel.DescribeDetails(anna.Details! with { Nickname = "Annerl", IsMe = true }, new DateOnly(2026, 10, 21))
            .Should().Be("Anna Muster · Schwester · geboren März 1975 (51 Jahre) · Das bin ich");

        await vm.EditPersonCommand.ExecuteAsync(null);

        dialogs.EditRequests.Single().Initial.Should().Be(anna.Details!.ToInput(), "vorbelegt mit den gespeicherten Details");
        dialogs.EditRequests.Single().ExistingNames.Should().NotContain("Anna Muster", "die bearbeitete Person zählt nicht als vergeben");
        details.Updated.Should().ContainSingle().Which.Should().Be((7, dialogs.NextInput!));
        service.Merged.Should().BeEmpty();
    }

    [Fact]
    public async Task Bearbeiten_auf_vergebenen_Anzeigenamen_fragt_nach_Zusammenfuehren()
    {
        var anna = new PersonSummary(7, "Anna", 3, 2, null, false);
        var dup = new PersonSummary(8, "Anni", 1, 1, null, false);
        var service = new FakeRecognitionService();
        service.Persons.Add(new PersonOverview(anna, 0));
        service.Persons.Add(new PersonOverview(dup, 0));
        service.PersonFaces[8] = new PersonFaces(dup, [FakeRecognitionService.Face(2, FaceState.Confirmed)], []);
        service.PersonFaces[7] = new PersonFaces(anna, [FakeRecognitionService.Face(1, FaceState.Confirmed)], []);
        var details = new FakePersonDetailsService(service);
        var dialogs = new FakePersonsDialogs { NextName = "anna" };
        var vm = Create(service, dialogs, details);
        await vm.LoadPersonAsync(8, keepState: false);

        await vm.EditPersonCommand.ExecuteAsync(null);

        dialogs.Confirmations.Should().ContainSingle(m => m.Contains("Beide zusammenführen"));
        service.Merged.Should().Equal((8, 7));
        details.Updated.Should().ContainSingle().Which.PersonId.Should().Be(7, "die eingegebenen Angaben gehen beim Zusammenführen nicht verloren");
    }

    [Fact]
    public async Task Uebersicht_sortiert_sucht_und_gruppiert_nach_Beziehung()
    {
        var service = new FakeRecognitionService();
        void Add(int id, string first, string last, string? nick, string? relation, int files)
        {
            var d = new PersonDetails(id, first, last, nick, relation is null ? null : relation.Length, relation, null, false);
            service.Persons.Add(new PersonOverview(new PersonSummary(id, d.DisplayName, files, files, null, false) { Details = d }, 0));
        }

        Add(1, "Anna", "Zeller", null, "Schwester", 5);
        Add(2, "Bert", "Albrecht", null, null, 9);
        Add(3, "Franz", "Huber", "Opa Franz", "Großvater", 1);
        Add(4, "Clara", "Berger", null, "Schwester", 2);
        var vm = Create(service, new FakePersonsDialogs(), new FakePersonDetailsService(service));
        await vm.LoadOverviewAsync();

        vm.Persons.Select(p => p.Name).Should().Equal("Anna Zeller", "Bert Albrecht", "Clara Berger", "Opa Franz");
        vm.SelectedSort = PersonsViewModel.SortOptions.Single(o => o.Mode == PersonsSortMode.LastName);
        vm.Persons.Select(p => p.Name).Should().Equal("Bert Albrecht", "Clara Berger", "Opa Franz", "Anna Zeller");
        vm.SelectedSort = PersonsViewModel.SortOptions.Single(o => o.Mode == PersonsSortMode.PhotoCount);
        vm.Persons.Select(p => p.Name).Should().Equal("Bert Albrecht", "Anna Zeller", "Clara Berger", "Opa Franz");
        vm.Persons.First(p => p.PersonId == 1).Details.Should().StartWith("Schwester · 5 Fotos");

        vm.PersonSearch = "huber";
        vm.Persons.Select(p => p.Name).Should().Equal("Opa Franz");
        vm.PersonsSummary.Should().EndWith("1 gefunden");
        vm.PersonSearch = "schwest";
        vm.Persons.Select(p => p.PersonId).Should().BeEquivalentTo([1, 4], "Suche findet die Beziehung");
        vm.PersonSearch = null;

        vm.GroupByRelation = true;
        vm.RelationGroups.Select(g => g.Header).Should().Equal("Großvater · 1", "Schwester · 2", "Ohne Beziehung · 1");
        vm.RelationGroups[1].Persons.Select(p => p.Name).Should().Equal(["Anna Zeller", "Clara Berger"], "innerhalb wie sortiert (Anzahl)");
        vm.GroupByRelation = false;
        vm.RelationGroups.Should().BeEmpty();
    }

    [Fact]
    public async Task Einstellungen_Beziehung_umbenennen_auf_vorhandenen_Namen_fuehrt_zusammen_und_loeschen()
    {
        var details = new FakePersonDetailsService(new FakeRecognitionService())
        {
            Relations = { new PersonRelationInfo(1, "Schwester", 2), new PersonRelationInfo(2, "Schwster", 1), new PersonRelationInfo(3, "Taufpate", 0) },
        };
        var dialogs = new FakePersonDialogs(confirm: true, prompt: "schwester");
        using var vm = new PersonRelationsSettingsViewModel(details, dialogs, NullLogger<PersonRelationsSettingsViewModel>.Instance, new WeakReferenceMessenger());
        await vm.LoadAsync();
        vm.Relations.Select(r => r.CountText).Should().Equal("2 Personen", "1 Person", "0 Personen");
        vm.RenameCommand.CanExecute(null).Should().BeFalse("nichts gewählt");

        vm.SelectedRelation = vm.Relations[1];
        vm.MergeTargets.Select(r => r.Name).Should().Equal("Schwester", "Taufpate");
        await vm.RenameCommand.ExecuteAsync(null);

        details.Renamed.Should().Equal([(2, "schwester")]);
        details.Merged.Should().Equal([(2, 1)], "Name vergeben → nach Rückfrage zusammengeführt");
        dialogs.LastMessage.Should().Contain("zusammenführen");

        vm.SelectedRelation = vm.Relations.Single(r => r.Name == "Taufpate");
        await vm.DeleteCommand.ExecuteAsync(null);
        details.Deleted.Should().Equal(3);
        vm.Message.Should().Contain("gelöscht");
    }

    private static PersonsViewModel Create(FakeRecognitionService service, FakePersonsDialogs dialogs, IPersonDetailsService? details)
        => new(
            service,
            NullService.Create<IFaceIndexService>(),
            new FaceImageProvider(NullService.Create<IFaceCropService>()),
            dialogs,
            NullService.Create<ILightboxService>(),
            new PhotoViewerV3.Wpf.Tests.Shell.RecordingNavigationService(),
            NullLogger<PersonsViewModel>.Instance,
            new WeakReferenceMessenger(),
            details: details);
}

/// <summary>Details-Dienst-Attrappe: legt Personen im <see cref="FakeRecognitionService"/> an, merkt sich Aufrufe.</summary>
internal sealed class FakePersonDetailsService(FakeRecognitionService service) : IPersonDetailsService
{
    public List<PersonDetailsInput> Created { get; } = [];

    public List<(int PersonId, PersonDetailsInput Input)> Updated { get; } = [];

    public List<PersonRelationInfo> Relations { get; } = [];

    public List<(int Id, string Name)> Renamed { get; } = [];

    public List<(int Source, int Target)> Merged { get; } = [];

    public List<int> Deleted { get; } = [];

    public Task<PersonDetails?> GetPersonDetailsAsync(int personId, CancellationToken ct = default)
        => Task.FromResult(service.Persons.FirstOrDefault(p => p.Person.Id == personId)?.Person.Details);

    public Task<IReadOnlyList<PersonDetails>> GetAllPersonDetailsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<PersonDetails>>(service.Persons.Select(p => p.Person.Details).OfType<PersonDetails>().ToList());

    public Task<int> CreatePersonAsync(PersonDetailsInput input, CancellationToken ct = default)
    {
        Created.Add(input);
        var id = service.NextPersonId++;
        service.Persons.Add(new PersonOverview(new PersonSummary(id, input.DisplayName, 0, 0, null, false), 0));
        return Task.FromResult(id);
    }

    public Task UpdatePersonDetailsAsync(int personId, PersonDetailsInput input, CancellationToken ct = default)
    {
        Updated.Add((personId, input));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PersonRelationInfo>> GetRelationsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<PersonRelationInfo>>(Relations.ToList());

    public Task RenameRelationAsync(int relationId, string name, CancellationToken ct = default)
    {
        Renamed.Add((relationId, name));
        if (Relations.FirstOrDefault(r => r.Id != relationId && string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)) is { } taken)
        {
            throw new RelationNameTakenException(name, taken.Id);
        }

        return Task.CompletedTask;
    }

    public Task MergeRelationsAsync(int sourceId, int targetId, CancellationToken ct = default)
    {
        Merged.Add((sourceId, targetId));
        Relations.RemoveAll(r => r.Id == sourceId);
        return Task.CompletedTask;
    }

    public Task DeleteRelationAsync(int relationId, CancellationToken ct = default)
    {
        Deleted.Add(relationId);
        Relations.RemoveAll(r => r.Id == relationId);
        return Task.CompletedTask;
    }
}
