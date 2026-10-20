using System.IO;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoViewerV3.Domain.Enums;
using PhotoViewerV3.Gallery.Services;
using PhotoViewerV3.Services.Library;
using PhotoViewerV3.Services.Messaging;
using PhotoViewerV3.Sorter.Services;
using PhotoViewerV3.Sorter.ViewModels;
using PhotoViewerV3.Wpf.Tests.Fixtures;

namespace PhotoViewerV3.Wpf.Tests.Sorter;

/// <summary>Wurzel-Auswahl von Sortierer und Flatten (ADR-0020/ADR-0024): Vorbelegung, Synchronisation, Sperre, Neuladen.</summary>
[Collection(UiTestCollection.Name)]
public sealed class SorterRootSelectionTests
{
    private const string Archive = @"D:\Fotos";
    private const string InboxOffline = @"F:\Kamera";
    private const string InboxHandy = @"E:\Eingang\Handy";

    private static LibraryRootInfo Root(int id, string path, LibraryRootRole role, bool available = true, int libraryId = 1)
        => new(id, libraryId, path, role, Path.GetFileName(path), id, available);

    private static LibraryInfo Library(int id, params LibraryRootInfo[] roots)
        => new(id, "Bibliothek " + id, roots.First(r => r.IsArchive).Path, DateTime.UtcNow, DateTime.UtcNow, 0, 0, true, roots);

    private static LibraryInfo Standard() => Library(
        1,
        Root(1, Archive, LibraryRootRole.Archive),
        Root(2, InboxOffline, LibraryRootRole.Inbox, available: false),
        Root(3, InboxHandy, LibraryRootRole.Inbox));

    private static (SorterRootSelection Selection, FakeLibraryService Libraries, FakeFolderPicker Picker, WeakReferenceMessenger Messenger) Create(LibraryInfo? active)
    {
        var libraries = new FakeLibraryService { Active = active };
        var picker = new FakeFolderPicker();
        var messenger = new WeakReferenceMessenger();
        var selection = new SorterRootSelection(
            new SorterLibraryLocator(libraries, NullLogger<SorterLibraryLocator>.Instance),
            picker,
            messenger,
            NullLogger.Instance,
            "Quelle",
            "Ziel");
        return (selection, libraries, picker, messenger);
    }

