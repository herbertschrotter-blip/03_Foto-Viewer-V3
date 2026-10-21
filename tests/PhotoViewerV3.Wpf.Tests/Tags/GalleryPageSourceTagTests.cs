using System.IO;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoViewerV3.Domain.Entities;
using PhotoViewerV3.Domain.Enums;
using PhotoViewerV3.Gallery.Data;
using PhotoViewerV3.Services.Database;
using PhotoViewerV3.Services.Infrastructure;
using PhotoViewerV3.Services.Tags;

namespace PhotoViewerV3.Wpf.Tests.Tags;

/// <summary>
/// Tag-Filter (ODER / UND / ohne Tag) und Suche nach Tag-Namen in der Galerie-SQL gegen eine echte, migrierte DB
/// (ADR-0021). Fünf Dateien: a,b,c mit „Urlaub“, a,b zusätzlich „Familie“, d ohne Tag, e mit „Arbeit“.
/// </summary>
public sealed class GalleryPageSourceTagTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PhotoViewerV3.Wpf.Tests", Guid.NewGuid().ToString("N"));
    private LibraryContextFactory _factory = null!;
    private GalleryPageSource _source = null!;
    private int _libraryId;
    private TagInfo _urlaub = null!;
    private TagInfo _familie = null!;
    private TagInfo _arbeit = null!;
    private readonly Dictionary<string, int> _ids = new();

    public async Task InitializeAsync()
    {
        var paths = new TempPaths(_root);
        _factory = new LibraryContextFactory(paths, new SqlitePragmaInterceptor());
        await new DatabaseInitializer(_factory, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();

        await using (var ctx = _factory.Create())
        {
            var library = new Library { Name = "T", RootPath = @"C:\Fotos", CreatedAt = DateTime.UtcNow };
            ctx.Libraries.Add(library);
            await ctx.SaveChangesAsync();
            _libraryId = library.Id;
            var folder = new Folder { LibraryId = library.Id, Path = @"C:\Fotos", Name = "Fotos", ScannedAt = DateTime.UtcNow };
            ctx.Folders.Add(folder);
            await ctx.SaveChangesAsync();
            foreach (var name in new[] { "a", "b", "c", "d", "e" })
            {
                var file = new MediaFile
                {
                    LibraryId = library.Id,
                    FolderId = folder.Id,
                    Name = $"IMG_{name}.jpg",
                    Extension = ".jpg",
                    Size = 1000,
                    CreatedAtUtc = DateTime.UtcNow,
                    ModifiedAtUtc = DateTime.UtcNow,
                    MediaType = MediaType.Image,
                };
                ctx.Files.Add(file);
                await ctx.SaveChangesAsync();
                _ids[name] = file.Id;
            }
        }

        using (var connection = _factory.OpenSqliteConnection())
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO FilesFts(rowid, Name, Extension) SELECT Id, Name, Extension FROM Files;";
            cmd.ExecuteNonQuery();
        }

        var tags = new TagService(_factory, NullLogger<TagService>.Instance, new WeakReferenceMessenger());
        _urlaub = await tags.CreateAsync("Urlaub 2024");
        _familie = await tags.CreateAsync("Familie");
        _arbeit = await tags.CreateAsync("Arbeit");
        await tags.AssignAsync([_ids["a"], _ids["b"], _ids["c"]], [_urlaub.Id]);
        await tags.AssignAsync([_ids["a"], _ids["b"]], [_familie.Id]);
        await tags.AssignAsync([_ids["e"]], [_arbeit.Id]);

        _source = new GalleryPageSource(_factory);
        _source.Open();
    }

    public Task DisposeAsync()
    {
        _source.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
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
    public async Task ODER_findet_Dateien_mit_mindestens_einem_Tag()
        => (await NamesAsync(Query(tagIds: [_familie.Id, _arbeit.Id]))).Should().BeEquivalentTo("IMG_a.jpg", "IMG_b.jpg", "IMG_e.jpg");

    [Fact]
    public async Task UND_findet_nur_Dateien_mit_allen_Tags()
    {
        (await NamesAsync(Query(tagIds: [_urlaub.Id, _familie.Id], mode: GalleryTagMode.All))).Should().BeEquivalentTo("IMG_a.jpg", "IMG_b.jpg");
        (await NamesAsync(Query(tagIds: [_urlaub.Id, _arbeit.Id], mode: GalleryTagMode.All))).Should().BeEmpty();
    }

    [Fact]
    public async Task Ohne_Tag_findet_nur_ungetaggte_Dateien()
        => (await NamesAsync(Query(tagIds: [_urlaub.Id], mode: GalleryTagMode.None))).Should().BeEquivalentTo("IMG_d.jpg");

    [Fact]
    public async Task Suche_findet_Tag_Namen_ohne_Gross_Kleinschreibung_und_Dateinamen()
    {
        (await NamesAsync(Query(search: "urlaub"))).Should().BeEquivalentTo("IMG_a.jpg", "IMG_b.jpg", "IMG_c.jpg");
        (await NamesAsync(Query(search: "ARB"))).Should().BeEquivalentTo("IMG_e.jpg");
        (await NamesAsync(Query(search: "IMG_d"))).Should().BeEquivalentTo("IMG_d.jpg");
    }

    [Fact]
    public async Task Suche_nach_Tag_Namen_folgt_Umbenennen_und_neuen_Zuweisungen()
    {
        using var tags = new TagService(_factory, NullLogger<TagService>.Instance, new WeakReferenceMessenger());
        await tags.RenameAsync(_arbeit.Id, "Baustelle");
        (await NamesAsync(Query(search: "arbeit"))).Should().BeEmpty("der alte Name steht nirgends mehr");
        await tags.AssignAsync([_ids["d"]], [_arbeit.Id]);
        (await NamesAsync(Query(search: "baustelle"))).Should().Equal("IMG_d.jpg", "IMG_e.jpg");
    }

    [Fact]
    public async Task Suche_nach_Tag_Namen_liest_Zuordnungen_ueber_den_Tag_Index()
    {
        // 0.2.30: vorher „SCAN ft“ (alle FileTags-Zeilen per Join gegen Tags), jetzt TagId IN (passende Tags) über IX_FileTags_Tag.
        foreach (var count in new[] { true, false })
        {
            var plan = await _source.ExplainAsync(Query(search: "urlaub"), count, CancellationToken.None);
            var joined = string.Join(" | ", plan);
            plan.Should().Contain(l => l.StartsWith("SEARCH ft USING INDEX IX_FileTags_Tag (TagId=?)", StringComparison.Ordinal),
                $"Tag-Zuordnungen über den Index (Plan: {joined})");
            plan.Should().NotContain(l => System.Text.RegularExpressions.Regex.IsMatch(l, "^SCAN (ft|f)( |$)"), $"kein Durchlauf (Plan: {joined})");
        }
    }

    [Fact]
    public async Task Suche_und_Tag_Filter_wirken_zusammen()
        => (await NamesAsync(Query(search: "urlaub", tagIds: [_familie.Id]))).Should().BeEquivalentTo("IMG_a.jpg", "IMG_b.jpg");

    [Fact]
    public async Task Zaehler_und_Page_stimmen_ueberein()
    {
        var query = Query(tagIds: [_urlaub.Id]);
        var count = await _source.GetTotalCountAsync(query, CancellationToken.None);
        var rows = await _source.LoadPageAsync(query, 0, 2, count, CancellationToken.None);
        count.Should().Be(3);
        rows.Should().HaveCount(2);
        var last = await _source.LoadPageAsync(query, 1, 2, count, CancellationToken.None);
        last.Should().ContainSingle("Reverse-Trick auf der letzten Seite liefert den Rest");
    }

    private GalleryQuery Query(string? search = null, IReadOnlyList<int>? tagIds = null, GalleryTagMode mode = GalleryTagMode.Any)
        => new(_libraryId, GalleryFilterState.None, search, Sort: GallerySort.NameAsc, TagIds: tagIds, TagMode: mode);

    private async Task<List<string>> NamesAsync(GalleryQuery query)
    {
        var count = await _source.GetTotalCountAsync(query, CancellationToken.None);
        var rows = await _source.LoadPageAsync(query, 0, 100, count, CancellationToken.None);
        rows.Should().HaveCount(count, "Zähl- und Page-SQL nutzen dieselben Bedingungen");
        return rows.Select(r => r.Name).ToList();
    }

    private sealed class TempPaths : IAppDataPaths
    {
        public TempPaths(string root)
        {
            AppDataRoot = root;
            LibraryDatabasePath = Path.Combine(root, "library.db");
            ThumbnailsRoot = Path.Combine(root, "thumbs");
            LogsRoot = Path.Combine(root, "logs");
            Directory.CreateDirectory(root);
        }

        public string AppDataRoot { get; }
        public string LibraryDatabasePath { get; }
        public string ThumbnailsRoot { get; }
        public string LogsRoot { get; }

        public string GetLibraryThumbnailsPath(long libraryId) => Path.Combine(ThumbnailsRoot, libraryId.ToString());

        public void EnsureDirectoriesExist()
        {
            Directory.CreateDirectory(AppDataRoot);
            Directory.CreateDirectory(ThumbnailsRoot);
            Directory.CreateDirectory(LogsRoot);
        }
    }
}
