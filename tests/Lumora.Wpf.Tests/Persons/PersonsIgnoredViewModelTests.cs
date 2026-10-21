using System.Drawing;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Domain.Enums;
using Lumora.Faces;
using Lumora.Lightbox;
using Lumora.Persons;
using Lumora.Persons.ViewModels;
using Lumora.Services.Faces;
using Lumora.Services.Faces.Assignment;
using Lumora.Services.Faces.Crops;
using Lumora.Services.Faces.Indexing;
using Lumora.Services.Faces.Models;
using Lumora.Shell;
using Lumora.Wpf.Tests.Fixtures;

namespace Lumora.Wpf.Tests.Persons;

/// <summary>
/// ViewModels zu „Ignorierte Gesichter“ (ADR-0040): Ansicht laden/nachladen, „zu klein“-Markierung, Zurückholen,
/// ganze Gruppe ignorieren (Knopf und Kachel-Kontextmenü, Rückfrage mit Anzahl inkl. Ausreißer), „Alle Vorschläge
/// bestätigen/ablehnen…“ über die ganze Liste, Einstellung Mindestgröße.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class PersonsIgnoredViewModelTests
{
    [Fact]
    public async Task Ansicht_laedt_ignorierte_markiert_zu_kleine_und_holt_die_Auswahl_zurueck()
    {
        var ignore = new FakeIgnoreService();
        ignore.Faces.AddRange(Enumerable.Range(1, 5).Select(i => Ignored(i, auto: i > 3)));
        var vm = Create(new FakeRecognitionService(), new FakePersonsDialogs(), ignore);
        await vm.LoadOverviewAsync();
        vm.IgnoredButtonText.Should().Be("Ignorierte Gesichter (5)");
        vm.CanShowIgnored.Should().BeTrue();

        await vm.ShowIgnoredCommand.ExecuteAsync(null);

        vm.Mode.Should().Be(PersonsPageMode.Ignored);
        vm.IsIgnoredView.Should().BeTrue();
        vm.Ignored.Should().HaveCount(5);
        vm.Ignored.Where(t => t.IsAutoIgnored).Select(t => t.FaceId).Should().Equal(4L, 5L);
        vm.Ignored[3].ScoreText.Should().Be("18 px");
        vm.Ignored[3].AutomationName.Should().Contain("zu klein");
        vm.IgnoredSummary.Should().Contain("5 ignorierte Gesichter").And.Contain("davon 2 automatisch");
        vm.RestoreIgnoredCommand.CanExecute(null).Should().BeFalse("nichts ausgewählt");

        vm.ToggleTileCommand.Execute(vm.Ignored[0]);
        vm.ToggleTileCommand.Execute(vm.Ignored[4]);
        vm.SelectedIgnoredCount.Should().Be(2);
        await vm.RestoreIgnoredCommand.ExecuteAsync(null);

        ignore.Unignored.Should().ContainSingle().Which.Should().BeEquivalentTo(new long[] { 1, 5 });
        vm.Message.Should().Contain("2 Gesichter zurückgeholt");
        vm.Ignored.Select(t => t.FaceId).Should().Equal(2L, 3L, 4L);

        await vm.BackCommand.ExecuteAsync(null);
        vm.Ignored.Should().BeEmpty();
        vm.IgnoredButtonText.Should().Be("Ignorierte Gesichter (3)");
    }

    [Fact]
    public async Task Ansicht_laedt_seitenweise_nach_wenn_das_Ende_sichtbar_wird()
    {
        var ignore = new FakeIgnoreService();
        ignore.Faces.AddRange(Enumerable.Range(1, PersonsViewModel.IgnoredPageSize + 120).Select(i => Ignored(i, auto: false)));
        var vm = Create(new FakeRecognitionService(), new FakePersonsDialogs(), ignore);

        await vm.ShowIgnoredCommand.ExecuteAsync(null);
        vm.Ignored.Should().HaveCount(PersonsViewModel.IgnoredPageSize, "erste Seite");
        vm.ReportVisibleRange(PersonsListKind.Ignored, 0, 20);
        vm.IgnoredRangeText.Should().Be($"Zeigt 1–21 von {PersonsViewModel.IgnoredPageSize + 120:N0} ignorierten Gesichtern — nach unten scrollen für weitere.");
        vm.Ignored.Should().HaveCount(PersonsViewModel.IgnoredPageSize, "Anfang sichtbar — noch nichts nachladen");

        vm.ReportVisibleRange(PersonsListKind.Ignored, PersonsViewModel.IgnoredPageSize - 12, PersonsViewModel.IgnoredPageSize - 1);
        await WaitUntil(() => vm.Ignored.Count == PersonsViewModel.IgnoredPageSize + 120);
        vm.Ignored.Select(t => t.FaceId).Should().OnlyHaveUniqueItems();
        ignore.Requests.Should().Contain((PersonsViewModel.IgnoredPageSize, PersonsViewModel.IgnoredPageSize));
    }

    [Fact]
    public async Task Ganze_Gruppe_ignorieren_fragt_mit_Anzahl_samt_Ausreissern_und_schliesst_die_Gruppe()
    {
        var service = new FakeRecognitionService { Groups = new FaceGroupsResult([FakeRecognitionService.Group(1, 6, 2)], 8, 1) };
        var dialogs = new FakePersonsDialogs();
        var ignore = new FakeIgnoreService();
        var vm = Create(service, dialogs, ignore);
        await vm.LoadOverviewAsync();
        vm.OpenGroupCommand.Execute(vm.Groups[0]);
        vm.GroupSelectedCount.Should().Be(6, "Ausreißer abgewählt");
        vm.IgnoreWholeGroupCommand.CanExecute(null).Should().BeTrue();

        dialogs.ConfirmResult = false;
        await vm.IgnoreWholeGroupCommand.ExecuteAsync(null);
        ignore.IgnoredGroups.Should().BeEmpty("abgebrochen");

        dialogs.ConfirmResult = true;
        await vm.IgnoreWholeGroupCommand.ExecuteAsync(null);

        dialogs.Confirmations[^1].Should().Contain("Alle 8 Gesichter").And.Contain("2 als Ausreißer markierten");
        ignore.IgnoredGroups.Should().Equal(1L);
        vm.Mode.Should().Be(PersonsPageMode.Overview);
        vm.Message.Should().Contain("8 Gesichter ignoriert");
    }

    [Fact]
    public async Task Gruppe_per_Kontextmenue_der_Kachel_ignorieren()
    {
        var service = new FakeRecognitionService { Groups = new FaceGroupsResult([FakeRecognitionService.Group(1, 4, 0), FakeRecognitionService.Group(2, 3, 1)], 8, 1) };
        var dialogs = new FakePersonsDialogs();
        var ignore = new FakeIgnoreService { GroupResult = 0 };
        var vm = Create(service, dialogs, ignore);
        await vm.LoadOverviewAsync();

        await vm.IgnoreGroupTileCommand.ExecuteAsync(vm.Groups[1]);

        ignore.IgnoredGroups.Should().Equal(2L);
        dialogs.Confirmations.Should().ContainSingle().Which.Should().Contain("Alle 4 Gesichter").And.Contain("1 als Ausreißer");
        vm.MessageIsError.Should().BeTrue("die Gruppe gab es nicht mehr (0)");
        vm.Message.Should().Contain("geändert");
    }

    [Fact]
    public async Task Alle_Vorschlaege_bestaetigen_nimmt_die_ganze_Liste_mit_Rueckfrage_und_Ausreisser_Pruefung()
    {
        var service = new FakeRecognitionService();
        service.Persons.Add(new PersonOverview(new PersonSummary(7, "Anna", 1, 1, null, false), 30));
        service.PersonFaces[7] = new PersonFaces(service.Persons[0].Person, [],
            Enumerable.Range(100, 30).Select(i => FakeRecognitionService.Face(i, FaceState.Suggested, 0.8f)).ToList());
        service.Outliers.Add(129);
        var dialogs = new FakePersonsDialogs { OutlierDecision = OutlierDecision.SkipOutliers };
        var vm = Create(service, dialogs, new FakeIgnoreService());
        await vm.LoadPersonAsync(7, keepState: false);
        vm.ReportVisibleRange(PersonsListKind.Suggestions, 0, 13); // nur 14 auf dem Bildschirm
        vm.SuggestionsRangeText.Should().Be("Zeigt 1–14 von 30 Vorschlägen — nach unten scrollen für weitere.");

        await vm.ConfirmAllSuggestionsCommand.ExecuteAsync(null);

        dialogs.Confirmations.Should().ContainSingle().Which.Should().Contain("Alle 30 Vorschläge").And.Contain("Anna");
        service.Checks.Should().ContainSingle().Which.Faces.Should().HaveCount(30, "auch die nicht sichtbaren");
        dialogs.OutlierQuestions.Should().ContainSingle();
        service.Confirmed.Should().ContainSingle().Which.Faces.Should().HaveCount(29).And.NotContain(129L);

        // „Sichtbare bestätigen“ nimmt dagegen nur die 14
        await vm.ConfirmVisibleSuggestionsCommand.ExecuteAsync(null);
        service.Confirmed[^1].Faces.Should().HaveCount(14);

        dialogs.ConfirmResult = false;
        await vm.RejectAllSuggestionsCommand.ExecuteAsync(null);
        service.Rejected.Should().BeEmpty("abgebrochen");
        dialogs.ConfirmResult = true;
        await vm.RejectAllSuggestionsCommand.ExecuteAsync(null);
        service.Rejected.Should().ContainSingle().Which.Should().HaveCount(30);
        dialogs.Confirmations[^1].Should().Contain("Alle 30 Vorschläge für „Anna“ ablehnen");
    }

    [Fact]
    public async Task Einstellung_Mindestgroesse_laedt_und_speichert()
    {
        var ignore = new FakeIgnoreService { MinSize = 32 };
        var vm = new FaceSettingsViewModel(
            NullService.Create<IFaceIndexService>(),
            NullService.Create<IFaceModelProvider>(),
            NullService.Create<IDialogService>(),
            NullLogger<FaceSettingsViewModel>.Instance,
            new WeakReferenceMessenger(),
            ignore);
        vm.HasMinFaceSize.Should().BeTrue();
        vm.MinFaceSizeChoices.Select(c => c.Value).Should().Equal(FaceAutoIgnore.Choices);
        vm.MinFaceSizeChoices.Single(c => c.Value == 24).Label.Should().Be("kleiner als 24 Pixel (Standard)");
        vm.MinFaceSizeChoices[0].Label.Should().Be("Aus");

        await vm.LoadMinFaceSizeAsync();
        vm.SelectedMinFaceSize.Should().Be(32);
        ignore.SetCalls.Should().BeEmpty("Laden speichert nichts");

        vm.SelectedMinFaceSize = 0;
        await WaitUntil(() => ignore.SetCalls.Count == 1);
        ignore.SetCalls.Should().Equal(0);
        await WaitUntil(() => vm.MinFaceSizeMessage is not null);
        vm.MinFaceSizeMessage.Should().Contain("zurückgeholt");

        var without = new FaceSettingsViewModel(
            NullService.Create<IFaceIndexService>(), NullService.Create<IFaceModelProvider>(), NullService.Create<IDialogService>(),
            NullLogger<FaceSettingsViewModel>.Instance, new WeakReferenceMessenger());
        without.HasMinFaceSize.Should().BeFalse("ohne Dienst kein Abschnitt");
    }

    private static IgnoredFaceInfo Ignored(long id, bool auto)
        => new(id, (int)(10_000 - id), new RectangleF(0.3f, 0.2f, 0.1f, 0.1f), auto ? 18 : 80, auto);

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        condition().Should().BeTrue();
    }

    internal static PersonsViewModel Create(FakeRecognitionService service, FakePersonsDialogs dialogs, IFaceIgnoreService ignore)
        => new(
            service,
            NullService.Create<IFaceIndexService>(),
            new FaceImageProvider(NullService.Create<IFaceCropService>()),
            dialogs,
            NullService.Create<ILightboxService>(),
            NullService.Create<INavigationService>(),
            NullLogger<PersonsViewModel>.Instance,
            new WeakReferenceMessenger(),
            ignore);
}

