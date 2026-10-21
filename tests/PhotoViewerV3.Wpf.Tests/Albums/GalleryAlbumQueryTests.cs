using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoViewerV3.Albums;
using PhotoViewerV3.Domain.Entities;
using PhotoViewerV3.Domain.Enums;
using PhotoViewerV3.Gallery.Data;
using PhotoViewerV3.Services.Albums;
using PhotoViewerV3.Services.Database;
using PhotoViewerV3.Services.Library;

namespace PhotoViewerV3.Wpf.Tests.Albums;

/// <summary>
/// Alben und Ordner-Verknüpfungen in der Galerie-SQL (ADR-0054) gegen eine echte, migrierte DB. Bibliothek T:
/// C:\Fotos\Urlaub (u1, u2), C:\Fotos\Urlaub\Strand (s1), C:\Fotos\Familie (f1, f2 = Video); f1 ist nach „Urlaub“,
/// f2 nach „Strand“ verknüpft. Bibliothek U: C:\Andere (x1). Ordneransicht echt + verknüpft, „nur echte Dateien“, Zähler,
/// Ketten-Kennzeichen, Filter „Album“, intelligentes Album = derselbe Filter in der Galerie, Bereich.
/// </summary>
public sealed class GalleryAlbumQueryTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PhotoViewerV3.Wpf.Tests", Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, int> _ids = new();
    private readonly Dictionary<string, int> _folders = new();
    private LibraryContextFactory _factory = null!;
    private GalleryPageSource _source = null!;
    private int _lib;
    private int _other;

    public async Task InitializeAsync()
    {
        var paths = new TempPaths(_root);
        paths.EnsureDirectoriesExist();
        _factory = new LibraryContextFactory(paths, new SqlitePragmaInterceptor());
        await new DatabaseInitializer(_factory, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();

        await using (var ctx = _factory.Create())
        {
            var t = new Library { Name = "T", RootPath = @"C:\Fotos", CreatedAt = DateTime.UtcNow, LastOpenedAt = DateTime.UtcNow };
            var u = new Library { Name = "U", RootPath = @"C:\Andere", CreatedAt = DateTime.UtcNow, LastOpenedAt = DateTime.UtcNow.AddDays(-1) };
            ctx.Libraries.AddRange(t, u);
            await ctx.SaveChangesAsync();
            _lib = t.Id;
            _other = u.Id;
            foreach (var (key, libraryId, path) in new[]
                     {
                         ("root", t.Id, @"C:\Fotos"), ("urlaub", t.Id, @"C:\Fotos\Urlaub"), ("strand", t.Id, @"C:\Fotos\Urlaub\Strand"),
                         ("familie", t.Id, @"C:\Fotos\Familie"), ("andere", u.Id, @"C:\Andere"),
                     })
            {
                var folder = new Folder { LibraryId = libraryId, Path = path, Name = Path.GetFileName(path), ScannedAt = DateTime.UtcNow };
                ctx.Folders.Add(folder);
                await ctx.SaveChangesAsync();
                _folders[key] = folder.Id;
            }

            var minute = 0;
            foreach (var (name, folder, type) in new[]
                     {
                         ("u1", "urlaub", MediaType.Image), ("u2", "urlaub", MediaType.Image), ("s1", "strand", MediaType.Image),
                         ("f1", "familie", MediaType.Image), ("f2", "familie", MediaType.Video), ("x1", "andere", MediaType.Image),
                     })
            {
                var file = new MediaFile
                {
                    LibraryId = folder == "andere" ? u.Id : t.Id, FolderId = _folders[folder], Name = name + (type == MediaType.Video ? ".mp4" : ".jpg"),
                    Extension = type == MediaType.Video ? ".mp4" : ".jpg", Size = 1000, MediaType = type,
                    CreatedAtUtc = DateTime.UtcNow, ModifiedAtUtc = new DateTime(2024, 1, 1, 12, minute++, 0, DateTimeKind.Utc),
                };
                ctx.Files.Add(file);
                await ctx.SaveChangesAsync();
                _ids[name] = file.Id;
            }
        }

        using var connection = _factory.OpenSqliteConnection();
        Exec(connection, "INSERT INTO FilesFts(rowid, Name, Extension) SELECT Id, Name, Extension FROM Files;");
        Exec(connection, "INSERT INTO LibraryGroups (Id, Name, CreatedAtUtc) SELECT Id, Name, 0 FROM Libraries; UPDATE Libraries SET GroupId = Id;");
        Exec(connection, $"INSERT INTO FolderLinks (FolderId, FileId, AddedAtUtc) VALUES ({_folders["urlaub"]}, {_ids["f1"]}, 0), ({_folders["strand"]}, {_ids["f2"]}, 0);");
        _source = new GalleryPageSource(_factory);
        _source.Open();
    }

    public Task DisposeAsync()
    {
        _source.Dispose();
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
    public async Task Ordneransicht_zeigt_echte_und_verknuepfte_Dateien_mit_Zaehler_und_Kennzeichen()
    {
        var direct = Folder("urlaub", subfolders: false);
        (await NamesAsync(direct)).Should().Equal("f1.jpg", "u1.jpg", "u2.jpg");
        (await _source.GetTotalCountAsync(direct, CancellationToken.None)).Should().Be(3);
        (await _source.GetFolderLinkedCountAsync(direct, CancellationToken.None)).Should().Be(1);
        (await _source.GetLinkedHereAsync(direct, [_ids["f1"], _ids["u1"]], CancellationToken.None)).Should().BeEquivalentTo([_ids["f1"]]);

        var recursive = Folder("urlaub", subfolders: true);
        (await NamesAsync(recursive)).Should().Equal("f1.jpg", "f2.mp4", "s1.jpg", "u1.jpg", "u2.jpg");
        (await _source.GetFolderLinkedCountAsync(recursive, CancellationToken.None)).Should().Be(2, "Verknüpfung im Unterordner zählt mit");
        (await _source.GetLinkedHereAsync(recursive, [_ids["s1"], _ids["f2"]], CancellationToken.None)).Should().BeEquivalentTo([_ids["f2"]]);

        // „nur echte Dateien“
        (await NamesAsync(recursive with { IncludeFolderLinks = false })).Should().Equal("s1.jpg", "u1.jpg", "u2.jpg");
        (await _source.GetFolderLinkedCountAsync(recursive with { IncludeFolderLinks = false }, CancellationToken.None)).Should().Be(0);

        // Filter wirken auf verknüpfte Dateien wie auf echte
        var images = recursive with { Filter = new GalleryFilterState(GalleryMediaKind.Images) };
        (await NamesAsync(images)).Should().Equal("f1.jpg", "s1.jpg", "u1.jpg", "u2.jpg");
        (await _source.GetFolderLinkedCountAsync(images, CancellationToken.None)).Should().Be(1);

        // Original-Ordner: die Datei liegt dort echt, nicht als Verknüpfung
        var familie = Folder("familie", subfolders: false);
        (await NamesAsync(familie)).Should().Equal("f1.jpg", "f2.mp4");
        (await _source.GetFolderLinkedCountAsync(familie, CancellationToken.None)).Should().Be(0);
        (await _source.GetLinkedHereAsync(familie, [_ids["f1"]], CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task Ordner_ohne_Verknuepfungen_behaelt_den_Abfrageplan()
    {
        var plain = await _source.ExplainAsync(Folder("familie", subfolders: false), count: false, CancellationToken.None);
        plain.Should().NotContain(l => l.Contains("FolderLinks", StringComparison.Ordinal), "ohne Verknüpfungen bleibt die Abfrage unverändert");
        var linked = await _source.ExplainAsync(Folder("urlaub", subfolders: false), count: false, CancellationToken.None);
        linked.Should().Contain(l => l.Contains("FolderLinks", StringComparison.Ordinal));
        linked.Should().NotContain(l => l.StartsWith("SCAN f ", StringComparison.Ordinal) || l == "SCAN f", $"kein Durchlauf aller Dateien (Plan: {string.Join(" | ", linked)})");
    }

    [Fact]
    public async Task Filter_Album_zeigt_nur_Fotos_des_Albums_auch_mit_Ordner_und_Suche()
    {
        using var connection = _factory.OpenSqliteConnection();
        Exec(connection, $"INSERT INTO Albums (Id, GroupId, Name, Kind, SortOrder, CreatedAtUtc, UpdatedAtUtc) VALUES (7, {_lib}, 'Beste', 0, 0, 0, 0);");
        Exec(connection, $"INSERT INTO AlbumItems (AlbumId, FileId, AddedAtUtc) VALUES (7, {_ids["u2"]}, 0), (7, {_ids["f2"]}, 0), (7, {_ids["s1"]}, 0);");

        var album = Query() with { AlbumId = 7 };
        (await NamesAsync(album)).Should().Equal("f2.mp4", "s1.jpg", "u2.jpg");
        (await _source.GetTotalCountAsync(album, CancellationToken.None)).Should().Be(3);
        (await _source.GetUnfilteredCountAsync(album, CancellationToken.None)).Should().Be(5, "„von N Dateien“ bezieht sich auf die Bibliothek");
        (await NamesAsync(album with { FolderId = _folders["familie"], IncludeSubfolders = false })).Should().Equal("f2.mp4");
        (await NamesAsync(album with { SearchTerm = "u2" })).Should().Equal("u2.jpg");
        (await NamesAsync(album with { Filter = new GalleryFilterState(GalleryMediaKind.Videos) })).Should().Equal("f2.mp4");
        (await _source.GetFileIdsAsync(album, null, CancellationToken.None)).Should().Equal(_ids["f2"], _ids["s1"], _ids["u2"]);
    }

    [Fact]
    public async Task Intelligentes_Album_zeigt_genau_den_gespeicherten_Galerie_Filter_im_Bereich()
    {
        using var connection = _factory.OpenSqliteConnection();
        Exec(connection, $"INSERT INTO Tags (Id, Name, Color, SortOrder, CreatedAtUtc, GroupId) VALUES (1, 'Urlaub', '#E53935', 0, 0, {_lib}), (2, 'Urlaub', '#E53935', 0, 0, {_other});");
        Exec(connection, $"INSERT INTO FileTags (FileId, TagId) VALUES ({_ids["u1"]}, 1), ({_ids["s1"]}, 1), ({_ids["f2"]}, 1), ({_ids["x1"]}, 2);");
        Exec(connection, $"UPDATE Files SET Rating = 4 WHERE Id IN ({_ids["u1"]}, {_ids["f2"]}, {_ids["x1"]});");

        var filter = new SmartAlbumFilter { Tags = ["urlaub"], MinRating = 3 };
        var expected = Query() with { TagIds = [1], Filter = new GalleryFilterState(MinRating: 3) };
        using var evaluator = new GallerySmartAlbumEvaluator(_factory);
        var scope = LibraryScopeInfo.ForLibrary(_lib, _lib);
        var ids = await evaluator.GetFileIdsAsync(filter, scope);
        ids.Should().BeEquivalentTo(await _source.GetFileIdsAsync(expected, null, CancellationToken.None), "intelligentes Album = Galerie mit demselben Filter");
        ids.Should().Equal(_ids["f2"], _ids["u1"]);
        (await evaluator.SummarizeAsync(filter, scope)).Should().Be((2, (int?)_ids["f2"]));

        // Bild + Ordner mit Unterordnern, unbekannter Begriff trifft nichts, anderer Bereich nur dessen Dateien
        (await evaluator.GetFileIdsAsync(filter with { Media = "Images", FolderPath = @"C:\Fotos\Urlaub" }, scope)).Should().Equal(_ids["u1"]);
        (await evaluator.GetFileIdsAsync(new SmartAlbumFilter { Tags = ["Gibt es nicht"] }, scope)).Should().BeEmpty("ein gelöschter Begriff weitet das Album nie auf alles aus");
        (await evaluator.GetFileIdsAsync(filter, LibraryScopeInfo.ForLibrary(_other, _other))).Should().Equal(_ids["x1"]);
        (await evaluator.GetFileIdsAsync(filter, new LibraryScopeInfo(_lib, _lib, "T", [_lib, _other], [_lib, _other], true)))
            .Should().BeEquivalentTo([_ids["f2"], _ids["u1"]], "Tag-Namen gelten in der Gruppe der aktiven Bibliothek");
    }

    private GalleryQuery Query() => new(_lib, GalleryFilterState.None, null, Sort: GallerySort.NameAsc);

    private GalleryQuery Folder(string key, bool subfolders) => Query() with { FolderId = _folders[key], IncludeSubfolders = subfolders };

    private async Task<IReadOnlyList<string>> NamesAsync(GalleryQuery query)
        => (await _source.LoadPageAsync(query, 0, 50, 0, CancellationToken.None)).Select(r => r.Name).ToList();

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
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
