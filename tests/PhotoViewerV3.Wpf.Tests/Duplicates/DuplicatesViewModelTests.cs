using System.Windows.Media;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoViewerV3.Domain.Enums;
using PhotoViewerV3.Duplicates;
using PhotoViewerV3.Gallery.Services;
using PhotoViewerV3.Services.Duplicates;
using PhotoViewerV3.Services.Library;
using PhotoViewerV3.Shell;

namespace PhotoViewerV3.Wpf.Tests.Duplicates;

/// <summary>Fakes der Doppelten-Dienste für die ViewModel- und Seiten-Tests.</summary>
internal sealed class FakeDuplicates : IDuplicateService, IDuplicateCleanupService, IDuplicateSettingsStore, ILibraryScope, IGalleryDialogService, IDuplicateImageSource
{
    public List<DuplicateGroup> Groups { get; } = new();

    public List<DuplicateQuery> Queries { get; } = new();

    public List<(IReadOnlyList<DuplicateCleanupGroup> Groups, bool Transfer)> CleanUps { get; } = new();

    public List<int> Undos { get; } = new();

    public bool ConfirmAnswer { get; set; } = true;

    public List<string> Confirmations { get; } = new();

    public DuplicateSettings Settings { get; set; } = DuplicateSettings.Default;

    public DuplicateUndoInfo? Undoable { get; set; }

    public LibraryScopeInfo Scope { get; set; } = LibraryScopeInfo.ForLibrary(1, 1, "Test");

    public List<int> RequestedImages { get; } = new();

    public static DuplicateMember Member(int id, int width = 4000, bool inbox = false, long size = 1_000_000, string? name = null, string library = "Test")
        => new(id, 1, library, name ?? $"foto{id}.jpg", @"C:\Bilder\2024", MediaType.Image, size, width, width * 3 / 4,
            new DateTime(2024, 7, 12, 13, 30, 0, DateTimeKind.Utc), CaptureDateSource.Exif, 120, (byte)3, id % 2 == 0, 1, 0, 0, inbox,
            DateTime.UtcNow, null);

    public DuplicateGroup Group(DuplicateMode mode, params DuplicateMember[] members)
    {
        var ordered = KeeperRule.Order(members);
        var group = new DuplicateGroup($"G{members[0].FileId}", mode, ordered, ordered[0].FileId, KeeperRule.Reason(ordered[0], ordered[1]));
        Groups.Add(group);
        return group;
    }

    public Task<DuplicateResult> GetGroupsAsync(DuplicateQuery query, CancellationToken ct = default)
    {
        Queries.Add(query);
        return Task.FromResult(new DuplicateResult(Groups.Where(g => g.Mode == query.Mode).ToList(), 0, 3));
    }

    public Task<int> CountInboxAlreadyInLibraryAsync(IReadOnlyList<int> libraryIds, CancellationToken ct = default) => Task.FromResult(0);

