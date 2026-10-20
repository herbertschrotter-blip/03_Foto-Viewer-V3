using System.IO;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoViewerV3.Gallery.Data;
using PhotoViewerV3.Services.Categories;
using PhotoViewerV3.Services.Database;
using PhotoViewerV3.Services.Infrastructure;

namespace PhotoViewerV3.Wpf.Tests.Categories;

/// <summary>
/// Segmentweise Kategorie-Sortierung (ADR-0031) gegen eine echte, migrierte DB: Über beliebige Seitengrößen ergibt das
/// Blättern genau die Reihenfolge der bisherigen Ein-Abfrage-Sortierung (0.2.23: ohne Wert zuletzt, SortOrder/Id des
/// Werts, Datum neu → alt, Id als Zweitsortierung). Katalog: 61 Dateien in Bibliothek 1 (viele gleiche Änderungsdaten),
/// 20 in Bibliothek 2; Kategorie „Stufe“ mit drei Werten in umsortierter Reihenfolge, dazu eine zweite Kategorie.
/// </summary>
public sealed class GalleryCategorySortPagingTests : IAsyncLifetime
{
    private const int LibraryFiles = 61;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PhotoViewerV3.Wpf.Tests", Guid.NewGuid().ToString("N"));
    private LibraryContextFactory _factory = null!;
    private GalleryPageSource _source = null!;
    private CategoryService _categories = null!;
    private CategoryInfo _stufe = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _factory = new LibraryContextFactory(new AppDataPaths(_root), new SqlitePragmaInterceptor());
        await new DatabaseInitializer(_factory, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();

        using (var connection = _factory.OpenSqliteConnection())
        {
            Exec(connection, "INSERT INTO Libraries (Id, Name, RootPath, CreatedAt, LastOpenedAt) VALUES (1, 'A', 'C:\\A', 1, 1), (2, 'B', 'C:\\B', 1, 1);");
            Exec(connection, "INSERT INTO Folders (Id, LibraryId, Path, ParentId, Name, ScannedAt) VALUES (1, 1, 'C:\\A', NULL, 'A', 1), (2, 2, 'C:\\B', NULL, 'B', 1);");
            for (var i = 1; i <= LibraryFiles + 20; i++)
            {
                var library = i <= LibraryFiles ? 1 : 2;
                var name = i % 4 == 0 ? $"Haus_{i:D3}.jpg" : $"IMG_{i:D3}.jpg";
                Exec(connection,
                    "INSERT INTO Files (Id, LibraryId, FolderId, Name, Extension, Size, CreatedAtUtc, ModifiedAtUtc, MediaType, IsFavorite, Missing) " +
                    $"VALUES ({i}, {library}, {library}, '{name}', '.jpg', {i}, 0, {1000 + i * 37 % 7}, 0, {(i % 3 == 0 ? 1 : 0)}, 0);");
            }

            Exec(connection, "INSERT INTO FilesFts(rowid, Name, Extension) SELECT Id, Name, Extension FROM Files;");
        }

        _categories = new CategoryService(_factory, NullLogger<CategoryService>.Instance, new WeakReferenceMessenger());
        _stufe = await _categories.CreateAsync("Stufe", ["A", "B", "C"]);
        await _categories.ReorderValuesAsync(_stufe.Id, [_stufe.Values[2].Id, _stufe.Values[0].Id, _stufe.Values[1].Id]);
        var andere = await _categories.CreateAsync("Andere", ["X"]);
        var all = Enumerable.Range(1, LibraryFiles + 20).ToList();
        await _categories.SetValueAsync(all.Where(i => i % 5 is 0 or 1).ToList(), _stufe.Id, _stufe.Values[0].Id);
        await _categories.SetValueAsync(all.Where(i => i % 5 == 2).ToList(), _stufe.Id, _stufe.Values[1].Id);
        await _categories.SetValueAsync(all.Where(i => i % 5 == 3).ToList(), _stufe.Id, _stufe.Values[2].Id);
        await _categories.SetValueAsync(all.Where(i => i % 2 == 0).ToList(), andere.Id, andere.Values[0].Id);

        _source = new GalleryPageSource(_factory);
        _source.Open();
    }