/// <summary>Attrappe für <see cref="IFaceIgnoreService"/>: Liste ignorierter Gesichter, merkt sich Aufrufe.</summary>
internal sealed class FakeIgnoreService : IFaceIgnoreService
{
    public List<IgnoredFaceInfo> Faces { get; } = [];

    public List<IReadOnlyCollection<long>> Unignored { get; } = [];

    public List<long> IgnoredGroups { get; } = [];

    public List<(int Offset, int Limit)> Requests { get; } = [];

    public List<int> SetCalls { get; } = [];

    public int MinSize { get; set; } = FaceAutoIgnore.DefaultMinSize;

    /// <summary>Rückgabe von <see cref="IgnoreGroupAsync"/>; null = Anzahl der Gruppenmitglieder ist unbekannt → 8.</summary>
    public int? GroupResult { get; set; }

    public Task<IgnoredFacesPage> GetIgnoredFacesAsync(int offset, int limit, CancellationToken ct = default)
    {
        Requests.Add((offset, limit));
        return Task.FromResult(new IgnoredFacesPage(Faces.Skip(offset).Take(limit).ToList(), Faces.Count, Faces.Count(f => f.IsAutomatic)));
    }

    public Task<int> UnignoreFacesAsync(IReadOnlyCollection<long> faceIds, CancellationToken ct = default)
    {
        Unignored.Add(faceIds.ToList());
        var count = Faces.RemoveAll(f => faceIds.Contains(f.FaceId));
        return Task.FromResult(count);
    }

    public Task<int> IgnoreGroupAsync(long groupId, CancellationToken ct = default)
    {
        IgnoredGroups.Add(groupId);
        return Task.FromResult(GroupResult ?? 8);
    }

    public Task<int> GetMinFaceSizeAsync(CancellationToken ct = default) => Task.FromResult(MinSize);

    public Task SetMinFaceSizeAsync(int pixels, CancellationToken ct = default)
    {
        SetCalls.Add(pixels);
        MinSize = pixels;
        return Task.CompletedTask;
    }
}