    [Fact]
    public async Task Vorbelegung_Quelle_erster_erreichbarer_Eingang_Ziel_Archiv()
    {
        var (selection, _, _, _) = Create(Standard());

        var info = await selection.LoadAsync();

        info!.Id.Should().Be(1);
        selection.HasLibrary.Should().BeTrue();
        selection.SourceRoots.Select(r => r.RootId).Should().Equal(1, 2, 3);
        selection.TargetRoots[0].Should().BeSameAs(SorterRootOption.SourceFolderOption);
        selection.TargetRoots.Skip(1).Select(r => r.RootId).Should().Equal(1, 2, 3);
        selection.SourceRoots.Single(r => r.RootId == 2).IsAvailable.Should().BeFalse();
        selection.SourceRoots.Single(r => r.RootId == 2).Detail.Should().Contain("offline");

        selection.SourceFolder.Should().Be(InboxHandy, "der Offline-Eingang wird übersprungen");
        selection.SelectedSourceRoot!.RootId.Should().Be(3);
        selection.TargetRoot.Should().Be(Archive);
        selection.SelectedTargetRoot!.RootId.Should().Be(1);
        selection.TargetHint.Should().Be("Archiv-Wurzel „Fotos“");
        selection.TargetFolderDisplay.Should().Be(@"Fotos\");
        selection.IsTargetBlocked.Should().BeFalse();
    }

    [Fact]
    public async Task Ohne_Eingang_ist_die_Archiv_Wurzel_die_Quelle()
    {
        var (selection, _, _, _) = Create(Library(1, Root(1, Archive, LibraryRootRole.Archive)));

        await selection.LoadAsync();

        selection.SourceFolder.Should().Be(Archive);
        selection.TargetRoot.Should().Be(Archive);
    }

    [Fact]
    public async Task Ohne_Bibliothek_bleibt_alles_leer()
    {
        var (selection, _, _, _) = Create(null);

        (await selection.LoadAsync()).Should().BeNull();

        selection.HasLibrary.Should().BeFalse();
        selection.SourceRoots.Should().BeEmpty();
        selection.SourceFolder.Should().BeEmpty();
        selection.TargetRoot.Should().BeNull();
        selection.TargetRootDisplay.Should().Be("= Quellordner");
    }

    [Fact]
    public async Task Auswahl_und_Pfade_bleiben_synchron_und_Ziele_ausserhalb_oder_offline_sind_gesperrt()
    {
        var (selection, _, picker, _) = Create(Standard());
        await selection.LoadAsync();

        selection.SourceFolder = @"d:\fotos\2024\Urlaub";
        selection.SelectedSourceRoot!.RootId.Should().Be(1, "der Ordner liegt unter der Archiv-Wurzel");
        selection.SourceFolder.Should().Be(@"d:\fotos\2024\Urlaub", "das Synchronisieren der Auswahl überschreibt den Pfad nicht");

        selection.SelectedSourceRoot = selection.SourceRoots.Single(r => r.RootId == 3);
        selection.SourceFolder.Should().Be(InboxHandy);

        selection.SelectedTargetRoot = SorterRootOption.SourceFolderOption;
        selection.TargetRoot.Should().BeNull();
        selection.TargetRootDisplay.Should().Be("= Quellordner");
        selection.IsTargetBlocked.Should().BeFalse();

        selection.TargetRoot = Path.Combine(Archive, "2026");
        selection.SelectedTargetRoot!.RootId.Should().Be(1);
        selection.TargetHint.Should().Be("unter Archiv „Fotos“");
        selection.TargetFolderDisplay.Should().Be(@"Fotos\2026");

        selection.TargetRoot = @"C:\Temp\Export";
        selection.SelectedTargetRoot.Should().BeNull();
        selection.IsTargetBlocked.Should().BeTrue();
        selection.TargetHint.Should().Contain("Außerhalb der Bibliothek");
        selection.TargetFolderDisplay.Should().Be(@"C:\Temp\Export");

        selection.TargetRoot = Path.Combine(InboxOffline, "neu");
        selection.IsTargetBlocked.Should().BeTrue();
        selection.TargetHint.Should().Contain("offline");

        picker.Folder = Path.Combine(InboxHandy, "2025");
        selection.PickTargetRootCommand.Execute(null);
        selection.TargetRoot.Should().Be(Path.Combine(InboxHandy, "2025"));
        selection.SelectedTargetRoot!.RootId.Should().Be(3);

        picker.Folder = Path.Combine(Archive, "Alt");
        selection.PickSourceFolderCommand.Execute(null);
        selection.SourceFolder.Should().Be(Path.Combine(Archive, "Alt"));

        selection.IsEnabled = false;
        selection.PickSourceFolderCommand.CanExecute(null).Should().BeFalse();
        selection.PickTargetRootCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public Task Nachrichten_laden_neu_behalten_gueltige_Auswahl_und_melden_Aenderungen() => TestUiThread.Run(async () =>
    {
        var (selection, libraries, _, messenger) = Create(Library(1, Root(1, Archive, LibraryRootRole.Archive)));
        var events = new List<SorterRootsReloadedEventArgs>();
        selection.Reloaded += (_, e) => events.Add(e);

        messenger.Send(new LibraryRootsChangedMessage(1));
        events.Should().BeEmpty("vor dem ersten Laden (Seite nie geöffnet) wird nichts geladen");

        await selection.LoadAsync();
        selection.SourceFolder = Path.Combine(Archive, "2024");
        events.Clear();

        // Eingang hinzugefügt: Wurzeln geändert, die gültige Quelle bleibt.
        libraries.Active = Library(1, Root(1, Archive, LibraryRootRole.Archive), Root(3, InboxHandy, LibraryRootRole.Inbox));
        messenger.Send(new LibraryRootsChangedMessage(1));
        events.Should().ContainSingle().Which.Should().Match<SorterRootsReloadedEventArgs>(e => e.RootsChanged && !e.LibraryChanged);
        selection.SourceRoots.Should().HaveCount(2);
        selection.SourceFolder.Should().Be(Path.Combine(Archive, "2024"));

        // Andere Bibliothek in der Nachricht: ignoriert.
        messenger.Send(new LibraryRootsChangedMessage(99));
        events.Should().HaveCount(1);

        // Eingang entfernt, Quelle lag darin → zurück auf die Vorbelegung.
        selection.SourceFolder = InboxHandy;
        libraries.Active = Library(1, Root(1, Archive, LibraryRootRole.Archive));
        messenger.Send(new LibraryRootsChangedMessage(1));
        events.Should().HaveCount(2);
        selection.SourceFolder.Should().Be(Archive);

        // Bibliothekswechsel: alles neu vorbelegt.
        selection.TargetRoot = null;
        libraries.Active = Library(2, Root(7, @"G:\Archiv2", LibraryRootRole.Archive, libraryId: 2), Root(8, @"G:\Neu", LibraryRootRole.Inbox, libraryId: 2));
        messenger.Send(new ActiveLibraryChangedMessage(2));
        events.Should().HaveCount(3);
        events[^1].LibraryChanged.Should().BeTrue();
        selection.LibraryId.Should().Be(2);
        selection.SourceFolder.Should().Be(@"G:\Neu");
        selection.TargetRoot.Should().Be(@"G:\Archiv2");
    }, TimeSpan.FromSeconds(30));

    private sealed class FakeFolderPicker : IFolderPickerService
    {
        public string? Folder { get; set; }

        public string? PickFolder(string title, string? initialDirectory) => Folder;
    }

    private sealed class FakeLibraryService : ILibraryService
    {
        public LibraryInfo? Active { get; set; }

        public Task<LibraryInfo?> GetActiveAsync(CancellationToken ct = default) => Task.FromResult(Active);

        public Task<IReadOnlyList<LibraryInfo>> GetAllAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task<LibraryInfo?> GetAsync(int libraryId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<LibraryInfo> CreateAsync(string name, string rootPath, CancellationToken ct = default) => throw new NotSupportedException();

        public Task RenameAsync(int libraryId, string newName, CancellationToken ct = default) => throw new NotSupportedException();

        public Task SetActiveAsync(int libraryId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task ClearAsync(int libraryId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task DeleteThumbnailCacheAsync(int libraryId) => throw new NotSupportedException();

        public Task DeleteAsync(int libraryId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<LibraryRootInfo>> GetRootsAsync(int libraryId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<LibraryRootInfo> AddRootAsync(int libraryId, string path, LibraryRootRole role, string? name = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task RenameRootAsync(int rootId, string newName, CancellationToken ct = default) => throw new NotSupportedException();

        public Task SetRootRoleAsync(int rootId, LibraryRootRole role, CancellationToken ct = default) => throw new NotSupportedException();

        public Task RemoveRootAsync(int rootId, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
