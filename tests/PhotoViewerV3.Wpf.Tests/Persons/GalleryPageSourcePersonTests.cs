using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoViewerV3.Domain.Entities;
using PhotoViewerV3.Domain.Enums;
using PhotoViewerV3.Gallery.Data;
using PhotoViewerV3.Gallery.ViewModels;
using PhotoViewerV3.Services.Database;

namespace PhotoViewerV3.Wpf.Tests.Persons;

/// <summary>
/// Personen in der Galerie-SQL (0.2.38) gegen eine echte, migrierte DB mit Fake-Gesichtern:
/// a = Anna; b = Anna + Bert (+ zweites Gesicht mit Vorschlag Bert); c = Vorschlag Bert; d = unbekanntes Gesicht;
/// e = keine Gesichter; f = ignoriertes Gesicht; g = Anna + unbekanntes Gesicht; VID_v = Video. Carla ist ausgeblendet
/// und nirgends.
/// </summary>
public sealed class GalleryPageSourcePersonTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PhotoViewerV3.Wpf.Tests", Guid.NewGuid().ToString("N"));
    private LibraryContextFactory _factory = null!;
    private GalleryPageSource _source = null!;
    private int _libraryId;
    private int _anna;
    private int _bert;
    private int _carla;
    private readonly Dictionary<string, int> _ids = new();

    public async Task InitializeAsync()
    {
        var paths = new TempPaths(_root);
        paths.EnsureDirectoriesExist();
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
            foreach (var name in new[] { "a", "b", "c", "d", "e", "f", "g" })
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

            ctx.Files.Add(new MediaFile
            {
                LibraryId = library.Id, FolderId = folder.Id, Name = "VID_v.mp4", Extension = ".mp4", Size = 1000,
                CreatedAtUtc = DateTime.UtcNow, ModifiedAtUtc = DateTime.UtcNow, MediaType = MediaType.Video,
            });
            await ctx.SaveChangesAsync();
        }

        using (var connection = _factory.OpenSqliteConnection())
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO FilesFts(rowid, Name, Extension) SELECT Id, Name, Extension FROM Files;";
                cmd.ExecuteNonQuery();
            }

            _anna = FaceTestData.InsertPerson(connection, "Anna Muster");
            _bert = FaceTestData.InsertPerson(connection, "Bert");
            _carla = FaceTestData.InsertPerson(connection, "Carla", hidden: true);
            FaceTestData.InsertFace(connection, _ids["a"], FaceState.Confirmed, personId: _anna);
            FaceTestData.InsertFace(connection, _ids["b"], FaceState.Confirmed, personId: _anna);
            FaceTestData.InsertFace(connection, _ids["b"], FaceState.Confirmed, personId: _bert, x: 0.6f);
            FaceTestData.InsertFace(connection, _ids["b"], FaceState.Suggested, suggestedPersonId: _bert, x: 0.8f);
            FaceTestData.InsertFace(connection, _ids["c"], FaceState.Suggested, suggestedPersonId: _bert);
            FaceTestData.InsertFace(connection, _ids["d"], FaceState.Unconfirmed);
            FaceTestData.InsertFace(connection, _ids["f"], FaceState.Ignored);
            FaceTestData.InsertFace(connection, _ids["g"], FaceState.Confirmed, personId: _anna);
            FaceTestData.InsertFace(connection, _ids["g"], FaceState.Unconfirmed, x: 0.6f);
        }

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
    public async Task ODER_findet_Fotos_mit_mindestens_einer_bestaetigten_Person()
    {
        (await NamesAsync(Persons(GalleryPersonMode.Any, false, _anna, _bert))).Should().BeEquivalentTo("IMG_a.jpg", "IMG_b.jpg", "IMG_g.jpg");
        (await NamesAsync(Persons(GalleryPersonMode.Any, false, _bert))).Should().BeEquivalentTo(["IMG_b.jpg"], "ein Vorschlag zählt ohne Option nicht");
    }

    [Fact]
    public async Task ODER_mit_Vorschlaegen_nimmt_vorgeschlagene_Fotos_dazu()
        => (await NamesAsync(Persons(GalleryPersonMode.Any, true, _bert))).Should().BeEquivalentTo("IMG_b.jpg", "IMG_c.jpg");

    [Fact]
    public async Task UND_findet_nur_Fotos_mit_allen_Personen()
    {
        (await NamesAsync(Persons(GalleryPersonMode.All, false, _anna, _bert))).Should().BeEquivalentTo("IMG_b.jpg");
        (await NamesAsync(Persons(GalleryPersonMode.All, true, _anna, _bert))).Should().BeEquivalentTo("IMG_b.jpg");
        (await NamesAsync(Persons(GalleryPersonMode.All, false, _anna, _carla))).Should().BeEmpty();
    }

    [Fact]
    public async Task Ohne_Person_findet_Fotos_ohne_bestaetigtes_Gesicht()
    {
        (await NamesAsync(Persons(GalleryPersonMode.NoPerson, false, _anna))).Should().BeEquivalentTo(
            ["IMG_c.jpg", "IMG_d.jpg", "IMG_e.jpg", "IMG_f.jpg"], "gewählte Personen werden ignoriert, ein Vorschlag ist keine Person, Videos zählen nicht");
        (await NamesAsync(Persons(GalleryPersonMode.NoPerson, true))).Should().BeEquivalentTo("IMG_d.jpg", "IMG_e.jpg", "IMG_f.jpg");
    }

    [Fact]
    public async Task Unbenannte_Gesichter_findet_Fotos_mit_offenem_Gesicht()
    {
        (await NamesAsync(Persons(GalleryPersonMode.UnnamedFaces, false))).Should().BeEquivalentTo(
            ["IMG_b.jpg", "IMG_c.jpg", "IMG_d.jpg", "IMG_g.jpg"], "Vorschläge sind noch unbenannt, ignorierte nicht");
        (await NamesAsync(Persons(GalleryPersonMode.UnnamedFaces, true))).Should().BeEquivalentTo("IMG_d.jpg", "IMG_g.jpg");
    }

    [Fact]
    public async Task Zaehlung_passt_zur_Seite()
    {
        var query = Persons(GalleryPersonMode.Any, true, _anna, _bert);
        (await _source.GetTotalCountAsync(query, CancellationToken.None)).Should().Be(4);
        (await _source.GetTotalCountAsync(Persons(GalleryPersonMode.UnnamedFaces, false), CancellationToken.None)).Should().Be(4);
    }

    [Fact]
    public async Task Suche_findet_Personennamen_als_Teilstring_ohne_Gross_Kleinschreibung()
    {
        (await NamesAsync(Query(search: "muster"))).Should().BeEquivalentTo("IMG_a.jpg", "IMG_b.jpg", "IMG_g.jpg");
        (await NamesAsync(Query(search: "BERT"))).Should().BeEquivalentTo(["IMG_b.jpg"], "nur bestätigte Gesichter");
        (await NamesAsync(Query(search: "IMG_e"))).Should().BeEquivalentTo("IMG_e.jpg");
    }

    [Fact]
    public async Task Personen_Filter_mit_Kategorie_Sortierung_und_Suche_kombiniert()
    {
        var sorted = Query(search: "img") with
        {
            PersonFilter = new GalleryPersonFilter([_anna], GalleryPersonMode.Any),
            CategorySort = new GalleryCategorySort(999, false),
        };
        var rows = await _source.LoadPageAsync(sorted, 0, 50, 0, CancellationToken.None);
        rows.Select(r => r.Name).Should().BeEquivalentTo("IMG_a.jpg", "IMG_b.jpg", "IMG_g.jpg");
    }

    [Fact]
    public async Task Personen_je_Datei_bestaetigt_und_Vorschlag_ohne_Doppelte()
    {
        var rows = await _source.GetPersonsForFilesAsync(_ids.Values.ToList(), CancellationToken.None);
        GalleryItemPersons Of(string name) => GalleryItemPersons.From(rows.Where(r => r.FileId == _ids[name]));

        Of("b").Confirmed.Should().Equal("Anna Muster", "Bert");
        Of("b").Suggested.Should().BeEmpty("Bert ist auf diesem Foto schon bestätigt");
        Of("c").Confirmed.Should().BeEmpty();
        Of("c").Suggested.Should().Equal("Bert");
        Of("c").SuggestionsText.Should().Be("Bert?");
        Of("d").Should().BeSameAs(GalleryItemPersons.Empty);
        Of("g").ConfirmedText.Should().Be("Anna Muster");
    }

    [Fact]
    public async Task Personen_mit_Anzahl_der_Fotos_dieser_Bibliothek()
    {
        var counts = await _source.GetPersonCountsAsync(_libraryId, CancellationToken.None);
        counts.Select(c => (c.Name, c.FileCount, c.IsHidden)).Should().Equal(("Anna Muster", 3, false), ("Bert", 1, false), ("Carla", 0, true));
        (await _source.GetPersonCountsAsync(_libraryId + 99, CancellationToken.None)).Should().OnlyContain(c => c.FileCount == 0);
        (await _source.HasFacesAsync(_libraryId, CancellationToken.None)).Should().BeTrue();
        (await _source.HasFacesAsync(_libraryId + 99, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task Filter_und_Suche_lesen_Gesichter_ueber_abdeckende_Indizes()
    {
        foreach (var count in new[] { true, false })
        {
            await AssertPlanAsync(Persons(GalleryPersonMode.Any, true, _anna, _bert), count,
                "USING COVERING INDEX IX_Faces_Person_State_File", "USING COVERING INDEX IX_Faces_SuggestedPerson_State_File");
            await AssertPlanAsync(Persons(GalleryPersonMode.All, false, _anna, _bert), count, "USING COVERING INDEX IX_Faces_Person_State_File");
            await AssertPlanAsync(Persons(GalleryPersonMode.NoPerson, false), count, "USING COVERING INDEX IX_Faces_File_State_Person");
            await AssertPlanAsync(Persons(GalleryPersonMode.UnnamedFaces, true), count, "USING COVERING INDEX IX_Faces_File_State_Person");
            await AssertPlanAsync(Query(search: "anna"), count, "USING COVERING INDEX IX_Faces_Person_State_File");
        }
    }

    private async Task AssertPlanAsync(GalleryQuery query, bool count, params string[] expected)
    {
        var plan = await _source.ExplainAsync(query, count, CancellationToken.None);
        var joined = string.Join(" | ", plan);
        foreach (var part in expected)
        {
            plan.Should().Contain(l => l.Contains(part, StringComparison.Ordinal), $"{part} (Plan: {joined})");
        }

        plan.Should().NotContain(l => Regex.IsMatch(l, "^SCAN (fa|fs|fp)( |$)"), $"kein Durchlauf aller Gesichter (Plan: {joined})");
    }

    private GalleryQuery Persons(GalleryPersonMode mode, bool suggestions, params int[] ids)
        => Query() with { PersonFilter = new GalleryPersonFilter(ids, mode, suggestions) };

    private GalleryQuery Query(string? search = null)
        => new(_libraryId, GalleryFilter.All, search, Sort: GallerySort.NameAsc);

    private async Task<IReadOnlyList<string>> NamesAsync(GalleryQuery query)
    {
        var rows = await _source.LoadPageAsync(query, 0, 50, 0, CancellationToken.None);
        return rows.Select(r => r.Name).ToList();
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
