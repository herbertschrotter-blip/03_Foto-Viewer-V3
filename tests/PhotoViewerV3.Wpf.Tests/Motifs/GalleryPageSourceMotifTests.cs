using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoViewerV3.Domain.Entities;
using PhotoViewerV3.Domain.Enums;
using PhotoViewerV3.Gallery.Data;
using PhotoViewerV3.Services.Database;

namespace PhotoViewerV3.Wpf.Tests.Motifs;

/// <summary>
/// Motive in der Galerie-SQL (ADR-0052) gegen eine echte, migrierte DB: a = Hund; b = Hund + Strand; c = Strand;
/// d = nichts; zweite Bibliothek mit eigenem Begriff „Hund“ und Treffer auf x. Filter ODER/UND, Suche nach dem Begriff
/// (UNION-Zweig, nur Begriffe des Bereichs), „Motiv: …“, Zähler, Treffer je Datei, Abfrageplan über den Index.
/// </summary>
public sealed class GalleryPageSourceMotifTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PhotoViewerV3.Wpf.Tests", Guid.NewGuid().ToString("N"));
    private LibraryContextFactory _factory = null!;
    private GalleryPageSource _source = null!;
    private int _libraryId;
    private int _otherLibraryId;
    private int _dog;
    private int _beach;
    private int _otherDog;
    private readonly Dictionary<string, int> _ids = new();

    public async Task InitializeAsync()
    {
        var paths = new TempPaths(_root);
        paths.EnsureDirectoriesExist();
        _factory = new LibraryContextFactory(paths, new SqlitePragmaInterceptor());
        await new DatabaseInitializer(_factory, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();

        await using (var ctx = _factory.Create())
        {
            foreach (var (libName, root, files) in new[] { ("T", @"C:\Fotos", new[] { "a", "b", "c", "d" }), ("U", @"C:\Andere", new[] { "x" }) })
            {
                var library = new Library { Name = libName, RootPath = root, CreatedAt = DateTime.UtcNow };
                ctx.Libraries.Add(library);
                await ctx.SaveChangesAsync();
                var folder = new Folder { LibraryId = library.Id, Path = root, Name = libName, ScannedAt = DateTime.UtcNow };
                ctx.Folders.Add(folder);
                await ctx.SaveChangesAsync();
                foreach (var name in files)
                {
                    var file = new MediaFile
                    {
                        LibraryId = library.Id, FolderId = folder.Id, Name = $"IMG_{name}.jpg", Extension = ".jpg", Size = 1000,
                        CreatedAtUtc = DateTime.UtcNow, ModifiedAtUtc = DateTime.UtcNow, MediaType = MediaType.Image,
                    };
                    ctx.Files.Add(file);
                    await ctx.SaveChangesAsync();
                    _ids[name] = file.Id;
                }

                if (libName == "T")
                {
                    _libraryId = library.Id;
                }
                else
                {
                    _otherLibraryId = library.Id;
                }
            }
        }

        using var connection = _factory.OpenSqliteConnection();
        Exec(connection, "INSERT INTO FilesFts(rowid, Name, Extension) SELECT Id, Name, Extension FROM Files;");
        _dog = InsertTerm(connection, _libraryId, "Hund", 0);
        _beach = InsertTerm(connection, _libraryId, "Strand", 1);
        InsertTerm(connection, _libraryId, "Katze", 2);
        _otherDog = InsertTerm(connection, _otherLibraryId, "Hund", 0);
        Exec(connection, $"INSERT INTO FileMotifs (FileId, TermId, Score) VALUES ({_ids["a"]}, {_dog}, 0.05), ({_ids["b"]}, {_dog}, 0.02), ({_ids["b"]}, {_beach}, 0.2), ({_ids["c"]}, {_beach}, 0.03), ({_ids["x"]}, {_otherDog}, 0.5);");

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
    public async Task Filter_eines_davon_und_alle_zusammen()
    {
        (await NamesAsync(Motifs(GalleryMotifMode.Any, _dog))).Should().Equal("IMG_a.jpg", "IMG_b.jpg");
        (await NamesAsync(Motifs(GalleryMotifMode.Any, _dog, _beach))).Should().Equal("IMG_a.jpg", "IMG_b.jpg", "IMG_c.jpg");
        (await NamesAsync(Motifs(GalleryMotifMode.All, _dog, _beach))).Should().Equal("IMG_b.jpg");
        (await _source.GetTotalCountAsync(Motifs(GalleryMotifMode.All, _dog, _beach), CancellationToken.None)).Should().Be(1);
        (await NamesAsync(Motifs(GalleryMotifMode.Any, _otherDog))).Should().BeEmpty("Begriff der anderen Bibliothek trifft hier nichts");
        Query().HasMotifFilter.Should().BeFalse();
        Motifs(GalleryMotifMode.Any, _dog).HasMotifFilter.Should().BeTrue();
    }

    [Fact]
    public async Task Filter_wirkt_mit_Suche_und_Datei_Filter_zusammen()
    {
        (await NamesAsync(Motifs(GalleryMotifMode.Any, _beach) with { SearchTerm = "IMG_c" })).Should().Equal("IMG_c.jpg");
        (await NamesAsync(Motifs(GalleryMotifMode.Any, _beach) with { Filter = new GalleryFilterState(GalleryMediaKind.Videos) })).Should().BeEmpty();
    }

    [Fact]
    public async Task Suche_findet_Motive_nur_aus_dem_eigenen_Bereich()
    {
        (await NamesAsync(Query("strand"))).Should().Equal("IMG_b.jpg", "IMG_c.jpg");
        (await NamesAsync(Query("hun"))).Should().Equal(new[] { "IMG_a.jpg", "IMG_b.jpg" }, "Teilwort wie bei Tags");
        (await NamesAsync(Query("hund") with { LibraryId = _otherLibraryId })).Should().Equal("IMG_x.jpg");
        (await NamesAsync(Query("IMG_d"))).Should().Equal(new[] { "IMG_d.jpg" }, "Dateinamen-Suche bleibt");
    }

    [Fact]
    public async Task Motiv_Praefix_sucht_nur_Motive()
    {
        Query("Motiv: Strand").MotifOnlySearchTerm.Should().Be("Strand");
        Query("motiv:hund").MotifOnlySearchTerm.Should().Be("hund");
        Query("Motivation").MotifOnlySearchTerm.Should().BeNull();
        Query("Motiv:").MotifOnlySearchTerm.Should().BeNull();
        (await NamesAsync(Query("Motiv: Strand"))).Should().Equal("IMG_b.jpg", "IMG_c.jpg");
        (await NamesAsync(Query("motiv: img"))).Should().BeEmpty("kein Dateiname, nur Motive");
        (await _source.GetTotalCountAsync(Query("Motiv: hund"), CancellationToken.None)).Should().Be(2);
    }

    [Fact]
    public async Task Bildinhalt_Suche_schraenkt_auf_die_gefundenen_Dateien_ein()
    {
        (await NamesAsync(Query() with { ContentSearchIds = [_ids["c"], _ids["a"]] })).Should().Equal("IMG_a.jpg", "IMG_c.jpg");
        (await NamesAsync(Query() with { ContentSearchIds = [] })).Should().BeEmpty("nichts ähnlich genug");
        (await NamesAsync(Motifs(GalleryMotifMode.Any, _beach) with { ContentSearchIds = [_ids["a"], _ids["b"]] })).Should().Equal(new[] { "IMG_b.jpg" }, "UND mit dem Motiv-Filter");
        (await NamesAsync(Query() with { ContentSearchIds = [_ids["x"]] })).Should().BeEmpty("Datei einer anderen Bibliothek");
        (Query() with { ContentSearchIds = [] }).HasMotifFilter.Should().BeTrue();
    }

    [Fact]
    public async Task Zaehler_je_Begriff_und_Motive_je_Datei()
    {
        var counts = await _source.GetMotifCountsAsync(_libraryId, CancellationToken.None);
        counts.Select(c => (c.Name, c.FileCount)).Should().Equal(("Hund", 2), ("Strand", 2), ("Katze", 0));
        (await _source.GetMotifCountsAsync(_otherLibraryId, CancellationToken.None)).Select(c => (c.Name, c.FileCount)).Should().Equal(("Hund", 1));

        var rows = await _source.GetMotifsForFilesAsync([_ids["b"], _ids["d"]], CancellationToken.None);
        rows.Where(r => r.FileId == _ids["b"]).Select(r => r.Name).Should().Equal("Strand", "Hund"); // wahrscheinlichste zuerst
        rows.Should().NotContain(r => r.FileId == _ids["d"]);
    }

    [Fact]
    public async Task Filter_und_Suche_lesen_Treffer_ueber_den_Index()
    {
        foreach (var count in new[] { true, false })
        {
            await AssertPlanAsync(Motifs(GalleryMotifMode.Any, _dog, _beach), count, "IX_FileMotifs_Term_File");
            await AssertPlanAsync(Motifs(GalleryMotifMode.All, _dog, _beach), count, "IX_FileMotifs_Term_File");
            await AssertPlanAsync(Query("hund"), count, "IX_FileMotifs_Term_File");
            await AssertPlanAsync(Query("Motiv: hund"), count, "IX_FileMotifs_Term_File");
        }
    }

    private async Task AssertPlanAsync(GalleryQuery query, bool count, string expected)
    {
        var plan = await _source.ExplainAsync(query, count, CancellationToken.None);
        var joined = string.Join(" | ", plan);
        plan.Should().Contain(l => l.Contains(expected, StringComparison.Ordinal), $"{expected} (Plan: {joined})");
        plan.Should().NotContain(l => Regex.IsMatch(l, "^SCAN fm( |$)"), $"kein Durchlauf aller Treffer (Plan: {joined})");
    }

    private GalleryQuery Motifs(GalleryMotifMode mode, params int[] ids) => Query() with { MotifFilter = new GalleryMotifFilter(ids, mode) };

    private GalleryQuery Query(string? search = null) => new(_libraryId, GalleryFilterState.None, search, Sort: GallerySort.NameAsc);

    private async Task<IReadOnlyList<string>> NamesAsync(GalleryQuery query)
    {
        var rows = await _source.LoadPageAsync(query, 0, 50, 0, CancellationToken.None);
        return rows.Select(r => r.Name).ToList();
    }

    private static int InsertTerm(SqliteConnection connection, int libraryId, string name, int sort)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO MotifTerms (LibraryId, Name, Prompt, Threshold, SortOrder, CreatedAtUtc) VALUES ($lib, $name, NULL, NULL, $sort, 0); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$lib", libraryId);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$sort", sort);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

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