    public Task<IReadOnlyList<int>> FindByContentAsync(IReadOnlyList<int> libraryIds, long size, byte[] sha256, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<int>>([]);

    public Task<DuplicateCleanupResult> CleanUpAsync(IReadOnlyList<DuplicateCleanupGroup> groups, bool transferMetadata, CancellationToken ct = default)
    {
        CleanUps.Add((groups, transferMetadata));
        var deleted = groups.SelectMany(g => g.DeleteFileIds).ToHashSet();
        foreach (var group in Groups.ToList())
        {
            if (group.Members.Any(m => deleted.Contains(m.FileId)))
            {
                Groups.Remove(group);
            }
        }

        Undoable = new DuplicateUndoInfo(77, deleted.Count, DateTime.UtcNow, null);
        return Task.FromResult(new DuplicateCleanupResult(77, deleted.Count, 0, 2, 1, []));
    }

    public Task<DuplicateUndoInfo?> GetUndoableAsync(CancellationToken ct = default) => Task.FromResult(Undoable);

    public Task<DuplicateUndoResult?> UndoAsync(int batchId, CancellationToken ct = default)
    {
        Undos.Add(batchId);
        Undoable = null;
        return Task.FromResult<DuplicateUndoResult?>(new DuplicateUndoResult(batchId, 2, 0, []));
    }

    public Task<DuplicateSettings> LoadAsync(CancellationToken ct = default) => Task.FromResult(Settings);

    public Task SaveAsync(DuplicateSettings settings, CancellationToken ct = default)
    {
        Settings = settings;
        return Task.CompletedTask;
    }

    public Task<LibraryScopeInfo> GetCurrentAsync(CancellationToken ct = default) => Task.FromResult(Scope);

    public Task<LibraryScopeInfo> GetForLibraryAsync(int libraryId, CancellationToken ct = default) => Task.FromResult(Scope);

    public Task<IReadOnlyList<LibraryGroupInfo>> GetGroupsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LibraryGroupInfo>>([]);

    public Task ShowAllLinkedAsync(int groupId, CancellationToken ct = default) => Task.CompletedTask;

    public Task RenameGroupAsync(int groupId, string name, CancellationToken ct = default) => Task.CompletedTask;

    public Task<bool> ConfirmAsync(string title, string message, string primaryButtonText, bool isDestructive = false)
    {
        Confirmations.Add(message);
        return Task.FromResult(ConfirmAnswer);
    }

    public Task ShowErrorsAsync(string title, string message, IReadOnlyList<string> details) => Task.CompletedTask;

    public event Action<IReadOnlyList<int>>? ImagesReady;

    public ImageSource? TryGet(int fileId) => null;

    public void Request(IEnumerable<DuplicateMember> members)
    {
        RequestedImages.AddRange(members.Select(m => m.FileId));
        ImagesReady?.Invoke([]);
    }

    public DuplicatesViewModel CreateViewModel(IMessenger? messenger = null)
        => new(this, this, this, this, this, this, NullLogger<DuplicatesViewModel>.Instance, messenger: messenger ?? new WeakReferenceMessenger());
}

/// <summary>Seite „Doppelte“: Laden, Behalten-Wahl, Bestätigung, Aufräumen mit/ohne Übertragen, Rückgängig, Filter, Einstellungen.</summary>
public sealed class DuplicatesViewModelTests
{
    [Fact]
    public async Task Laedt_Gruppen_mit_Vorschlag_und_Zusammenfassung()
    {
        var f = new FakeDuplicates();
        f.Group(DuplicateMode.Exact, FakeDuplicates.Member(1, inbox: true), FakeDuplicates.Member(2));
        f.Group(DuplicateMode.Exact, FakeDuplicates.Member(3), FakeDuplicates.Member(4), FakeDuplicates.Member(5));
        var vm = f.CreateViewModel();

        await vm.ActivateAsync();

        vm.Groups.Should().HaveCount(2);
        vm.SummaryText.Should().Be("2 Gruppen · 5 Dateien · 3 überzählig (2,9 MB)");
        var first = vm.Groups[0];
        first.Keeper!.FileId.Should().Be(2, "liegt in der Bibliothek");
        first.ReasonText.Should().Be("Vorschlag behalten: liegt schon in der Bibliothek");
        first.Members.Single(m => m.FileId == 1).ActionText.Should().Be("In den Papierkorb");
        first.Members.Single(m => m.FileId == 2).ActionText.Should().Be("Behalten (Vorschlag)");
        first.Members.Single(m => m.FileId == 1).LocationText.Should().Be("Eingang");
        vm.DeleteCount.Should().Be(3);
        vm.CleanUpText.Should().StartWith("Übrige in den Papierkorb (3 · ");
        vm.RuleText.Should().Contain("höhere Auflösung → Aufnahmedatum aus der Datei → liegt schon in der Bibliothek");
        f.Queries.Should().ContainSingle().Which.LibraryIds.Should().Equal(1);
    }

    [Fact]
    public async Task Behalten_waehlen_und_Gruppe_ausschliessen_aendern_was_geloescht_wird()
    {
        var f = new FakeDuplicates();
        f.Group(DuplicateMode.Exact, FakeDuplicates.Member(1, inbox: true), FakeDuplicates.Member(2));
        f.Group(DuplicateMode.Exact, FakeDuplicates.Member(3), FakeDuplicates.Member(4));
        var vm = f.CreateViewModel();
        await vm.ActivateAsync();

        var group = vm.Groups[0];
        group.Members.Single(m => m.FileId == 1).IsKeep = true;
        group.Keeper!.FileId.Should().Be(1);
        group.Members.Single(m => m.FileId == 2).IsKeep.Should().BeFalse("nur eine Datei je Gruppe bleibt");
        group.ReasonText.Should().StartWith("Eigene Wahl");
        group.Members.Single(m => m.FileId == 1).IsKeep = false;
        group.Keeper!.FileId.Should().Be(1, "Abwählen allein ändert nichts — eine andere wählen");
        vm.Groups[1].IsIncluded = false;
        vm.DeleteCount.Should().Be(1);

        await vm.CleanUpCommand.ExecuteAsync(null);

        f.CleanUps.Should().ContainSingle();
        var (groups, transfer) = f.CleanUps[0];
        transfer.Should().BeTrue("Standard: Angaben übertragen");
        groups.Should().ContainSingle().Which.Should().BeEquivalentTo(new DuplicateCleanupGroup(1, [2], true));
        f.Confirmations.Should().ContainSingle().Which.Should().Contain("1 Datei aus 1 Gruppe").And.Contain("übertragen");
        vm.StatusText.Should().Be("1 Datei im Papierkorb · 2 Angaben übertragen · 1 Personen nicht übertragbar (kein passendes Gesicht)");
        vm.UndoInfo.Should().NotBeNull();
        vm.Groups.Should().ContainSingle("aufgeräumte Gruppe ist weg");
    }

