using System.IO;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoViewerV3.Gallery.Data;
using PhotoViewerV3.Libraries;
using PhotoViewerV3.Libraries.Linking;
using PhotoViewerV3.Services.Database;
using PhotoViewerV3.Services.Library;
using PhotoViewerV3.Services.Scanning;
using PhotoViewerV3.Services.Tags;
using PhotoViewerV3.Shell;
using PhotoViewerV3.Wpf.Tests.Fixtures;
using PhotoViewerV3.Wpf.Tests.Shell;

namespace PhotoViewerV3.Wpf.Tests.Gallery;

/// <summary>
/// Bibliotheks-Bereich der Galerie (ADR-0048) gegen eine echte, migrierte DB mit zwei Bibliotheken: eine Bibliothek sieht
/// weder Tags noch Personen noch Treffer der anderen (Suche nach Tag-/Personennamen, Flyout-Listen); nach dem Verknüpfen
/// zeigt die Ansicht „Alle verknüpften“ beide — Zähler = Summe, Filter, Suche und Personen wirken über beide. Dazu der
/// Bibliotheks-Umschalter (Eintrag „Alle verknüpften (Name)“) und „Verknüpfen mit…“ in den Einstellungen.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class GalleryLibraryScopeTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PhotoViewerV3.Wpf.Tests", Guid.NewGuid().ToString("N"));
    private readonly WeakReferenceMessenger _messenger = new();
    private LibraryContextFactory _factory = null!;
    private LibraryService _libraries = null!;
    private LibraryScope _scope = null!;
    private LibraryLinkService _linking = null!;
    private TagService _tags = null!;
    private GalleryPageSource _source = null!;
    private int _a;
    private int _b;
    private readonly Dictionary<string, int> _ids = new();

    public async Task InitializeAsync()
    {
        var paths = new TempPaths(Path.Combine(_root, "appdata"));
        Directory.CreateDirectory(paths.AppDataRoot);
        _factory = new LibraryContextFactory(paths, new SqlitePragmaInterceptor());
        await new DatabaseInitializer(_factory, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();
        _libraries = new LibraryService(_factory, paths, NullLogger<LibraryService>.Instance, _messenger);
        _scope = new LibraryScope(_factory, new TestCatalogContext(), _messenger);
        _linking = new LibraryLinkService(_factory, new TestCatalogContext(), NullLogger<LibraryLinkService>.Instance, _messenger);
        _tags = new TagService(_factory, NullLogger<TagService>.Instance, _messenger, _scope);

        _a = await CreateLibraryWithFilesAsync("Familie", ["IMG_a1.jpg", "Haus_a2.jpg"]);
        _b = await CreateLibraryWithFilesAsync("Arbeit", ["IMG_b1.jpg", "Haus_b2.jpg", "IMG_b3.jpg"]);

        await _libraries.SetActiveAsync(_a);
        var urlaubA = await _tags.CreateAsync("Urlaub");
        await _tags.AssignAsync([_ids["IMG_a1.jpg"]], [urlaubA.Id]);
        InsertPersonWithFace(_a, "Anna Familie", _ids["IMG_a1.jpg"]);

        await _libraries.SetActiveAsync(_b);
        var urlaubB = await _tags.CreateAsync("Urlaub");
        var kunde = await _tags.CreateAsync("Kunde Meier");
        await _tags.AssignAsync([_ids["IMG_b1.jpg"]], [urlaubB.Id]);
        await _tags.AssignAsync([_ids["Haus_b2.jpg"]], [kunde.Id]);
        InsertPersonWithFace(_b, "Bert Arbeit", _ids["Haus_b2.jpg"]);
        using (var c = _factory.OpenSqliteConnection())
        {
            Exec(c, $"UPDATE Files SET IsFavorite = 1 WHERE Id IN ({_ids["IMG_a1.jpg"]}, {_ids["IMG_b3.jpg"]});");
        }

        await _libraries.SetActiveAsync(_a);
        _source = new GalleryPageSource(_factory);
        _source.Open();
    }

    public Task DisposeAsync()
    {
        _source.Dispose();
        _tags.Dispose();
        _linking.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task Eine_Bibliothek_sieht_keine_Treffer_Tags_oder_Personen_der_anderen()
    {
        (await Count(Query(_a))).Should().Be(2);
        (await Ids(Query(_a, search: "urlaub"))).Should().Equal(_ids["IMG_a1.jpg"]);
        (await Ids(Query(_a, search: "Kunde"))).Should().BeEmpty("das Tag „Kunde Meier“ gehört zu B");
        (await Ids(Query(_a, search: "Bert"))).Should().BeEmpty("die Person aus B wird in A nicht gefunden");
        (await _source.GetPersonCountsAsync(_a, CancellationToken.None)).Select(p => p.Name).Should().Equal("Anna Familie");
        (await _source.GetPersonCountsAsync(_b, CancellationToken.None)).Select(p => p.Name).Should().Equal("Bert Arbeit");
        (await _tags.GetAllAsync()).Select(t => t.Name).Should().Equal("Urlaub");
        (await _tags.GetFileCountsAsync()).Values.Sum().Should().Be(1);
    }

    [Fact]
    public async Task Alle_verknuepften_zeigt_beide_Zaehler_ist_Summe_und_Filter_wirken()
    {
        await _linking.LinkAsync(_a, _b);
        var group = (await _scope.GetForLibraryAsync(_a)).GroupId;
        await _scope.ShowAllLinkedAsync(group);
        var scope = await _scope.GetCurrentAsync();
        scope.IsAllLinked.Should().BeTrue();
        var all = Query(_a) with { LibraryIds = scope.LibraryIds };

        (await Count(all)).Should().Be((await Count(Query(_a))) + (await Count(Query(_b))), "Zähler = Summe der Bibliotheken");
        (await Count(all)).Should().Be(5);
        (await _source.GetUnfilteredCountAsync(all, CancellationToken.None)).Should().Be(5);
        (await Ids(all with { Filter = new GalleryFilterState(FavoritesOnly: true) })).Should().BeEquivalentTo([_ids["IMG_a1.jpg"], _ids["IMG_b3.jpg"]]);
        (await Ids(all with { SearchTerm = "urlaub" })).Should().BeEquivalentTo([_ids["IMG_a1.jpg"], _ids["IMG_b1.jpg"]], "gleichnamiges Tag nach dem Verknüpfen eins");
        var urlaub = (await _tags.GetAllAsync()).Single(t => t.Name == "Urlaub");
        (await Ids(all with { TagIds = [urlaub.Id] })).Should().HaveCount(2);
        (await _tags.GetFileCountsAsync())[urlaub.Id].Should().Be(2);
        (await Ids(all with { SearchTerm = "Bert" })).Should().Equal(_ids["Haus_b2.jpg"]);
        (await _source.GetPersonCountsAsync(scope.LibraryIds, CancellationToken.None)).Select(p => p.Name).Should().BeEquivalentTo(["Anna Familie", "Bert Arbeit"]);
        (await _source.HasFacesAsync(scope.LibraryIds, CancellationToken.None)).Should().BeTrue();
        var page = await _source.LoadPageAsync(all, 0, 10, 5, CancellationToken.None);
        page.Select(r => r.LibraryId).Distinct().Should().BeEquivalentTo([_a, _b], "jede Zeile kennt ihre Bibliothek (Vorschaubild-Cache)");
    }

    [Fact]
    public Task Umschalter_zeigt_Alle_verknuepften_und_Einstellungen_verknuepfen_und_loesen() => OnUiAsync(async () =>
    {
        var navigation = new RecordingNavigationService();
        var dialogs = new ScriptedDialogs { ConfirmAnswer = true };
        var switcher = new LibrarySwitcherViewModel(_libraries, navigation, dialogs, NullLogger<LibrarySwitcherViewModel>.Instance, _messenger, null, scope: _scope);
        await switcher.RefreshAsync();
        switcher.LinkedGroups.Should().BeEmpty("ohne Verknüpfung kein Eintrag „Alle verknüpften“");

        var linkDialogs = new ScriptedLinkDialogs(_linking);
        var settings = new LibraryViewModel(_libraries, NullService.Create<IScanService>(), dialogs, navigation,
            NullLogger<LibraryViewModel>.Instance, _messenger, null, _linking, _scope, linkDialogs);
        await settings.LoadCommand.ExecuteAsync(null);
        var familie = settings.Libraries.Single(l => l.Id == _a);
        familie.ShowLinking.Should().BeTrue();
        familie.CanLink.Should().BeTrue();
        familie.IsLinked.Should().BeFalse();
        settings.LinkCommand.CanExecute(familie).Should().BeTrue();

        await settings.LinkCommand.ExecuteAsync(familie);
        linkDialogs.LastViewModel!.SummaryText.Should().Contain("„Arbeit“ kommt in die Gruppe von „Familie“");
        linkDialogs.LastViewModel.Preview!.TagsMerged.Should().Be(1);
        settings.Libraries.Should().OnlyContain(l => l.IsLinked);
        settings.Libraries.Single(l => l.Id == _b).LinkText.Should().Contain("„Familie“");

        await switcher.RefreshAsync();
        var entry = switcher.LinkedGroups.Should().ContainSingle().Subject;
        entry.Header.Should().Be("Alle verknüpften (Familie)");
        entry.FileCount.Should().Be(5);
        await switcher.ShowAllLinkedCommand.ExecuteAsync(entry);
        switcher.IsAllLinked.Should().BeTrue();
        switcher.ActiveName.Should().Be("Alle verknüpften (Familie)");
        switcher.Libraries.Should().OnlyContain(l => !l.IsActive, "in der gemeinsamen Ansicht ist keine einzelne Bibliothek markiert");

        // Zurück zu einer einzelnen Bibliothek
        await switcher.SwitchToCommand.ExecuteAsync(switcher.Libraries.Single(l => l.Id == _b));
        switcher.IsAllLinked.Should().BeFalse();
        switcher.ActiveName.Should().Be("Arbeit");

        // Lösen mit Bestätigung
        await settings.LoadCommand.ExecuteAsync(null);
        await settings.UnlinkCommand.ExecuteAsync(settings.Libraries.Single(l => l.Id == _b));
        dialogs.Confirmations.Should().Contain("Verknüpfung lösen");
        settings.Libraries.Should().OnlyContain(l => !l.IsLinked);
        await switcher.RefreshAsync();
        switcher.LinkedGroups.Should().BeEmpty();
        (await _tags.GetAllAsync()).Select(t => t.Name).Should().BeEquivalentTo(["Urlaub", "Kunde Meier"], "B behält nach dem Lösen alle gemeinsamen Begriffe");
    });

    [Fact]
    public Task Versteckter_Katalog_blendet_Verknuepfen_aus() => OnUiAsync(async () =>
    {
        var hidden = new TestCatalogContext();
        hidden.SetHidden(true);
        var linking = new LibraryLinkService(_factory, hidden, NullLogger<LibraryLinkService>.Instance, _messenger);
        var settings = new LibraryViewModel(_libraries, NullService.Create<IScanService>(), new ScriptedDialogs(), new RecordingNavigationService(),
            NullLogger<LibraryViewModel>.Instance, _messenger, null, linking, _scope, new ScriptedLinkDialogs(linking));
        await settings.LoadCommand.ExecuteAsync(null);
        settings.IsLinkingAvailable.Should().BeFalse();
        settings.Libraries.Should().OnlyContain(l => !l.ShowLinking && !l.CanLink);
        settings.LinkCommand.CanExecute(settings.Libraries[0]).Should().BeFalse();
    });

    // ---- Hilfen ---------------------------------------------------------------------------------------------------

    private static Task OnUiAsync(Func<Task> scenario)
        => TestUiThread.Dispatcher.InvokeAsync(scenario).Task.Unwrap().WaitAsync(Timeout);

    private async Task<int> CreateLibraryWithFilesAsync(string name, string[] files)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        foreach (var file in files)
        {
            var bytes = new byte[64];
            bytes[0] = 0xFF;
            bytes[1] = 0xD8;
            bytes[2] = 0xFF;
            await File.WriteAllBytesAsync(Path.Combine(folder, file), bytes);
        }

        var library = await _libraries.CreateAsync(name, folder);
        using (var c = _factory.OpenSqliteConnection())
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Exec(c, $"INSERT INTO Folders (LibraryId, Path, ParentId, Name, ScannedAt) VALUES ({library.Id}, '{folder.Replace("'", "''")}', NULL, '{name}', {now});");
            var folderId = Scalar(c, "SELECT last_insert_rowid();");
            foreach (var file in files)
            {
                Exec(c, "INSERT INTO Files (LibraryId, FolderId, Name, Extension, Size, CreatedAtUtc, ModifiedAtUtc, MediaType, IsFavorite, Missing) " +
                        $"VALUES ({library.Id}, {folderId}, '{file}', '.jpg', 64, {now}, {now}, 0, 0, 0);");
                _ids[file] = (int)Scalar(c, "SELECT last_insert_rowid();");
            }

            Exec(c, "INSERT INTO FilesFts(rowid, Name, Extension) SELECT Id, Name, Extension FROM Files WHERE Id NOT IN (SELECT rowid FROM FilesFts);");
        }

        return library.Id;
    }

    /// <summary>Person der Gruppe der Bibliothek mit einem bestätigten Gesicht (wie der Zuordnungsdienst).</summary>
    private void InsertPersonWithFace(int libraryId, string name, int fileId)
    {
        using var c = _factory.OpenSqliteConnection();
        var (first, last) = PhotoViewerV3.Domain.Entities.PersonNames.Split(name);
        Exec(c, $"""
            INSERT INTO Persons (Name, CreatedAtUtc, CoverFaceId, IsHidden, FirstName, LastName, GroupId)
            VALUES ('{name}', 1, NULL, 0, '{first}', '{last}', (SELECT GroupId FROM Libraries WHERE Id = {libraryId}));
            """);
        var personId = Scalar(c, "SELECT last_insert_rowid();");
        Exec(c, $"""
            INSERT INTO Faces (FileId, BoxX, BoxY, BoxW, BoxH, Landmarks, DetScore, Size, Sharpness, Quality, Embedding,
                               ModelVersion, PersonId, State, SuggestedPersonId, SuggestionScore, CreatedAtUtc)
            VALUES ({fileId}, 0.1, 0.1, 0.2, 0.2, x'00', 0.9, 100, 10, 0.8, x'00', 'v1', {personId}, 2, NULL, NULL, 1);
            """);
    }

    private GalleryQuery Query(int libraryId, string? search = null)
        => new(libraryId, GalleryFilterState.None, search, Sort: GallerySort.NameAsc);

    private Task<int> Count(GalleryQuery query) => _source.GetTotalCountAsync(query, CancellationToken.None);

    private async Task<List<int>> Ids(GalleryQuery query)
    {
        var total = await Count(query);
        return (await _source.LoadPageAsync(query, 0, 100, total, CancellationToken.None)).Select(r => r.Id).ToList();
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>„Verknüpfen mit…“ ohne Fenster: wählt den ersten Kandidaten, wartet auf die Vorschau, übernimmt den Standard.</summary>
    private sealed class ScriptedLinkDialogs(ILibraryLinkService linking) : ILibraryLinkDialogService
    {
        public LibraryLinkDialogViewModel? LastViewModel { get; private set; }

        public async Task<LibraryLinkDialogResult?> ShowLinkAsync(LibraryLinkDialogRequest request)
        {
            var vm = new LibraryLinkDialogViewModel(linking, request);
            LastViewModel = vm;
            vm.CanConfirm.Should().BeFalse("ohne Wahl kein Verknüpfen");
            await vm.LoadPreviewAsync(request.Candidates[0]);
            vm.SelectedCandidate = request.Candidates[0];
            await vm.LoadPreviewAsync(request.Candidates[0]);
            return vm.BuildResult();
        }
    }

    private sealed class TempPaths(string root) : PhotoViewerV3.Services.Infrastructure.IAppDataPaths
    {
        public string AppDataRoot => root;

        public string LibraryDatabasePath => Path.Combine(root, "library.db");

        public string ThumbnailsRoot => Path.Combine(root, "thumbs");

        public string LogsRoot => Path.Combine(root, "logs");

        public string GetLibraryThumbnailsPath(long libraryId) => Path.Combine(ThumbnailsRoot, libraryId.ToString());

        public void EnsureDirectoriesExist() => Directory.CreateDirectory(root);
    }
}
