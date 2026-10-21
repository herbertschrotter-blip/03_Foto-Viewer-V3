using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Domain.Entities;
using Lumora.Domain.Enums;
using Lumora.Gallery.Data;
using Lumora.Gallery.ViewModels;
using Lumora.Services.Database;

namespace Lumora.Wpf.Tests.Persons;

/// <summary>
/// Beziehung und Personen-Details in der Galerie-SQL (0.2.47, ADR-0045) gegen eine migrierte DB:
/// a = Anna (Schwester); b = Anna + Bert (Bruder); c = Vorschlag Clara (Schwester); d = Opa Franz (Großvater, Rufname);
/// e = keine Gesichter. Suche findet Vor-/Nachname, Rufname und Beziehung; Filter nach Beziehung ODER untereinander,
/// UND mit den Personen.
/// </summary>
public sealed class GalleryPageSourceRelationTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Lumora.Wpf.Tests", Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, int> _ids = new();
    private LibraryContextFactory _factory = null!;
    private GalleryPageSource _source = null!;
    private int _libraryId;
    private int _anna;
    private int _bert;
    private int _sister;
    private int _brother;
    private int _grandpa;

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
            foreach (var name in new[] { "a", "b", "c", "d", "e" })
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
        }

        using var connection = _factory.OpenSqliteConnection();
        Exec(connection, "INSERT INTO FilesFts(rowid, Name, Extension) SELECT Id, Name, Extension FROM Files;");
        _sister = Relation(connection, "Schwester");
        _brother = Relation(connection, "Bruder");
        _grandpa = Relation(connection, "Großvater");
        _anna = Person(connection, "Anna", "Zeller", null, _sister);
        _bert = Person(connection, "Bert", "Albrecht", null, _brother);
        var clara = Person(connection, "Clara", "Berger", null, _sister);
        var franz = Person(connection, "Franz", "Huber", "Opa Franz", _grandpa);
        FaceTestData.InsertFace(connection, _ids["a"], FaceState.Confirmed, personId: _anna);
        FaceTestData.InsertFace(connection, _ids["b"], FaceState.Confirmed, personId: _anna);
        FaceTestData.InsertFace(connection, _ids["b"], FaceState.Confirmed, personId: _bert, x: 0.6f);
        FaceTestData.InsertFace(connection, _ids["c"], FaceState.Suggested, suggestedPersonId: clara);
        FaceTestData.InsertFace(connection, _ids["d"], FaceState.Confirmed, personId: franz);

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
    public async Task Suche_findet_Vorname_Nachname_Rufname_und_Beziehung()
    {
        (await NamesAsync(Query(search: "huber"))).Should().BeEquivalentTo(["IMG_d.jpg"], "Nachname hinter dem Rufnamen");
        (await NamesAsync(Query(search: "franz"))).Should().BeEquivalentTo(["IMG_d.jpg"]);
        (await NamesAsync(Query(search: "Opa"))).Should().BeEquivalentTo(["IMG_d.jpg"], "Rufname");
        (await NamesAsync(Query(search: "zeller"))).Should().BeEquivalentTo("IMG_a.jpg", "IMG_b.jpg");
        (await NamesAsync(Query(search: "SCHWESTER"))).Should().BeEquivalentTo(["IMG_a.jpg", "IMG_b.jpg"], "Beziehung; Vorschläge zählen in der Suche nicht");
        (await NamesAsync(Query(search: "bruder"))).Should().BeEquivalentTo("IMG_b.jpg");
    }

    [Fact]
    public async Task Filter_nach_Beziehung_ODER_untereinander_UND_mit_Personen()
    {
        (await NamesAsync(Filter([], _sister))).Should().BeEquivalentTo("IMG_a.jpg", "IMG_b.jpg");
        (await NamesAsync(Filter([], _sister, _grandpa))).Should().BeEquivalentTo("IMG_a.jpg", "IMG_b.jpg", "IMG_d.jpg");
        (await NamesAsync(Filter([_bert], _sister))).Should().BeEquivalentTo(["IMG_b.jpg"], "Bert UND eine Schwester");
        (await NamesAsync(Filter([], _sister) with { PersonFilter = new GalleryPersonFilter([], GalleryPersonMode.Any, true, [_sister]) }))
            .Should().BeEquivalentTo(["IMG_a.jpg", "IMG_b.jpg", "IMG_c.jpg"], "mit Vorschlägen zählt Claras Vorschlag");
        (await NamesAsync(Filter([], _brother) with { SearchTerm = "IMG_a" })).Should().BeEmpty("UND mit der Suche");
        (await _source.GetTotalCountAsync(Filter([], _sister), CancellationToken.None)).Should().Be(2);
        new GalleryPersonFilter([], GalleryPersonMode.Any, false, [_sister]).Should().Match<GalleryPersonFilter>(f => f.IsActive && !f.HasPersonPart && f.HasRelationPart);
    }

    [Fact]
    public async Task Beziehungen_mit_Anzahl_und_Suchtext_der_Personen()
    {
        var relations = await _source.GetRelationCountsAsync(_libraryId, CancellationToken.None);
        relations.Select(r => (r.Name, r.FileCount)).Should().Equal(("Bruder", 1), ("Großvater", 1), ("Schwester", 2));

        var persons = await _source.GetPersonCountsAsync(_libraryId, CancellationToken.None);
        persons.Select(p => p.Name).Should().Equal("Anna Zeller", "Bert Albrecht", "Clara Berger", "Opa Franz");
        persons.Single(p => p.Name == "Opa Franz").SearchText.Should().Contain("Huber").And.Contain("Großvater");
        new Lumora.Gallery.People.PersonFilterEntry(1, "X", 0, null).SearchText.Should().Be("X");

        var rows = await _source.GetPersonsForFilesAsync([_ids["d"]], CancellationToken.None);
        GalleryItemPersons.From(rows).ConfirmedText.Should().Be("Opa Franz", "Listenspalte zeigt den Anzeigenamen");
    }

    private GalleryQuery Query(string? search = null)
        => new(_libraryId, GalleryFilterState.None, search, Sort: GallerySort.NameAsc);

    private GalleryQuery Filter(int[] persons, params int[] relations)
        => Query() with { PersonFilter = new GalleryPersonFilter(persons, GalleryPersonMode.Any, false, relations) };

    private async Task<List<string>> NamesAsync(GalleryQuery query)
        => (await _source.LoadPageAsync(query, 0, 50, 0, CancellationToken.None)).Select(r => r.Name).ToList();

    private static int Relation(SqliteConnection connection, string name)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO PersonRelations (Name) VALUES ($n); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$n", name);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static int Person(SqliteConnection connection, string first, string last, string? nick, int relation)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Persons (Name, CreatedAtUtc, CoverFaceId, IsHidden, FirstName, LastName, Nickname, RelationId, IsMe)
            VALUES ($n, 1, NULL, 0, $f, $l, $k, $r, 0); SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$n", PersonNames.DisplayName(first, last, nick));
        cmd.Parameters.AddWithValue("$f", first);
        cmd.Parameters.AddWithValue("$l", last);
        cmd.Parameters.AddWithValue("$k", (object?)nick ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$r", relation);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private sealed class TempPaths(string root) : Lumora.Services.Infrastructure.IAppDataPaths
    {
        public string AppDataRoot => root;
        public string LibraryDatabasePath => Path.Combine(root, "library.db");
        public string ThumbnailsRoot => Path.Combine(root, "thumbs");
        public string LogsRoot => Path.Combine(root, "logs");
        public string GetLibraryThumbnailsPath(long libraryId) => Path.Combine(ThumbnailsRoot, libraryId.ToString());
        public void EnsureDirectoriesExist() => Directory.CreateDirectory(root);
    }
}