    [Fact]
    public async Task Ohne_Bestaetigung_wird_nichts_geloescht_und_Uebertragen_ist_abschaltbar()
    {
        var f = new FakeDuplicates { ConfirmAnswer = false };
        f.Group(DuplicateMode.Exact, FakeDuplicates.Member(1), FakeDuplicates.Member(2));
        var vm = f.CreateViewModel();
        await vm.ActivateAsync();

        await vm.CleanUpCommand.ExecuteAsync(null);
        f.CleanUps.Should().BeEmpty();

        f.ConfirmAnswer = true;
        vm.TransferMetadata = false;
        await vm.CleanUpCommand.ExecuteAsync(null);
        f.CleanUps.Should().ContainSingle().Which.Transfer.Should().BeFalse();
        f.Confirmations.Last().Should().Contain("nicht übertragen");
        await WaitAsync(() => !f.Settings.TransferMetadata); // Einstellung gemerkt (im Hintergrund gespeichert)
    }

    [Fact]
    public async Task Rueckgaengig_ruft_den_Dienst_und_laedt_neu()
    {
        var f = new FakeDuplicates { Undoable = new DuplicateUndoInfo(5, 2, DateTime.UtcNow, null) };
        var vm = f.CreateViewModel();
        await vm.ActivateAsync();
        vm.UndoText.Should().Be("Rückgängig (2 Dateien)");
        vm.UndoCommand.CanExecute(null).Should().BeTrue();

        await vm.UndoCommand.ExecuteAsync(null);

        f.Undos.Should().Equal(5);
        vm.StatusText.Should().Be("2 Dateien aus dem Papierkorb zurückgeholt");
        vm.UndoInfo.Should().BeNull();
        vm.UndoCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task Umschalter_Schwelle_und_Eingang_Filter_gehen_in_die_Abfrage()
    {
        var f = new FakeDuplicates { Settings = new DuplicateSettings(6, true) };
        f.Group(DuplicateMode.Similar, FakeDuplicates.Member(1), FakeDuplicates.Member(2, width: 1600) with { Distance = 4 });
        var vm = f.CreateViewModel();
        await vm.ActivateAsync();
        vm.Threshold.Should().Be(6, "aus den Einstellungen");

        vm.IsSimilarMode = true;
        await WaitAsync(() => f.Queries.Count >= 2);
        f.Queries.Last().Should().BeEquivalentTo(new DuplicateQuery([1], DuplicateMode.Similar, 6, false));
        vm.Groups.Should().ContainSingle();
        vm.Groups[0].HeaderText.Should().StartWith("2 Dateien · ähnlich");
        vm.Groups[0].Members[1].DistanceText.Should().Be("Abstand 4");
        vm.PendingText.Should().Be("Noch 3 Bild-Fingerabdrücke offen — das Ergebnis ist noch unvollständig.");

        vm.Threshold = 9;
        await WaitAsync(() => f.Queries.Last().MaxDistance == 9);
        await WaitAsync(() => f.Settings.SimilarThreshold == 9);

        vm.ShowInboxDuplicates();
        await WaitAsync(() => f.Queries.Last() is { InboxOnly: true, Mode: DuplicateMode.Exact });
        vm.IsExactMode.Should().BeTrue();
        vm.EmptyText.Should().Be("Keine Datei im Eingang liegt schon in der Bibliothek.");
    }

    [Fact]
    public async Task Alle_verknuepften_zeigen_die_Bibliothek_und_nach_Aenderungen_wird_neu_gelesen()
    {
        var messenger = new WeakReferenceMessenger();
        var f = new FakeDuplicates { Scope = new LibraryScopeInfo(1, 9, "Familie", [1, 2], [1, 2], true) };
        f.Group(DuplicateMode.Exact, FakeDuplicates.Member(1, library: "A"), FakeDuplicates.Member(2, library: "B"));
        var vm = f.CreateViewModel(messenger);
        await vm.ActivateAsync();

        vm.Groups[0].Members.Single(m => m.FileId == 1).LocationText.Should().Be("Bibliothek · A");
        f.Queries.Last().LibraryIds.Should().Equal(1, 2);

        messenger.Send(new DuplicatesChangedMessage([1]));
        await WaitAsync(() => f.Queries.Count == 2);
        vm.Deactivate();
        messenger.Send(new DuplicatesChangedMessage([1]));
        await Task.Delay(100);
        f.Queries.Should().HaveCount(2, "unsichtbare Seite liest erst beim nächsten Öffnen");
    }

    [Fact]
    public void Statusleisten_Texte()
    {
        StatusBarViewModel.DescribeInboxDuplicates(1).Should().Be("1 Datei im Eingang ist schon in der Bibliothek");
        StatusBarViewModel.DescribeInboxDuplicates(1234).Should().Be($"{1234:N0} Dateien im Eingang sind schon in der Bibliothek");
        StatusBarViewModel.DescribeDuplicateIndex(new DuplicateIndexStatus(true, "Prüfsummen", 12, 3400)).Should().Be($"Doppelte: Prüfsummen 12 / {3400:N0}");
        StatusBarViewModel.DescribeDuplicateIndex(DuplicateIndexStatus.Idle).Should().BeNull();
        StatusBarViewModel.DescribeDuplicateIndex(new DuplicateIndexStatus(false, "", 0, 0, "wartet auf den Katalog")).Should().Be("Doppelte: wartet auf den Katalog");
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        condition().Should().BeTrue();
    }
}