    public Task DisposeAsync()
    {
        _source.Dispose();
        _categories.Dispose();
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

    public static TheoryData<int, bool> PageSizes()
    {
        var data = new TheoryData<int, bool>();
        foreach (var size in new[] { 1, 4, 7, 12, 13, 60, 61, 100 })
        {
            data.Add(size, false);
            data.Add(size, true);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(PageSizes))]
    public async Task Blaettern_ergibt_ueber_alle_Seitengrenzen_die_bisherige_Reihenfolge(int pageSize, bool descending)
    {
        var query = new GalleryQuery(1, GalleryFilter.All, null, CategorySort: new GalleryCategorySort(_stufe.Id, descending));
        var expected = Reference(descending, string.Empty);
        expected.Should().HaveCount(LibraryFiles, "nur Dateien der Bibliothek 1");
        (await PagedIdsAsync(query, pageSize)).Should().Equal(expected);
    }

    [Theory]
    [InlineData(5, false)]
    [InlineData(5, true)]
    [InlineData(3, true)]
    public async Task Blaettern_mit_Filter_und_mit_Suche_ergibt_die_bisherige_Reihenfolge(int pageSize, bool descending)
    {
        var sort = new GalleryCategorySort(_stufe.Id, descending);
        var favorites = new GalleryQuery(1, GalleryFilter.Favorites, null, CategorySort: sort);
        (await PagedIdsAsync(favorites, pageSize)).Should().Equal(Reference(descending, "AND f.IsFavorite = 1"));

        var search = new GalleryQuery(1, GalleryFilter.All, "haus", CategorySort: sort);
        var expectedSearch = Reference(descending, "AND f.Name LIKE 'Haus%'");
        expectedSearch.Should().NotBeEmpty();
        (await PagedIdsAsync(search, pageSize)).Should().Equal(expectedSearch);
    }

    [Fact]
    public async Task Gleiche_Werte_stehen_nach_Datum_neu_vor_alt_und_bei_gleichem_Datum_hoehere_Id_zuerst()
    {
        var ids = await PagedIdsAsync(new GalleryQuery(1, GalleryFilter.All, null, CategorySort: new GalleryCategorySort(_stufe.Id, false)), 9);
        var modified = Lookup("SELECT Id, ModifiedAtUtc FROM Files");
        var value = Lookup($"SELECT FileId, ValueId FROM FileCategoryValues WHERE CategoryId = {_stufe.Id}");
        for (var i = 1; i < ids.Count; i++)
        {
            var (prev, cur) = (ids[i - 1], ids[i]);
            if (value.GetValueOrDefault(prev) == value.GetValueOrDefault(cur))
            {
                (modified[prev] > modified[cur] || (modified[prev] == modified[cur] && prev > cur))
                    .Should().BeTrue($"{prev} vor {cur} im selben Segment");
            }
        }

        value.GetValueOrDefault(ids[0]).Should().Be(_stufe.Values[2].Id, "C steht in der Verwaltung an erster Stelle");
        value.ContainsKey(ids[^1]).Should().BeFalse("ohne Wert zuletzt");
    }

    [Fact]
    public async Task Zuweisung_zwischen_zwei_Seiten_wird_beim_naechsten_Laden_beruecksichtigt()
    {
        var query = new GalleryQuery(1, GalleryFilter.All, null, CategorySort: new GalleryCategorySort(_stufe.Id, false));
        var count = await _source.GetTotalCountAsync(query, CancellationToken.None);
        await _source.LoadPageAsync(query, 0, 10, count, CancellationToken.None);

        // Gesamtzahl bleibt gleich, nur die Segmentgrößen ändern sich — zwischengespeicherte Zählungen wären jetzt falsch.
        var without = Reference(false, string.Empty).Where(id => id % 5 == 4).Take(6).ToList();
        await _categories.SetValueAsync(without, _stufe.Id, _stufe.Values[2].Id);

        (await PagedIdsAsync(query, 10)).Should().Equal(Reference(false, string.Empty), "keine doppelten oder fehlenden Dateien an Segmentgrenzen");
    }

    [Fact]
    public async Task Neue_Datei_nach_dem_Zaehlen_erscheint_ohne_erneutes_Zaehlen()
    {
        var query = new GalleryQuery(1, GalleryFilter.All, null, CategorySort: new GalleryCategorySort(_stufe.Id, false));
        var count = await _source.GetTotalCountAsync(query, CancellationToken.None);
        using (var connection = _factory.OpenSqliteConnection())
        {
            Exec(connection,
                "INSERT INTO Files (Id, LibraryId, FolderId, Name, Extension, Size, CreatedAtUtc, ModifiedAtUtc, MediaType, IsFavorite, Missing) " +
                "VALUES (500, 1, 1, 'Neu.jpg', '.jpg', 1, 0, 1, 0, 0, 0);");
        }

        // Die gemerkte Gesamtzahl gilt nicht mehr (fremder Commit) — „ohne Wert“ wächst um die neue Datei.
        var rows = await _source.LoadPageAsync(query, 0, 100, count, CancellationToken.None);
        rows.Select(r => r.Id).Should().Equal(Reference(false, string.Empty));
        rows[^1].Id.Should().Be(500, "ohne Wert und ältestes Datum → ganz am Ende");
    }

    [Fact]
    public async Task Werte_ohne_Dateien_und_Seiten_hinter_dem_Ende_bleiben_leer()
    {
        var leer = await _categories.CreateAsync("Leer", ["L1", "L2"]);
        var query = new GalleryQuery(1, GalleryFilter.All, null, CategorySort: new GalleryCategorySort(leer.Id, true));
        var modified = Lookup("SELECT Id, ModifiedAtUtc FROM Files");
        (await PagedIdsAsync(query, 25)).Should().Equal(Enumerable.Range(1, LibraryFiles).OrderByDescending(id => modified[id]).ThenByDescending(id => id),
            "ohne Werte ist alles „ohne Wert“ in der Standard-Reihenfolge");
        (await _source.LoadPageAsync(query, 10, 25, LibraryFiles, CancellationToken.None)).Should().BeEmpty();
    }

    private async Task<List<int>> PagedIdsAsync(GalleryQuery query, int pageSize)
    {
        var count = await _source.GetTotalCountAsync(query, CancellationToken.None);
        var ids = new List<int>();
        for (var page = 0; (long)page * pageSize < count; page++)
        {
            var rows = await _source.LoadPageAsync(query, page, pageSize, count, CancellationToken.None);
            rows.Should().HaveCount(Math.Min(pageSize, count - page * pageSize), $"Seite {page}");
            ids.AddRange(rows.Select(r => r.Id));
        }

        return ids;
    }

    /// <summary>Reihenfolge der bisherigen Ein-Abfrage-Sortierung (0.2.23) als Referenz.</summary>
    private List<int> Reference(bool descending, string where)
    {
        var dir = descending ? "DESC" : "ASC";
        return Ids($"""
            SELECT f.Id FROM Files f
            LEFT JOIN FileCategoryValues scv ON scv.FileId = f.Id AND scv.CategoryId = {_stufe.Id}
            LEFT JOIN CategoryValues sv ON sv.Id = scv.ValueId
            WHERE f.LibraryId = 1 {where}
            ORDER BY (sv.Id IS NULL) ASC, sv.SortOrder {dir}, sv.Id {dir}, f.ModifiedAtUtc DESC, f.Id DESC;
            """);
    }

    private List<int> Ids(string sql)
    {
        using var connection = _factory.OpenSqliteConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var ids = new List<int>();
        while (reader.Read())
        {
            ids.Add(reader.GetInt32(0));
        }

        return ids;
    }

    private Dictionary<int, long> Lookup(string sql)
    {
        using var connection = _factory.OpenSqliteConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var map = new Dictionary<int, long>();
        while (reader.Read())
        {
            map[reader.GetInt32(0)] = reader.GetInt64(1);
        }

        return map;
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
